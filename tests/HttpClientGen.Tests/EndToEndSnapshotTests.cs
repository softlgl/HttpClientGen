using System.Text.Json;
using HttpClientGen.Core.Generators;
using HttpClientGen.Core.Models;
using HttpClientGen.Core.Parsing;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HttpClientGen.Tests;

[TestClass]
public sealed class EndToEndSnapshotTests
{
    private const string RootNs = "Snapshot.Client";

    private static readonly string FixturePath =
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "petstore.json");

    // ---------- 生成流程 ----------

    private static (ApiCatalog, IReadOnlyDictionary<string, string>) Generate(string json, string @namespace = RootNs)
    {
        var catalog = OpenApiParser.Parse(json, @namespace, baseUrl: null, forcedScheme: null);
        using var gen = new CodeGenerator();
        return (catalog, gen.Generate(catalog));
    }

    // ---------- 用例 ----------

    [TestMethod]
    public void 根据tag按模块产出文件且根扩展在根命名空间()
    {
        var (catalog, files) = Generate(File.ReadAllText(FixturePath));

        // 结构：两个模块 + 根 DI 扩展，无平铺根 Models.cs
        foreach (var module in new[] { "Pets", "Stores" })
        {
            CollectionAssert.Contains(files.Keys.ToList(), $"{module}/{module}Models.cs");
            CollectionAssert.Contains(files.Keys.ToList(), $"{module}/{module}Client.cs");
        }
        CollectionAssert.Contains(files.Keys.ToList(), "ServiceCollectionExtensions.cs");
        Assert.IsFalse(files.Keys.Contains("Models.cs"), "模型不应再平铺在根命名空间");

        // 鉴权推断为 Bearer
        Assert.IsTrue(catalog.RequiresAuth);
        Assert.AreEqual("bearer", catalog.AuthScheme);
    }

    [TestMethod]
    public void 生成产物整体编译零错误()
    {
        var (_, files) = Generate(File.ReadAllText(FixturePath));
        var compilation = SnapshotCompiler.Compile(files);
        var errors = SnapshotCompiler.Errors(compilation);
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors.Select(e => e.ToString())));
    }

    [TestMethod]
    public void 端点生成接口客户端类型且客户端实现对应接口()
    {
        var (catalog, files) = Generate(File.ReadAllText(FixturePath));
        var compilation = SnapshotCompiler.Compile(files);

        foreach (var client in catalog.Clients)
        {
            var implFq = $"{RootNs}.{client.Module}.{client.Name}";
            var ifaceFq = $"{RootNs}.{client.Module}.I{client.Name}";

            var impl = compilation.GetTypeByMetadataName(implFq);
            var iface = compilation.GetTypeByMetadataName(ifaceFq);

            Assert.IsNotNull(impl, $"未生成客户端类型 {implFq}");
            Assert.IsNotNull(iface, $"未生成接口 {ifaceFq}");

            // 客户端应至少拥有与接口同名的方法，且接口命名与客户端成对
            Assert.IsTrue(impl.AllInterfaces.Any(i => i.ToDisplayString() == ifaceFq),
                $"{client.Name} 未实现 {ifaceFq}");
        }
    }

    [TestMethod]
    public void DI扩展类提供AddGeneratedClients注册()
    {
        var (catalog, files) = Generate(File.ReadAllText(FixturePath));
        var compilation = SnapshotCompiler.Compile(files);

        var method = SnapshotCompiler.FindStaticMethod(compilation, "AddGeneratedClients");
        Assert.IsNotNull(method, "根命名空间应生成静态方法 AddGeneratedClients");
        Assert.IsTrue(method.IsExtensionMethod);

        // 为每个 tag 注册了一个 AddHttpClient<IX, X>
        var returnType = method.ReturnType.ToDisplayString();
        Assert.AreEqual(
            "Microsoft.Extensions.DependencyInjection.IServiceCollection", returnType,
            "AddGeneratedClients 应返回 IServiceCollection 以支持链式调用");
    }

    [TestMethod]
    public void 生成的DTO模型类型在产物中可解析()
    {
        var (catalog, files) = Generate(File.ReadAllText(FixturePath));
        var compilation = SnapshotCompiler.Compile(files);

        // 每个模块的 Models.cs 应与该模块的 DTO 一一对应
        foreach (var group in catalog.Models
                     .Where(m => !m.Module.Equals("Shared", StringComparison.Ordinal))
                     .GroupBy(m => m.Module))
        {
            var module = group.Key;
            var models = group.ToList();
            CollectionAssert.Contains(files.Keys.ToList(), $"{module}/{module}Models.cs");

            var firstShortName = models.First().Name;
            var type = compilation.GetTypeByMetadataName($"{RootNs}.{module}.{firstShortName}");
            Assert.IsNotNull(type, $"模块 {module} 的模型 {firstShortName} 未解析");
        }

        // 所有产物均非空
        Assert.IsTrue(files.Values.All(v => !string.IsNullOrWhiteSpace(v)));
    }

    [TestMethod]
    public void 契约信息可从解析结果读取()
    {
        var json = File.ReadAllText(FixturePath);
        var (catalog, _) = Generate(json);
        var raw = JsonDocument.Parse(json).RootElement;

        var expectedTitle = raw.GetProperty("info").GetProperty("title").GetString();
        Assert.AreEqual(expectedTitle, catalog.Title);
        Assert.IsNotNull(catalog.Version);
        Assert.IsTrue(catalog.Clients.Count >= 2);
    }

    [TestMethod]
    public void 数组与枚举查询参数_产物编译零错误()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "QueryGen", "version": "1.0" },
          "paths": {
            "/search/{id}": {
              "get": {
                "tags": ["Search"],
                "operationId": "searchItems",
                "parameters": [
                  { "name": "id", "in": "path", "required": true, "schema": { "type": "integer", "format": "int64" } },
                  { "name": "color", "in": "query", "required": false, "schema": { "$ref": "#/components/schemas/Color" } },
                  { "name": "tags", "in": "query", "required": false, "schema": { "type": "array", "items": { "type": "string" } } },
                  { "name": "minId", "in": "query", "required": false, "schema": { "type": "integer", "format": "int64" } }
                ],
                "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Item" } } } } }
              }
            }
          },
          "components": {
            "schemas": {
              "Color": { "type": "string", "enum": ["red", "green light", "2dark"] },
              "Item": { "type": "object", "properties": { "id": { "type": "integer", "format": "int64" }, "color": { "$ref": "#/components/schemas/Color" } } }
            }
          }
        }
        """;

        var (_, files) = Generate(spec);
        var compilation = SnapshotCompiler.Compile(files);
        var errors = SnapshotCompiler.Errors(compilation);
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors.Select(e => e.ToString())));

        var client = files["Search/SearchClient.cs"];
        StringAssert.Contains(client, "AppendQuery");      // 数组查询展开为重复键
        StringAssert.Contains(client, "WireValue");        // 枚举/标量的线值转换
        StringAssert.Contains(client, "object? Value");     // 查询元组承载原始值
        StringAssert.Contains(client, "Color? color");     // $ref 枚举映射为枚举类型参数
    }

    [TestMethod]
    public void 同tag无operationId末段同名_操作名去重()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Dedup", "version": "1" },
          "paths": {
            "/a/pets": { "get": { "tags": ["Data"], "responses": { "200": { "description": "ok" } } } },
            "/b/pets": { "get": { "tags": ["Data"], "responses": { "200": { "description": "ok" } } } }
          }
        }
        """;

        var catalog = OpenApiParser.Parse(spec, "Dedup.Client", baseUrl: null, forcedScheme: null);
        var names = catalog.Clients.Single().Operations.Select(o => o.Name).ToList();

        Assert.AreEqual(2, names.Count);
        Assert.AreEqual(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "操作名应唯一");
        CollectionAssert.Contains(names, "Getpets");
        CollectionAssert.Contains(names, "Getpets_2");
    }

    [TestMethod]
    public void 多安全方案_优先选择bearer不依赖字典顺序()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Auth", "version": "1" },
          "paths": {
            "/x": { "get": { "tags": ["X"], "responses": { "200": { "description": "ok" } } } }
          },
          "components": {
            "securitySchemes": {
              "apiKeyAuth": { "type": "apiKey", "name": "X-Key", "in": "header" },
              "bearerAuth": { "type": "http", "scheme": "bearer" }
            }
          }
        }
        """;

        var catalog = OpenApiParser.Parse(spec, "Auth.Client", baseUrl: null, forcedScheme: null);
        Assert.AreEqual("bearer", catalog.AuthScheme);
    }

    [TestMethod]
    public void 内联枚举_同定义复用顶层枚举_异定义新建且产物编译零错()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "InlineEnum", "version": "1" },
          "paths": {
            "/w": { "get": {
              "tags": ["Widget"], "operationId": "getWidget",
              "parameters": [ { "name": "mode", "in": "query", "required": false, "schema": { "type": "string", "enum": ["fast", "slow"] } } ],
              "responses": { "200": { "description": "ok", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/Widget" } } } } }
            } }
          },
          "components": {
            "schemas": {
              "Mode": { "type": "string", "enum": ["fast", "slow"] },
              "Widget": { "type": "object", "properties": {
                "mode": { "type": "string", "enum": ["fast", "slow"] },
                "color": { "type": "string", "enum": ["red", "blue"] }
              } }
            }
          }
        }
        """;

        var (catalog, files) = Generate(spec);

        var enumNames = catalog.Enums.Select(e => e.Name).ToList();
        CollectionAssert.Contains(enumNames, "Mode");
        CollectionAssert.Contains(enumNames, "WidgetColor");       // 异定义内联枚举→按模型+属性名新建
        Assert.IsFalse(enumNames.Contains("WidgetMode"), "与顶层 Mode 同定义的内联枚举应复用而非新建");

        var widget = catalog.Models.Single(m => m.Name == "Widget");
        Assert.AreEqual("Mode", widget.Properties.Single(p => p.Name == "Mode").Type);         // 属性复用
        Assert.AreEqual("WidgetColor", widget.Properties.Single(p => p.Name == "Color").Type);

        var op = catalog.Clients.Single().Operations.Single();
        Assert.AreEqual("Mode", op.QueryParams.Single(q => q.Name == "mode").Type);            // 查询参数复用

        var errors = SnapshotCompiler.Errors(SnapshotCompiler.Compile(files));
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors.Select(e => e.ToString())));
    }

    [TestMethod]
    public void 草稿3_1可空联合与oneOf_anyOf映射()
    {
        const string spec = """
        {
          "openapi": "3.1.0",
          "info": { "title": "NullableUnion", "version": "1" },
          "components": {
            "schemas": {
              "Thing": {
                "type": "object",
                "required": ["note"],
                "properties": {
                  "note": { "type": ["string", "null"] },
                  "count": { "type": ["integer", "null"] },
                  "singleAlt": { "oneOf": [ { "type": "null" }, { "type": "string" } ] },
                  "multiAlt": { "oneOf": [ { "type": "string" }, { "type": "integer" } ] }
                }
              }
            }
          }
        }
        """;

        var (catalog, files) = Generate(spec);
        var thing = catalog.Models.Single(m => m.Name == "Thing");

        // ["string","null"] → string 且可空（虽 required 仍因 nullable 联合而可空）
        var note = thing.Properties.Single(p => p.Name == "Note");
        Assert.AreEqual("string", note.Type);
        Assert.IsTrue(note.IsNullable);

        // ["integer","null"] → int 且可空
        var count = thing.Properties.Single(p => p.Name == "Count");
        Assert.AreEqual("int", count.Type);
        Assert.IsTrue(count.IsNullable);

        // oneOf 非空成员同为 string（含 null 占位）→ 收敛为 string
        Assert.AreEqual("string", thing.Properties.Single(p => p.Name == "SingleAlt").Type);

        // oneOf 多类型 → 无法用单一 C# 类型表达 → 退回 object
        Assert.AreEqual("object", thing.Properties.Single(p => p.Name == "MultiAlt").Type);

        var errors = SnapshotCompiler.Errors(SnapshotCompiler.Compile(files));
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors.Select(e => e.ToString())));
    }

    [TestMethod]
    public void 数组查询参数按style与explode生成对应序列化()
    {
        const string spec = """
        {
          "openapi": "3.0.0",
          "info": { "title": "Q", "version": "1" },
          "paths": {
            "/things": {
              "get": {
                "tags": ["Things"],
                "operationId": "listThings",
                "parameters": [
                  { "name": "ids", "in": "query", "schema": { "type": "array", "items": { "type": "integer" } } },
                  { "name": "ids2", "in": "query", "style": "form", "explode": false, "schema": { "type": "array", "items": { "type": "integer" } } },
                  { "name": "tags", "in": "query", "style": "spaceDelimited", "schema": { "type": "array", "items": { "type": "string" } } },
                  { "name": "fields", "in": "query", "style": "pipeDelimited", "schema": { "type": "array", "items": { "type": "string" } } }
                ],
                "responses": { "200": { "description": "ok" } }
              }
            }
          }
        }
        """;

        var (_, files) = Generate(spec);
        var client = files["Things/ThingsClient.cs"];

        // 未声明 style → 规范默认 form + explode=true（重复键）
        StringAssert.Contains(client, "(\"ids\", ids, \"form\", true)");
        // 显式 form + explode=false → 逗号单键
        StringAssert.Contains(client, "(\"ids2\", ids2, \"form\", false)");
        // spaceDelimited / pipeDelimited → 单键连接
        StringAssert.Contains(client, "(\"tags\", tags, \"spaceDelimited\", false)");
        StringAssert.Contains(client, "(\"fields\", fields, \"pipeDelimited\", false)");

        var errors = SnapshotCompiler.Errors(SnapshotCompiler.Compile(files));
        Assert.AreEqual(0, errors.Count, string.Join("\n", errors.Select(e => e.ToString())));
    }
}