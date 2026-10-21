using HttpClientGen.Core.Models;

namespace HttpClientGen.Core.Rendering;

/// <summary>单个客户端模板的渲染模型。</summary>
public sealed class ClientTemplateModel
{
    public required ApiCatalog Catalog { get; init; }
    public required ClientClass Client { get; init; }

    /// <summary>当前命名空间（根.模块）。</summary>
    public required string Namespace { get; init; }

    /// <summary>需要 using 的命名空间（根 / Shared / 各模块）。</summary>
    public required IReadOnlyList<string> ModuleUsings { get; init; }
}

/// <summary>顶层 ServiceCollection 扩展模板的渲染模型。</summary>
public sealed class ExtensionsTemplateModel
{
    public required ApiCatalog Catalog { get; init; }

    /// <summary>需要 using 的模块命名空间。</summary>
    public required IReadOnlyList<string> ModuleUsings { get; init; }
}

/// <summary>单个模块的模型/枚举模板渲染模型。</summary>
public sealed class ModelsTemplateModel
{
    public required string Namespace { get; init; }
    public required List<ApiModel> Models { get; init; }
    public required List<ApiEnum> Enums { get; init; }
    public required IReadOnlyList<string> ModuleUsings { get; init; }
}