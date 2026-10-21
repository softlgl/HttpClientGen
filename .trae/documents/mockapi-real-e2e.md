# MockApi 真实启动 + Swagger 生成闭环保真

## Context

当前测试用 `WebApplicationFactory` 的**内存 TestServer** 验证真实调用，且 MockApi 里**预生成了一份客户端源码**（`samples/MockApi/Api/`）编译进去。这偏离了工具的真实用法：用户实际是从 **swagger 文档**用 CLI 生成代码，再接入真实运行的服务。

本次改造目标：把集成测试改成完全贴合真实使用路径的闭环——
1. `MockApi` **真实启动**（Kestrel 真实端口）
2. MockApi 暴露自身 **swagger（OpenAPI）**
3. 测试内用 **dotnet 调用 HttpClientGen.dll** 从该 swagger 生成调用代码
4. 动态编译并**真实 HTTP 调用**

并移除 MockApi 里的预生成源码（它只负责当服务），消除对预生成代码的依赖。核心生成逻辑与模板**不改**。

## 改造内容

### 1. samples/MockApi —— 变成"纯真实服务 + 自曝 swagger"

文件：`samples/MockApi/Program.cs`、`samples/MockApi/MockApi.csproj`

- **移除** `builder.Services.AddGeneratedClients(...)` 与 `ApiBaseUrl` 读取（宿主不再承载生成客户端）。
- **移除** `samples/MockApi/Api/` 预生成源码目录（DeleteFile）。
- **暴露 OpenAPI**：用 .NET 10 内置 OpenAPI（无需外部包）：
  - `builder.Services.AddOpenApi();`
  - `app.MapOpenApi();` → 默认 `/openapi/v1.json`
  - 端点改为**返回强类型**（框架可从返回类型推断 schema）：
    - `GET /pets` → `List<PetDto>`
    - `POST /pets` → `PetDto`（用 `.Produces<PetDto>(StatusCodes.Status201Created)` 显式）
    - `GET /pets/{petId}` / `DELETE /pets/{petId}` → 强类型 + `Produces`
    - `GET /stores/orders` → `List<OrderDto>`
  - 保留 `public partial class Program;`（进程启动需要入口）。

> 说明：用内置 `AddOpenApi` 而非 Swashbuckle，零额外依赖且产出标准 OpenAPI 3.x，生成器可直接解析。若你的 .NET 版本无 `AddOpenApi`，回退加 `Microsoft.AspNetCore.OpenApi` 包。

### 2. tests/HttpClientGen.Tests —— 重写真实调用测试

改动文件：
- `RealCallTests.cs`（重写）
- `MockApiFactory.cs`（删除，改用真实进程）
- `MockApiProcess.cs`（新增：进程启动/释放 + 等待就绪）
- `GeneratedClientLoader.cs`（新增：CLI 生成 → Roslyn 编译 dll → AssemblyLoadContext 装载 + 动态调用）
- `RepoLocator.cs`（新增：从测试输出目录向上找 `HttpClientGen.slnx`，定位各项目 bin 路径）
- `HttpClientGen.Tests.csproj`：移除 `Microsoft.AspNetCore.Mvc.Testing` 包（不再用 WebApplicationFactory）、移除对 MockApi 的 `ProjectReference`（不再静态引用其类型），改为用进程装载

**测试流程（每个真实调用用例）：**
1. 借助 `RepoLocator` 定位 `samples/MockApi/bin/Debug/net10.0/MockApi.dll` 与 `src/HttpClientGen/bin/Debug/net10.0/HttpClientGen.dll`。
2. 用 `TcpListener` 绑定端口 0 取一个空闲端口 `port`。
3. `Process.Start("dotnet", MockApi.dll --urls http://127.0.0.1:{port})`，轮询 `GET /openapi/v1.json` 直到 200 或超时；`Dispose` 时 `Kill` 进程。
4. `Process.Start("dotnet", HttpClientGen.dll <url> -o <tempDir> -n ClientNs)`，生成客户端源码到临时目录。
5. 复用现有 [SnapshotCompiler.Compile](file:///d:/MyProjects/HttpClientGen/tests/HttpClientGen.Tests/SnapshotCompiler.cs)（已含 AddHttpClient 编译引用）编译临时目录所有 `.cs`，断言 0 error，`Emit` 到内存流。
6. 用新 `AssemblyLoadContext` 装载 dll，`Activator.CreateInstance`(带 `HttpClient { BaseAddress = 真实端口 }`) 构造具体客户端，通过 `dynamic` 调用方法做真实请求，断言返回（Id/Name/Status 等）。
7. 释放 `AssemblyLoadContext`，`Kill` MockApi 进程。

> 复用：`SnapshotCompiler.Compile` + 其引用收集逻辑直接沿用；真实调用时生成代码中的 URL 是相对路径，构造 `HttpClient` 时设 `BaseAddress = http://127.0.0.1:{port}` 即可命中 MockApi。
> 动态调用用 `dynamic`（Microsoft.CSharp 在 TPA 中），返回数组强转 `object[]`，枚举值取其 `ToString()` 断言，避免反射地狱。

**覆盖用例**（对齐原 RealCallTests 关键点）：
- `GET /pets` 返回 2 条，首条 Id=1、Name=Rex、Status=Available
- `GET /pets?status=sold` 过滤生效
- `GET /pets/{id}` 命中返回、未命中抛 `HttpRequestException`
- `POST /pets` 创建后再次 GET 可读到（Id=3）
- `DELETE /pets/{id}` 后再次读取抛异常
- stores 客户端 `ListOrders` 返回订单

**保留不动**：`EndToEndSnapshotTests.cs`（纯生成/编译结构断言，不依赖 MockApi 类型）、`SnapshotCompiler.cs`、`Fixtures/petstore.json`。

### 3. 文档

- 更新 `README.md` 的"测试"一节：从"WebApplicationFactory 内存"改为"真实启动 MockApi + swagger + CLI 生成 + 真实调用"链路。

## 影响与取舍

- MockApi 不再 `AddGeneratedClients`、不再携带预生成源码 → 更贴近"纯服务"，但若有人想用 MockApi 直接演示注入生成客户端，需自行生成。
- 移除 `Microsoft.AspNetCore.Mvc.Testing` 依赖，测试依赖进程启停；新增等待就绪/端口申请逻辑。
- 采用内置 `AddOpenApi`，产出标准 OpenAPI 3.x。

## 验证方式

1. `dotnet build HttpClientGen.slnx` —— 全项目 0 错误。
2. `dotnet test tests/HttpClientGen.Tests/HttpClientGen.Tests.csproj`：
   - 快照单测（原 7 个）仍通过
   - 新的真实调用用例（进程 + swagger + CLI 生成 + dynamic 调用）通过
3. 手动冒烟：`dotnet run --project samples/MockApi --urls http://127.0.0.1:5005` 后浏览器访问 `/openapi/v1.json` 能看到合法 OpenAPI。