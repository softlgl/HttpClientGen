using System.Text;
using HttpClientGen.Core.Models;
using Microsoft.OpenApi;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Microsoft.OpenApi.Readers;

namespace HttpClientGen.Core.Parsing;

/// <summary>
/// 解析 OpenAPI/Swagger 文档（支持 2.0 / 3.x），映射为中间模型 <see cref="ApiCatalog"/>。
/// </summary>
public static class OpenApiParser
{
    public static ApiCatalog Parse(string jsonContent, string targetNamespace, string? baseUrl, string? forcedScheme)
    {
        // reader(1.x) 仅支持 2.0 / 3.0：把 OpenAPI 3.1 文档降声明为 3.0 再解析（结构本身兼容）
        var reader = new OpenApiStringReader(new OpenApiReaderSettings());
        var doc = reader.Read(NormalizeVersion(jsonContent), out _);

        var catalog = new ApiCatalog
        {
            Title = doc.Info?.Title,
            Version = doc.Info?.Version?.ToString(),
            Namespace = targetNamespace,
            BaseUrl = baseUrl ?? ResolveServerUrl(doc),
        };

        // 解析鉴权方案
        ResolveAuth(catalog, doc, forcedScheme);

        // 先建立全局 key → 短名映射（模型名与 $ref 引用共用，保证一致）
        if (doc.Components?.Schemas is { } allSchemas)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            catalog.ShortTypeNames = allSchemas.Keys
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToDictionary(k => k, k => UniqueShortName(ShortTypeName(k), used));
        }

        // 计算每个 schema 的模块归属（单 tag 顶层引用 → 该模块，否则 Shared）
        var schemaModules = BuildSchemaModules(doc);

        // 登记所有顶层 schema：先登记枚举（建立签名供内联枚举去重复用），再登记模型（属性可能含内联枚举）
        if (doc.Components?.Schemas is { } schemas)
        {
            foreach (var (name, schema) in schemas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var shortName = catalog.ShortTypeNames.GetValueOrDefault(name);
                if (shortName is null || CSharpKeywords.Contains(shortName.ToLowerInvariant()))
                    continue; // 泛型外壳包裹基元（如 ServiceResult<string>）→ 响应直接解包，无需模型

                if (!TryIsEnum(schema, out var members)) continue;
                var asString = schema.Type == "string";
                catalog.Enums.Add(new ApiEnum
                {
                    Name = shortName,
                    Module = schemaModules.GetValueOrDefault(name, "Shared"),
                    Members = members,
                    AsString = asString,
                });
                catalog.EnumSignatures.TryAdd(EnumSignature(asString, members), shortName);
            }

            foreach (var (name, schema) in schemas.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var shortName = catalog.ShortTypeNames.GetValueOrDefault(name);
                if (shortName is null || CSharpKeywords.Contains(shortName.ToLowerInvariant()))
                    continue;
                if (TryIsEnum(schema, out _)) continue; // 已作为顶层枚举登记

                if (schema.Type == "object" || schema.Properties is { Count: > 0 })
                    catalog.Models.Add(BuildModel(shortName, schema, catalog, schemaModules.GetValueOrDefault(name, "Shared")));
            }
        }

        // 按 tag 分组端点
        BuildClients(catalog, doc);

