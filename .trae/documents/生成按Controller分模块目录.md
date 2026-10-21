# 按 Controller(Tag) 拆分生成目录与命名空间

## Context（背景）
当前 `CodeGenerator` 把生成产物全部平铺在 `-o` 输出目录：
- `Models.cs`：所有 DTO + 枚举，全部集中在根命名空间
- `{Tag}Client.cs` 每个 tag 一个，也在根命名空间
- `ServiceCollectionExtensions.cs` 根命名空间

用户希望**每个模块(Controller/Tag)命名空间独立、独立子目录，方便整个模块移动复用**。方案：按 tag 分组，每个 tag 一个子目录 + 子命名空间；被多模块共用、或仅作为嵌套属性的模型归入 `Shared` 子命名空间。

## 目标目录/命名空间结构
以根命名空间 `Custom.ApiClient`、模块 `SaleOrder`、`StoreAllot` 为例：
```
out/
  SaleOrder/                 # namespace Custom.ApiClient.SaleOrder
      SaleOrderClient.cs        # 接口 + 客户端
      SaleOrderModels.cs        # 该模块的 DTO + 枚举
  StoreAllot/
      StoreAllotClient.cs
      StoreAllotModels.cs
  Shared/                    # namespace Custom.ApiClient.Shared
      SharedModels.cs           # 共用/嵌套模型
  ServiceCollectionExtensions.cs   # namespace Custom.ApiClient（根）
```

## 模块归属判断规则
- 每个 `ClientClass`（即 tag/Controller）→ 其模块名 = `Pascal(tag)`。
- 每个模型/枚举的模块归属由「是否被某 tag 的**顶层引用**」决定：
  1. 遍历所有 operation，收集每个 tag 直接引用的 schema（响应、请求体、参数的最近 `$ref` id）。
  2. 一个 schema 只被**单个** tag 顶层引用 → 归该 tag 模块。
  3. 被**多个** tag 引用、或从未被任何 tag 顶层引用（仅作为嵌套属性出现）→ 归 `Shared`。
- 跨模块引用防护：每个生成的 `.cs` 文件顶部统一 `using` 根命名空间 + `Shared` + 全部模块命名空间（生成代码已 `#pragma warning disable`，冗余 using 无害，但保证任何跨模块引用都能编译）。

## 文件改动

### 1. `Models/ApiCatalog.cs`
- `ApiModel`、`ApiEnum` 增加 `public string Module { get; set; } = "Shared";`
- `ClientClass` 增加 `public string Module { get; set; } = string.Empty;`

### 2. `Parsing/OpenApiParser.cs`
- 新增私有辅助 `NearestRef(OpenApiSchema?)`：解包 `array`(Items)、`allOf` 后返回最近的 `$ref` 的 `Id`，用于收集顶层引用。
- Parse 中、登记模型之前，新增一次扫 **doc.Paths** 的遍历构建 `Dictionary<string,string> svcModules`（schemaKey → 模块名），沿用 `BuildClients` 的 tag 分组键（`operation.Tags?.FirstOrDefault()?.Name ?? FirstSegment(path)` → `Pascal`）。
  - 对每个操作的 `op.Responses[*].Content[*].Schema`、`op.RequestBody.Content[*].Schema`、`op.Parameters[*].Schema` 调 `NearestRef`，记入该 tag 的引用集合；最终单 tag → 该模块，否则 Shared。
- 登记模型/枚举处：`BuildModel(...)` 与枚举创建时传入 `Module = svcModules.GetValueOrDefault(name, "Shared")`，写入 `ApiModel.Module` / `ApiEnum.Module`。
- `BuildClients` 中给 `ClientClass.Module = ToPascal(groupKey)`。

### 3. `Rendering/TemplateModels.cs`
- `ClientTemplateModel` 增加 `public required string Namespace { get; init; }` 与 `public required IReadOnlyList<string> ModuleUsings { get; init; }`
- `ExtensionsTemplateModel` 增加 `public required IReadOnlyList<string> ModuleUsings { get; init; }`
- 新增 `ModelsTemplateModel { required string Namespace; required List<ApiModel> Models; required List<ApiEnum> Enums; required IReadOnlyList<string> ModuleUsings; }`

### 4. `Templates/Models.cshtml`
- 接收 `ModelsTemplateModel`；`namespace @(Model.Namespace)`；循环改 `Model.Models` / `Model.Enums`；顶部追加 `ModuleUsings` 的 using 行（含根/Shared/各模块）。

### 5. `Templates/Client.cshtml`
- `namespace @(Model.Namespace)`（= `根.模块`）；顶部追加 `ModuleUsings`（含 `using 根;`、`using 根.Shared;`、各模块）。其余逻辑（auth、SendAsync、BuildUrl）不变。

### 6. `Templates/ServiceCollectionExtensions.cshtml`
- 命名空间保持 `@Model.Catalog.Namespace`（根）；顶部追加 `ModuleUsings`，使 `CodeHelper.AddClient` 里未限定的 `I{Name},{Name}` 能在根下解析到各模块子命名空间。AddClient 逻辑不动。

### 7. `Generators/CodeGenerator.cs`（核心拆分）
- 先计算 `moduleNamespaces`：`Shared` + `Clients.Select(Module)` + 模型/枚举的 Module 集合，去重排序。
- 对 `catalog.Models`/`catalog.Enums` 按 `Module` 分组：
  - 非 `Shared` 模块有模型时 → `files[$"{module}/{module}Models.cs"] = Render("Models", new ModelsTemplateModel{...})`
  - `Shared` 有模型时 → `files["Shared/SharedModels.cs"] = Render(...)`
- 每个 client → `files[$"{client.Module}/{client.Name}.cs"] = Render("Client", new ClientTemplateModel{ Namespace=根.Module, ModuleUsings, Catalog, Client })`
- `files["ServiceCollectionExtensions.cs"] = Render(..., ModuleUsings=moduleNamespaces)`
- 返回键即相对路径（`模块/文件.cs`）。

### 8. `Program.cs`
- 无需逻辑改动：写文件前对父目录 `Directory.CreateDirectory(Path.GetDirectoryName(path))`（`Generate` 返回键已含子目录路径）。

## 验证
1. `dotnet build` 主项目通过。
2. 用 `D:\ChromeDownload\swagger.json` 重新生成到临时输出，检查：
   - 产生 21 个模块目录 + `Shared/` + `ServiceCollectionExtensions.cs`，每个模块内 `XXClient.cs` + `XXModels.cs`（或无模型则不生成 Models）。
   - 每个 `.cs` 顶部的 using 含根/Shared/各模块。
3. 独立 `dotnet new classlib` 验证工程整体编译 **0 错误 0 警告**（沿用现有 verify 流程）。
4. 回归本地 `samples/petstore.json`（pet/store/user 三模块 + Shared 或空 Shared 均不报错）。