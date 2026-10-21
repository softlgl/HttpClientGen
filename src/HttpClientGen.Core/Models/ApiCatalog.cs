namespace HttpClientGen.Core.Models;

/// <summary>生成器产出的代码目录（中间模型，供 Razor 模板使用）。</summary>
public sealed class ApiCatalog
{
    /// <summary>API 标题（来自 OpenAPI info.title）。</summary>
    public string? Title { get; set; }

    /// <summary>API 版本（来自 OpenAPI info.version）。</summary>
    public string? Version { get; set; }

    /// <summary>目标命名空间。</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>基础地址（可选，来自 --base-url 或 OpenAPI servers）。</summary>
    public string? BaseUrl { get; set; }

    /// <summary>鉴权方案：bearer / apikey。</summary>
    public string? AuthScheme { get; set; }

    /// <summary>API Key 名称（apikey 时有效）。</summary>
    public string? ApiKeyName { get; set; }

    /// <summary>API Key 位置：header / query。</summary>
    public string? ApiKeyLocation { get; set; }

    /// <summary>是否生成鉴权支持代码。</summary>
    public bool RequiresAuth => AuthScheme is not null;

    public List<ApiModel> Models { get; set; } = new();
    public List<ApiEnum> Enums { get; set; } = new();
    public List<ClientClass> Clients { get; set; } = new();

    /// <summary>schema 全名 → 短化 C# 类型名 的映射（模型名与 $ref 引用共用，保证一致）。</summary>
    public Dictionary<string, string> ShortTypeNames { get; set; } = new();

    /// <summary>枚举签名（基类型 + 有序成员原始值）→ 已生成枚举类型名，供内联枚举按定义去重复用。</summary>
    public Dictionary<string, string> EnumSignatures { get; } = new(StringComparer.Ordinal);
}

/// <summary>一个请求/响应模型类。</summary>
public sealed class ApiModel
{
    public string Name { get; set; } = string.Empty;

    /// <summary>所属模块（子目录/子命名空间）；默认 Shared。</summary>
    public string Module { get; set; } = "Shared";

    public List<ApiProperty> Properties { get; set; } = new();
}

/// <summary>模型属性。</summary>
public sealed class ApiProperty
{
    public string Name { get; set; } = string.Empty;

    /// <summary>序列化时的 JSON 名称。</summary>
    public string JsonName { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsNullable { get; set; }
}

/// <summary>枚举。</summary>
public sealed class ApiEnum
{
    public string Name { get; set; } = string.Empty;

    /// <summary>所属模块（子目录/子命名空间）；默认 Shared。</summary>
    public string Module { get; set; } = "Shared";

    public List<ApiEnumValue> Members { get; set; } = new();

    /// <summary>枚举值来自字符串（OpenAPI type=string）时，按字符串双向映射并需 JSON 转换器。</summary>
    public bool AsString { get; set; }
}

/// <summary>枚举成员：C# 标识名 + 数值。</summary>
public sealed class ApiEnumValue
{
    /// <summary>C# 成员标识符（Pascal 化/去重）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>OpenAPI 原始值（如 "available"），用于 JsonEnumMemberName 保持管线值。</summary>
    public string JsonName { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

/// <summary>按 tag 分组的一个类型化客户端。</summary>
public sealed class ClientClass
{
    public string Name { get; set; } = string.Empty;

    /// <summary>所属模块（tag 的 Pascal 化），决定子目录与子命名空间。</summary>
    public string Module { get; set; } = string.Empty;

    public List<ApiOperation> Operations { get; set; } = new();
}

/// <summary>一个端点操作。</summary>
public sealed class ApiOperation
{
    public string Name { get; set; } = string.Empty;
    public string HttpMethod { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string? Summary { get; set; }

    public List<ApiParameter> PathParams { get; set; } = new();
    public List<ApiParameter> QueryParams { get; set; } = new();
    public ApiParameter? Body { get; set; }
    public string? RequestContentType { get; set; }

    /// <summary>返回类型；为空表示无返回体（void）。</summary>
    public string? ResponseType { get; set; }

    public bool HasBody => Body is not null;
    public bool HasResponse => !string.IsNullOrEmpty(ResponseType);
    public bool HasQuery => QueryParams.Count > 0;
}

/// <summary>操作参数。</summary>
public sealed class ApiParameter
{
    public string Name { get; set; } = string.Empty;
    public string JsonName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsRequired { get; set; }
    public bool IsNullable { get; set; }

    /// <summary>查询参数序列化风格（数组/集合用）：form / spaceDelimited / pipeDelimited。</summary>
    public string Style { get; set; } = "form";

    /// <summary>查询参数是否展开为重复键（仅 style=form 生效：explode=true 重复键，false 逗号单键）。</summary>
    public bool Explode { get; set; } = true;
}