        return catalog;
    }

    /// <summary>
    /// 使 1.x reader 能解析 OpenAPI 3.1 文档（reader 仅认 2.0/3.0）：
    /// 1) 将 "openapi":"3.1.x" 声明视角为 3.0；
    /// 2) 把 3.1 的数组式联合 type（如 ["integer","string"]、["null","string"]）
    ///    归一为 3.0 的单值 type：取非 string/null 的 token 为主类型，仅 string/null 则视为 string。
    /// </summary>
    private static string NormalizeVersion(string json)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(
            json, "\"openapi\"\\s*:\\s*\"3\\.1\\.[0-9]+\"", "\"openapi\": \"3.0.0\"");
        s = System.Text.RegularExpressions.Regex.Replace(
            s, "\"type\"\\s*:\\s*\\[[^\\]]*\\]", TypeUnionEvaluator);
        return s;
    }

    private static string TypeUnionEvaluator(System.Text.RegularExpressions.Match m)
    {
        // 只扫描数组体 [ ... ]，避免把前面的键名 "type" 计入候选 token
        var array = m.Value[m.Value.IndexOf('[', StringComparison.Ordinal)..];
        var tokens = System.Text.RegularExpressions.Regex.Matches(array, "\"([^\"]+)\"")
            .Cast<System.Text.RegularExpressions.Match>()
            .Select(x => x.Groups[1].Value)
            .ToList();
        var nullable = tokens.Contains("null", StringComparer.Ordinal);
        var primary = tokens.FirstOrDefault(t => t != "null" && t != "string") ?? "string";
        // 3.1 用类型联合表达可空（如 ["string","null"]），降级为 3.0 的 type + nullable
        return nullable
            ? $"\"type\": \"{primary}\", \"nullable\": true"
            : $"\"type\": \"{primary}\"";
    }

    private static void ResolveAuth(ApiCatalog catalog, OpenApiDocument doc, string? forcedScheme)
    {
        string? schemeType = forcedScheme?.ToLowerInvariant();
        string? apiKeyName = null;
        string? apiKeyLocation = null;

        if (doc.Components?.SecuritySchemes is { Count: > 0 } schemes)
        {
            // 遍历全部安全方案，按 bearer > apikey > 其它 http 的优先级择一（多方案时结果确定，不再依赖字典枚举顺序）
            OpenApiSecurityScheme? bearer = null, apiKey = null, otherHttp = null;
            foreach (var s in schemes.Values)
            {
                switch (s.Type)
                {
                    case SecuritySchemeType.Http when string.Equals(s.Scheme, "bearer", StringComparison.OrdinalIgnoreCase):
                        bearer ??= s;
                        break;
                    case SecuritySchemeType.ApiKey:
                        apiKey ??= s;
                        break;
                    case SecuritySchemeType.Http:
                        otherHttp ??= s;
                        break;
                }
            }

            var chosen = bearer ?? apiKey ?? otherHttp;
            if (chosen is not null && schemeType is null)
                schemeType = chosen.Type == SecuritySchemeType.ApiKey ? "apikey" : "bearer";

            if (schemeType == "apikey" && chosen?.Type == SecuritySchemeType.ApiKey)
            {
                apiKeyName ??= chosen!.Name;
                apiKeyLocation ??= chosen!.In == ParameterLocation.Query ? "query" : "header";
            }
        }

        // 强制 apikey 但文档无 ApiKey 方案：用可预测的默认头名，避免生成「需鉴权却无设置入口」的客户端
        if (schemeType == "apikey" && apiKeyLocation is null)
        {
            apiKeyName ??= "X-Api-Key";
            apiKeyLocation = "header";
        }

        catalog.AuthScheme = schemeType;
        catalog.ApiKeyName = apiKeyName;
        catalog.ApiKeyLocation = apiKeyLocation;
    }

    private static string? ResolveServerUrl(OpenApiDocument doc)
    {
        var server = doc.Servers?.FirstOrDefault(s => !string.IsNullOrEmpty(s.Url));
        return server?.Url;
    }

    private static void BuildClients(ApiCatalog catalog, OpenApiDocument doc)
    {
        var groups = new SortedDictionary<string, ClientClass>(StringComparer.OrdinalIgnoreCase);

        // OpenAPI 3.1 允许省略 paths（仅 webhooks 等），doc.Paths 可能为 null
        foreach (var (path, pathItem) in doc.Paths ?? new OpenApiPaths())
        {
            foreach (var (method, operation) in pathItem.Operations)
            {
                var groupKey = operation.Tags?.FirstOrDefault()?.Name ?? FirstSegment(path);
                if (string.IsNullOrWhiteSpace(groupKey))
                    continue;

                if (!groups.TryGetValue(groupKey, out var client))
                {
                    client = new ClientClass { Name = ToPascal(groupKey) + "Client", Module = ToPascal(groupKey) };
                    groups[groupKey] = client;
                }

                client.Operations.Add(BuildOperation(catalog, path, method.ToString(), operation, client.Module));
            }
        }

        catalog.Clients = groups.Values.ToList();
        foreach (var client in catalog.Clients)
            DedupOperationNames(client);
    }

    /// <summary>同一客户端内操作名去重：缺 operationId 或末段碰撞时追加数字后缀，保证生成的方法名唯一。</summary>
    private static void DedupOperationNames(ClientClass client)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var op in client.Operations)
        {
            var baseName = string.IsNullOrEmpty(op.Name) ? "Operation" : op.Name;
            var candidate = baseName;
            int n = 2;
            while (!used.Add(candidate)) candidate = baseName + "_" + n++;
            op.Name = candidate;
        }
    }

    private static ApiOperation BuildOperation(ApiCatalog catalog, string path, string method, OpenApiOperation op, string module)
    {
        var name = ToPascal(op.OperationId ?? method + ShortLastSegment(path));

        var o = new ApiOperation
        {
            Name = name,
            HttpMethod = method.ToLowerInvariant() switch
            {
                "get" => "Get",
                "post" => "Post",
                "put" => "Put",
                "delete" => "Delete",
                "patch" => "Patch",
                "head" => "Head",
                "options" => "Options",
                _ => method.ToLowerInvariant(),
            },
            Path = path,
            Summary = op.Summary,
        };

        foreach (var p in op.Parameters)
        {
            var csharpType = MapType(p.Schema, catalog, out _, enumHint: module + ToPascal(p.Name), enumModule: module);
            switch (p.In)
            {
                case ParameterLocation.Path:
                    o.PathParams.Add(new ApiParameter { Name = ToCamel(p.Name), JsonName = p.Name, Type = csharpType, IsRequired = true, IsNullable = p.Schema?.Nullable == true });
                    break;
                case ParameterLocation.Query:
                {
                    var (qStyle, qExplode) = QuerySerialization(p);
                    o.QueryParams.Add(new ApiParameter { Name = ToCamel(p.Name), JsonName = p.Name, Type = csharpType, IsRequired = p.Required, IsNullable = p.Schema?.Nullable == true, Style = qStyle, Explode = qExplode });
                    break;
                }
            }
        }

        if (op.RequestBody?.Content is { Count: > 0 } content)
        {
            var mediaType = content.Keys.FirstOrDefault(k => k.StartsWith("application", StringComparison.OrdinalIgnoreCase))
                            ?? content.Keys.First();
            if (content[mediaType].Schema is { } bodySchema)
            {
                o.RequestContentType = mediaType.Split(';')[0];
                o.Body = new ApiParameter
                {
                    Name = "body",
                    JsonName = "body",
                    Type = MapType(bodySchema, catalog, out _),
                    IsNullable = bodySchema.Nullable == true,
                };
            }
        }

        o.ResponseType = ResolveResponseType(op, catalog);
        return o;
    }

    private static string? ResolveResponseType(OpenApiOperation op, ApiCatalog catalog)
    {
        foreach (var (code, response) in op.Responses)
        {
            if (code == "204") continue;
            if (code is "200" or "201" or "default" || code.StartsWith("2"))
            {
                var media = response.Content
                    .FirstOrDefault(kvp => kvp.Key.StartsWith("application") && kvp.Value.Schema is not null);
                if (media.Value?.Schema is { } schema)
                    return MapType(schema, catalog, out _);
            }
        }
        return null;
    }

    // ---- 公共辅助 ----

    private static ApiModel BuildModel(string name, OpenApiSchema schema, ApiCatalog catalog, string module)
    {
        var model = new ApiModel { Name = name, Module = module }; // name 已是短化后的标识符
        if (schema.Properties is { } props)
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (propName, propSchema) in props)
            {
                var pascal = ToPascal(propName);
                var unique = pascal;
                int suffix = 2;
                while (!usedNames.Add(unique)) unique = pascal + "_" + suffix++;
                model.Properties.Add(new ApiProperty
                {
                    Name = unique,
                    JsonName = propName,
                    Type = MapType(propSchema, catalog, out _, enumHint: name + ToPascal(propName), enumModule: module),
                    IsRequired = schema.Required?.Contains(propName) == true,
                    IsNullable = propSchema.Nullable == true || schema.Required?.Contains(propName) != true,
                });
            }
        }
        return model;
    }

    /// <summary>将 OpenAPI 类型映射为 C# 类型。返回 primitive 表示是否为标量原生类型。</summary>
    private static string MapType(OpenApiSchema? schema, ApiCatalog catalog, out bool primitive, string? enumHint = null, string? enumModule = null)
    {
        // $ref 引用
        if (schema?.Reference is { } reference)
        {
            primitive = false;
            return catalog.ShortTypeNames.GetValueOrDefault(reference.Id) ?? ToPascal(reference.Id);
        }

        // allOf 合并（取第一个有引用的）
        if (schema is { AllOf.Count: > 0 } && schema.AllOf.FirstOrDefault(a => a.Reference is not null) is { } allOfRef)
        {
            primitive = false;
            return catalog.ShortTypeNames.GetValueOrDefault(allOfRef.Reference!.Id) ?? ToPascal(allOfRef.Reference!.Id);
        }
        if (schema is { AllOf.Count: > 0 } && schema.AllOf.FirstOrDefault(a => a.Type != null) is { } allOfInline)
        {
            return MapType(allOfInline, catalog, out primitive);
        }

        if (schema is null) { primitive = true; return "object?"; }

        // 3.1 纯 null 类型（含 oneOf 成员 {type:"null"}）→ 可空占位，交属性/参数级 nullable 处理
        if (schema.Type == "null")
        {
            primitive = true;
            return "object";
        }

        if (schema.Type == "array")
        {
            var inner = MapType(schema.Items, catalog, out _,
                enumHint: enumHint is null ? null : enumHint + "Item", enumModule: enumModule);
            primitive = false;
            return $"{inner}[]";
        }

        // 内联枚举：带上下文（参数/属性）时生成或复用枚举类型；无上下文（如响应）退回标量映射
        if (enumHint is not null && schema.Enum is { Count: > 0 } && TryIsEnum(schema, out var enumMembers))
        {
            primitive = false;
            return RegisterInlineEnum(catalog, enumMembers, schema.Type == "string", enumHint, enumModule ?? "Shared");
        }

        // oneOf / anyOf：C# 无原生联合类型。全部成员收敛为同一非空类型 → 用该类型；否则退回 object（按原样 JSON 处理）
        if (schema.OneOf is { Count: > 0 } || schema.AnyOf is { Count: > 0 })
        {
            primitive = false;
            return ResolveCombinator(schema.OneOf is { Count: > 0 } ? schema.OneOf : schema.AnyOf!, catalog, enumHint, enumModule);
        }

        // OpenAPI 3.1 type 可能是数组
        var types = schema.Type is string t ? new[] { t } : Array.Empty<string>();
        var (typeName, isPrimitive) = (schema.Type, schema.Format) switch
        {
            ("string", "date-time") => ("DateTimeOffset", true),
            ("string", "date") => ("DateTime", true),
            ("string", "date-time-offset") => ("DateTimeOffset", true),
            ("string", "byte") => ("byte[]", false),
            ("string", "binary") => ("byte[]", false),
            ("string", _) => ("string", true),
            ("integer", "int64") => ("long", true),
            ("integer", _) => ("int", true),
            ("boolean", _) => ("bool", true),
            ("number", "float") => ("float", true),
            ("number", "double") => ("double", true),
            ("number", _) => ("decimal", true),
            ("object", _) => ("Dictionary<string, object?>", false),
            (_, _) => ("string", true),
        };

        primitive = isPrimitive;
        return typeName;
    }

    /// <summary>登记内联枚举：相同定义（基类型 + 有序成员）复用已存在的枚举类型，否则按上下文名新建并登记签名。</summary>
    private static string RegisterInlineEnum(ApiCatalog catalog, List<ApiEnumValue> members, bool asString, string hint, string module)
    {
        var sig = EnumSignature(asString, members);
        if (catalog.EnumSignatures.TryGetValue(sig, out var existing))
            return existing;

        var used = new HashSet<string>(
            catalog.Enums.Select(e => e.Name).Concat(catalog.Models.Select(m => m.Name)),
            StringComparer.Ordinal);
        var name = UniqueShortName(ToPascal(hint), used);
        catalog.Enums.Add(new ApiEnum { Name = name, Module = module, Members = members, AsString = asString });
        catalog.EnumSignatures[sig] = name;
        return name;
    }

    /// <summary>枚举定义签名：基类型（s/i）+ 有序成员原始 JSON 值，用于跨位置去重。</summary>
    private static string EnumSignature(bool asString, List<ApiEnumValue> members)
        => (asString ? "s" : "i") + "|" + string.Join("|", members.Select(m => m.JsonName));

    /// <summary>解析 oneOf/anyOf：忽略 null 占位成员；若所有非空成员映射为同一类型则返回它，否则无法用单一 C# 类型表达 → 退回 object。</summary>
    private static string ResolveCombinator(IList<OpenApiSchema> options, ApiCatalog catalog, string? enumHint, string? enumModule)
    {
        string? unified = null;
        foreach (var opt in options)
        {
            if (opt.Reference is null && opt.Type == "null") continue; // 可空占位，忽略
            var t = MapType(opt, catalog, out _,
                enumHint is null ? null : enumHint + "Option", enumModule);
            if (unified is null) unified = t;
            else if (unified != t) return "object";
        }
        return unified ?? "object";
    }

    /// <summary>
    /// 查询参数的数组序列化。OpenApiParameter.Explode 为非空 bool（未声明时=false），故用 Style 区分“显式声明 vs 缺省”：
    /// 仅当 style 显式为 Form 时才尊重 explode；reader 未设置（默认 Matrix）或其它风格 → 按查询规范默认 form + explode=true（重复键）。
    /// </summary>
    private static (string Style, bool Explode) QuerySerialization(OpenApiParameter p)
    {
        return p.Style switch
        {
            ParameterStyle.SpaceDelimited => ("spaceDelimited", false),
            ParameterStyle.PipeDelimited => ("pipeDelimited", false),
            ParameterStyle.Form => ("form", p.Explode), // 显式 form：尊重声明的 explode（false → 逗号单键）
            _ => ("form", true), // 未显式设置（默认 Matrix）或 Matrix/Label/DeepObject → 规范默认 form 重复键
        };
    }

    // ---- 命名工具 ----

    private static bool TryIsEnum(OpenApiSchema schema, out List<ApiEnumValue> members)
    {
        members = new List<ApiEnumValue>();
        if (schema.Enum is not { Count: > 0 }) return false;
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < schema.Enum.Count; i++)
        {
            var v = schema.Enum[i];
            var text = v switch
            {
                OpenApiString s => s.Value,
                OpenApiInteger n => n.Value.ToString(),
                OpenApiLong n => n.Value.ToString(),
                OpenApiBoolean b => b.Value.ToString(),
                _ => v?.ToString(),
            };
            if (string.IsNullOrEmpty(text)) continue;

            var name = ToPascal(text);
            // 去重：-1 与 1 可能映射到同一标识符
            var unique = name;
            int suffix = 2;
            while (!usedNames.Add(unique)) unique = name + "_" + suffix++;
            // 数值：可解析为数字用原值，否则用数组下标
            var value = long.TryParse(text, out var num) ? num.ToString() : i.ToString();
            members.Add(new ApiEnumValue { Name = unique, JsonName = text, Value = value });
        }
        return members.Count > 0;
    }

    private static string FirstSegment(string path)
    {
        var seg = path.Trim('/').Split('/').FirstOrDefault();
        return string.IsNullOrEmpty(seg) || CSharpKeywords.Contains(seg) ? "Default" : segmentToPlain(seg);
    }

    private static string segmentToPlain(string seg) => seg;

    private static string ShortLastSegment(string path)
    {
        var seg = path.Trim('/').Split('/').LastOrDefault() ?? "";
        var clean = new string(seg.Where(c => char.IsLetterOrDigit(c)).ToArray());
        return clean.Length > 0 ? clean : "Item";
    }

    private static string ToCamel(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        if (CSharpKeywords.Contains(s)) return "@" + s;
        return char.ToLowerInvariant(s[0]) + s.Substring(1);
    }

    private static string ToPascal(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new StringBuilder(s.Length);
        bool upper = true;
        foreach (var c in s)
        {
            if (!char.IsLetterOrDigit(c))
            {
                // 分隔符（含 `` ` ``、`` [[ ]] ``、`,`、`=、``:`` 等元数据残留）起新词
                upper = true;
                continue;
            }
            sb.Append(upper && char.IsLetter(c) ? char.ToUpperInvariant(c) : c);
            upper = false;
        }
        var result = sb.ToString();
        if (result.Length == 0) return result;
        if (!char.IsLetter(result[0])) result = "_" + result;
        return result;
    }

    // ---------- schema 全名 → 短化类型名 ----------

    /// <summary>短化：非泛型取最后一段类名；泛型取「外壳段拼接 + 最内层载荷」，基元载荷直接解包。</summary>
    private static string ShortTypeName(string fullKey)
    {
        var wrappers = new List<string>();
        var leaf = ResolveLeaf(fullKey, wrappers);
        var prim = PrimitiveCs(leaf);
        if (prim is not null) return prim;
        return string.Concat(wrappers) + leaf;
    }

    private static string UniqueShortName(string shortName, HashSet<string> used)
    {
        if (CSharpKeywords.Contains(shortName))
            return shortName; // 基元解包（如 string/int）无需去重，统一映射到 C# 内置类型
        var unique = shortName;
        int n = 2;
        while (!used.Add(unique)) unique = shortName + "_" + n++;
        return unique;
    }

    /// <summary>深入泛型外壳链：每层基名加入 wrappers，最内层返回载荷段。</summary>
    private static string ResolveLeaf(string key, List<string> wrappers)
    {
        var backtick = key.IndexOf('`');
        if (backtick < 0)
            return CleanTypeRef(key);
        wrappers.Add(LastDotSegment(key, backtick));
        var arg = FirstPayloadArg(key, backtick);
        return arg.Length == 0 ? LastDotSegment(key, backtick) : ResolveLeaf(arg, wrappers);
    }

    /// <summary>去掉命名空间路径、程序集限定，取最后一段简单名。</summary>
    private static string CleanTypeRef(string s)
    {
        var t = s.Trim().TrimEnd(']');
        var comma = t.IndexOf(',');
        if (comma >= 0) t = t.Substring(0, comma);
        return LastDotSegment(t, t.Length);
    }

    private static string LastDotSegment(string s, int upto)
    {
        if (upto > s.Length) upto = s.Length;
        var seg = s.Substring(0, upto);
        var dot = seg.LastIndexOf('.');
        return dot >= 0 ? seg.Substring(dot + 1) : seg;
    }

    /// <summary>返回第一个泛型实参的类型引用串（带嵌套泛型时保留其 [[...]]）。</summary>
    private static string FirstPayloadArg(string key, int backtick)
    {
        var p = backtick + 1;
        while (p < key.Length && char.IsDigit(key[p])) p++;
        if (p >= key.Length || key[p] != '[') return string.Empty;

        // 找到与最外层 [[ 匹配的 ]]
        int depth = 0, close = -1;
        for (int i = p; i < key.Length; i++)
        {
            if (key[i] == '[') depth++;
            else if (key[i] == ']') { depth--; if (depth == 0) { close = i; break; } }
        }
        if (close < 0 || close - (p + 2) <= 0) return string.Empty;
        var inner = key.Substring(p + 2, close - (p + 2));

        // 按顶层逗号分割取第一个实参
        depth = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            char c = inner[i];
            if (c == '[') depth++;
            else if (c == ']') depth--;
            else if (c == ',' && depth == 0)
                return inner.Substring(0, i).Trim();
        }
        return inner.Trim();
    }

    /// <summary>System.X 简单名 → C# 关键字/内置类型；非基元返回 null。</summary>
    private static string? PrimitiveCs(string? name) => name switch
    {
        "String" => "string",
        "Int32" => "int",
        "Int64" => "long",
        "Boolean" => "bool",
        "Double" => "double",
        "Decimal" => "decimal",
        "Single" => "float",
        "Byte" => "byte",
        "SByte" => "sbyte",
        "Int16" => "short",
        "UInt16" => "ushort",
        "UInt32" => "uint",
        "UInt64" => "ulong",
        "Char" => "char",
        "Object" => "object",
        "DateTime" => "DateTime",
        "DateTimeOffset" => "DateTimeOffset",
        "Guid" => "Guid",
        "TimeSpan" => "TimeSpan",
        _ => null,
    };

    // ---------- schema → 模块归属 ----------

    /// <summary>计算每个 schema 的模块归属：被单个 tag 的「业务闭包」覆盖 → 该模块；否则 Shared。</summary>
    private static Dictionary<string, string> BuildSchemaModules(OpenApiDocument doc)
    {
        // 收集每个 tag 顶层引用的 schema
        var tagRefs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, pathItem) in doc.Paths ?? new OpenApiPaths())
        {
            foreach (var (_, operation) in pathItem.Operations)
            {
                var groupKey = operation.Tags?.FirstOrDefault()?.Name ?? FirstSegment(path);
                if (string.IsNullOrWhiteSpace(groupKey))
                    continue;
                var module = ToPascal(groupKey);
                if (!tagRefs.TryGetValue(module, out var set))
                    tagRefs[module] = set = new HashSet<string>(StringComparer.Ordinal);
                CollectTopLevelRefs(operation, set);
            }
        }

        // 构建 schema → 依赖 schema 图（属性/items/allOf 引用的最近 ref）
        var deps = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (doc.Components?.Schemas is { } schemas)
        {
            foreach (var (id, schema) in schemas)
                deps[id] = ReferenceDependencies(schema);
        }

        // 对每个模块做依赖闭包 → 该模块覆盖的所有 schema
        var tagCovered = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (module, topRefs) in tagRefs)
        {
            var covered = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>(topRefs);
            while (queue.Count > 0)
            {
                var id = queue.Dequeue();
                if (!covered.Add(id)) continue;
                foreach (var dep in deps.GetValueOrDefault(id, new HashSet<string>()))
                    if (!covered.Contains(dep))
                        queue.Enqueue(dep);
            }
            tagCovered[module] = covered;
        }

        // schema 归属 = 闭包覆盖它的一或多个模块
        var refModules = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (module, covered) in tagCovered)
        {
            foreach (var id in covered)
            {
                if (!refModules.TryGetValue(id, out var set))
                    refModules[id] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(module);
            }
        }

        return refModules.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.Count == 1 ? kv.Value.First() : "Shared",
            StringComparer.Ordinal);
    }

    private static HashSet<string> ReferenceDependencies(OpenApiSchema schema)
    {
        var deps = new HashSet<string>(StringComparer.Ordinal);
        void Add(OpenApiSchema? s)
        {
            if (NearestRef(s) is { } id)
                deps.Add(id);
        }
        if (schema.Properties is { } props)
            foreach (var (_, ps) in props)
                Add(ps);
        if (schema is { AllOf.Count: > 0 })
            foreach (var a in schema.AllOf)
                Add(a);
        if (schema.Type == "array" && schema.Items is not null)
            Add(schema.Items);
        return deps;
    }

    private static void CollectTopLevelRefs(OpenApiOperation op, HashSet<string> set)
    {
        foreach (var p in op.Parameters)
            AddIfRef(p.Schema, set);
        if (op.RequestBody?.Content is { } reqContent)
            foreach (var media in reqContent.Values)
                AddIfRef(media.Schema, set);
        foreach (var (_, response) in op.Responses)
            foreach (var media in response.Content.Values)
                AddIfRef(media.Schema, set);
    }

    private static void AddIfRef(OpenApiSchema? schema, HashSet<string> set)
    {
        if (NearestRef(schema) is { } id)
            set.Add(id);
    }

    /// <summary>解包 array/allOf 后返回最近的 $ref id（无则 null）。</summary>
    private static string? NearestRef(OpenApiSchema? schema)
    {
        while (schema is not null)
        {
            if (schema.Reference is not null)
                return schema.Reference.Id;
            if (schema.Type == "array" && schema.Items is not null)
            {
                schema = schema.Items;
                continue;
            }
            var allOfRef = schema.AllOf.FirstOrDefault(a => a.Reference is not null);
            if (allOfRef is not null)
            {
                schema = allOfRef;
                continue;
            }
            break;
        }
        return null;
    }

    private static readonly HashSet<string> CSharpKeywords = new()
    {
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue",
        "decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally",
        "fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long",
        "namespace","new","null","object","operator","out","override","params","private","protected","public",
        "readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch",
        "this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void",
        "volatile","while","default","global","record","init","required","var","dynamic","params","async","await",
        "yield"
    };
}