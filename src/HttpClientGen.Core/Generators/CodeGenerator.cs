using HttpClientGen.Core.Models;
using HttpClientGen.Core.Rendering;

namespace HttpClientGen.Core.Generators;

/// <summary>将解析后的 <see cref="ApiCatalog"/> 渲染为若干 .cs 文件内容。</summary>
public sealed class CodeGenerator : IDisposable
{
    private readonly RazorRenderer _renderer = new();

    /// <summary>生成所有文件，返回 相对路径 → 内容。</summary>
    public IReadOnlyDictionary<string, string> Generate(ApiCatalog catalog)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 收集所有模块名（去重排序）
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in catalog.Clients) modules.Add(c.Module);
        foreach (var m in catalog.Models) modules.Add(m.Module);
        foreach (var e in catalog.Enums) modules.Add(e.Module);
        modules.Remove("Shared");
        var sortedModules = modules.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var rootNs = catalog.Namespace;

        // 构建 ModuleUsings（根 + Shared + 各模块）
        var moduleUsings = new List<string>(sortedModules.Count + 2) { rootNs };
        if (catalog.Models.Any(m => m.Module == "Shared") || catalog.Enums.Any(e => e.Module == "Shared"))
            moduleUsings.Add(rootNs + ".Shared");
        moduleUsings.AddRange(sortedModules.Select(m => rootNs + "." + m));

        // 按模块分组模型/枚举
        var modelsByModule = catalog.Models.GroupBy(m => m.Module)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var enumsByModule = catalog.Enums.GroupBy(e => e.Module)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // 每个模块生成 Models.cs
        foreach (var mod in sortedModules.Append("Shared"))
        {
            var modModels = modelsByModule.GetValueOrDefault(mod, new List<ApiModel>());
            var modEnums = enumsByModule.GetValueOrDefault(mod, new List<ApiEnum>());
            if (modModels.Count == 0 && modEnums.Count == 0) continue;

            var ns = mod == "Shared" ? rootNs + ".Shared" : rootNs + "." + mod;
            files[$"{mod}/{mod}Models.cs"] = _renderer.Render("Models", new ModelsTemplateModel
            {
                Namespace = ns,
                Models = modModels,
                Enums = modEnums,
                ModuleUsings = moduleUsings,
            });
        }

        // 每个客户端
        foreach (var client in catalog.Clients)
        {
            var ns = rootNs + "." + client.Module;
            files[$"{client.Module}/{client.Name}.cs"] = _renderer.Render("Client", new ClientTemplateModel
            {
                Catalog = catalog,
                Client = client,
                Namespace = ns,
                ModuleUsings = moduleUsings,
            });
        }

        // 根命名空间的 DI 扩展
        files["ServiceCollectionExtensions.cs"] = _renderer.Render("ServiceCollectionExtensions",
            new ExtensionsTemplateModel { Catalog = catalog, ModuleUsings = moduleUsings });

        return files;
    }

    public void Dispose() => _renderer.Dispose();
}