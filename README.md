# HttpClientGen

基于 Swagger/OpenAPI 文档自动生成 **.NET HttpClientFactory** 类型化客户端代码的命令行工具。输入一份 OpenAPI 3.x 或 Swagger 2.0 的 JSON，即可产出接口、类型化客户端、DTO 模型与 DI 注册代码，开箱即用。

- **纯 .NET CLI**：无运行时模板依赖（Razor 模板运行时编译）
- **一次生成，直接编译**：产物独立工程 0 错误 0 警告
- **按 Controller 模块化**：一个 tag = 一个子目录 + 独立命名空间，模块可整体移动复用

## 特性

- **双版本支持**：OpenAPI 3.x 与 Swagger 2.0（含 3.1 类型联合可空自动降级为 `type` + `nullable`）
- **双来源输入**：本地 JSON 文件路径，或 `http(s)://` 远程地址（自动识别）
- **类型化客户端**：`I{T}Client` 接口 + `{T}Client` 实现，基于 `HttpClientFactory`
- **模块化输出**：每个 Controller(tag) 一个子目录与独立命名空间，公共模型归入 `Shared`
- **鉴权方案**：内置 Bearer / APIKey，生成 `SetBearerToken`、请求头加 `API Key`
- **完整 DI 集成**：自动生成 `ServiceCollectionExtensions`，批量注册所有客户端
- **健壮解析**：处理 ABP 等框架的泛型/类型名短化、枚举重名去重、可空性处理；参数/属性里的**内联枚举**会生成强类型枚举（同定义自动复用同名枚举，异定义按「模块+上下文」新建）；`oneOf`/`anyOf` 非空成员同型则收敛为该类型，否则退回 `object`
- **参数序列化**：数组/集合参数按 OpenAPI `style`/`explode` 序列化——查询默认 form 展开为重复键（`name=v1&name=v2`）、`form`+`explode=false` 逗号单键、`spaceDelimited`/`pipeDelimited` 单键连接；路径数组按逗号拼接；枚举按 JSON 线值输出（字符串枚举取管线名、整型枚举取数值），null 自动跳过

## 快速开始

### 构建

```bash
dotnet build src/HttpClientGen/HttpClientGen.csproj -c Release
```

> 目标框架（net10.0）、`Nullable`、`ImplicitUsings` 等统一在仓库根的 `Directory.Build.props` 中声明，各 `.csproj` 无需重复。提交/推送后由 `.github/workflows/ci.yml` 自动还原、构建与测试。

### 使用

```bash
dotnet src/HttpClientGen/bin/Release/net10.0/HttpClientGen.dll \
  <spec> \
  -o <outputDir> \
  -n <RootNamespace>
```

| 参数 | 说明 | 默认 |
|---|---|---|
| `spec` | Swagger 来源：本地 JSON 路径或 `http(s)` 地址 | 必填 |
| `-o, --output` | 输出目录 | `Generated` |
| `-n, --namespace` | 根命名空间 | `Generated.ApiClient` |
| `-u, --base-url` | 基础地址（覆盖 OpenAPI `servers`） | 取 servers |
| `-s, --scheme` | 强制鉴权：`bearer` / `apikey` | 自动推断 |

**本地文件：**

```bash
dotnet HttpClientGen.dll "D:\api\swagger.json" -o out -n Petstore.Client
```

**远程地址：**

```bash
dotnet HttpClientGen.dll "https://petstore.swagger.io/v2/swagger.json" -o out -n Petstore.Client
```

## 生成结构

按 Controller(Tag) 拆分，每个 tag 一个子目录与独立命名空间：

```
out/
  SaleOrder/                 # namespace Root.SaleOrder
      SaleOrderClient.cs         # 接口 I+客户端
      SaleOrderModels.cs         # 该模块的 DTO + 枚举
  StoreAllot/
      StoreAllotClient.cs
      StoreAllotModels.cs
  Shared/                    # namespace Root.Shared
      SharedModels.cs            # 跨模块公共模型
  ServiceCollectionExtensions.cs   # namespace Root（DI 注册）
```

模块归属规则：被单个 Controller 的业务闭包覆盖的类型归该模块；被多个 Controller 共用、或未被任何操作的模型归入 `Shared`。

## 使用生成代码

```csharp
// 注册生成的客户端
builder.Services.AddGeneratedClients();

// 注入使用
public class DemoService(IStoreAllotClient storeAllot)
{
    public async Task DoAsync()
    {
        await storeAllot.SetBearerToken("your.access.token");
        var result = await storeAllot.GetAllAsync(ct);
    }
}
```

## 项目结构

```
Directory.Build.props     # 仓库级集中构建配置（TargetFramework/Nullable/ImplicitUsings 等，各 csproj 不再重复）
.github/workflows/ci.yml  # CI：还原 → 构建 slnx → 跑测试（统一 Debug）
src/
  HttpClientGen/          # CLI 入口（参数解析、来源加载、输出写盘）
  HttpClientGen.Core/
      Models/             # 中间模型（ApiCatalog / ApiModel / ClientClass）
      Parsing/            # OpenAPI 解析（支持 2.0/3.x、类型短化、模块归属）
      Rendering/          # Razor 运行时渲染引擎 + CodeHelper
      Templates/          # .cshtml 模板（Models / Client / ServiceCollectionExtensions）
      Generators/         # CodeGenerator：编排渲染并按模块产出文件
samples/
  MockApi/                # 真实服务示例：暴露 OpenAPI 端点（/openapi/v1.json），无预生成客户端
tests/
  HttpClientGen.Tests/    # 单元（快照）测试 + 真实端到端测试
```

## 测试

`dotnet test` 运行两类测试：

- **快照测试**：对本地/远程 Swagger 文档解析并渲染客户端源码，比对期望输出，覆盖类型短化、模块归属、枚举去重等规则。
- **真实端到端测试**：真实启动 `samples/MockApi`（Kestrel 监听随机端口 → `/openapi/v1.json`）→ 用 HttpClientGen CLI 从该地址生成客户端 → Roslyn 编译装载 → 以反射调用生成的方法对真实服务发起 HTTP 请求，验证全链路与真实用户使用路径一致。

```bash
dotnet test tests/HttpClientGen.Tests/HttpClientGen.Tests.csproj
```

## 依赖

- [.NET 10](https://dotnet.microsoft.com/) SDK
- [CommandLineParser](https://github.com/commandlineparser/commandline)（CLI 参数）
- [Microsoft.OpenApi](https://github.com/microsoft/OpenAPI.NET)（OpenAPI 解析）
- [Microsoft.AspNetCore.Razor](https://www.nuget.org/packages/Microsoft.AspNetCore.Razor.Language)（模板编译）

## License

MIT