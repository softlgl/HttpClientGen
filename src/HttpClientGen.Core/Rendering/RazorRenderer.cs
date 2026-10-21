using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HttpClientGen.Core.Rendering;

/// <summary>
/// 运行时 Razor 渲染引擎：将嵌入的 .cshtml 模板编译为程序集并渲染输出 C# 源码文本。
/// 模板仅使用 @{}、@()、@if/@foreach 等核心语法（不使用 @using/@functions/@inherits 等 MVC 指令），
/// 基础类型通过 SetBaseType 按模板模型注入。
/// </summary>
public sealed class RazorRenderer : IDisposable
{
    private readonly string _tempDir;
    private readonly RazorProjectFileSystem _fileSystem;
    private readonly IReadOnlyList<MetadataReference> _references;

    // 同一「模板名 + 模型基类」的编译产物可复用：渲染输出仅取决于运行时注入的 Model，
    // 编译出的类型本身与具体 Model 无关，故只需编译一次（多模块/多客户端场景收益显著）。
    private readonly ConcurrentDictionary<string, Type> _typeCache = new(StringComparer.Ordinal);
    // 每个已编译模板装入独立可回收上下文，Dispose 时统一卸载，避免长期进程累积程序集。
    private readonly List<AssemblyLoadContext> _loadContexts = new();

    public RazorRenderer()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HttpClientGen", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "Views"));
        _fileSystem = RazorProjectFileSystem.Create(_tempDir);
        _references = BuildReferences();
    }

    private static IReadOnlyList<MetadataReference> BuildReferences()
    {
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // 保证模板程序集能引用到我们的 TemplateBase
        trusted.Add(MetadataReference.CreateFromFile(typeof(TemplateBase).Assembly.Location));
        return trusted;
    }

    /// <summary>渲染指定模板，返回生成的文本。</summary>
    public string Render<TModel>(string templateName, TModel model)
        where TModel : class
        => RenderInternal(templateName, BaseTypeName(typeof(TemplateBase<>).MakeGenericType(typeof(TModel))), model);

    public string Render(string templateName, object? model = null)
        => RenderInternal(templateName, ResolveBaseType(model), model);

    private static string ResolveBaseType(object? model)
        => model is null ? typeof(TemplateBase).FullName! : BaseTypeName(typeof(TemplateBase<>).MakeGenericType(model.GetType()));

    /// <summary>将泛型模板基类转成合法的 C# 源码语法（避免 Razor 写出 `` `1[[程序集限定]] `` 形式导致编译失败）。</summary>
    private static string BaseTypeName(Type type)
    {
        var full = type.FullName!; // 例：...TemplateBase`1[[...]] 使用全限定泛型定义名解析
        var open = full.Split('`')[0];
        if (!full.Contains("[["))
            return full;
        var args = type.GetGenericArguments().Select(a => a.FullName);
        return $"{open}<{string.Join(", ", args)}>";
    }

    private string RenderInternal(string templateName, string baseTypeFullName, object? model)
    {
        // 命中缓存则跳过 Razor 处理 + Roslyn 编译，直接实例化并注入 Model 渲染
        var key = templateName + "|" + baseTypeFullName;
        var type = _typeCache.GetOrAdd(key, _ => CompileTemplateType(templateName, baseTypeFullName));

        if (Activator.CreateInstance(type) is not TemplateBase instance)
            throw new InvalidOperationException($"模板实例化失败：{type.FullName}");

        if (model is not null)
        {
            var modelProp = type.GetProperty("Model");
            modelProp?.SetValue(instance, model, null);
        }

        instance.ExecuteAsync().GetAwaiter().GetResult();
        return instance.Result;
    }

    /// <summary>将模板经 Razor 处理并 Roslyn 编译为可复用类型，装入独立可回收上下文。</summary>
    private Type CompileTemplateType(string templateName, string baseTypeFullName)
    {
        var filePath = Path.Combine(_tempDir, "Views", templateName + ".cshtml");
        File.WriteAllText(filePath, LoadTemplate(templateName));

        var engine = RazorProjectEngine.Create(
            RazorConfiguration.Default,
            _fileSystem,
            builder => builder.SetBaseType(baseTypeFullName));

        var projectItem = _fileSystem.GetItem($"Views/{templateName}.cshtml", fileKind: null);
        var codeDoc = engine.Process(projectItem);
        var generated = codeDoc.GetCSharpDocument();
        if (generated.Diagnostics.Any(d => d.Severity == RazorDiagnosticSeverity.Error))
        {
            throw new InvalidOperationException(
                $"Razor 模板 {templateName} 生成失败：\n{string.Join("\n", generated.Diagnostics)}");
        }

        var syntaxTree = CSharpSyntaxTree.ParseText(StripHostingAttributes(generated.GeneratedCode));
        var compilation = CSharpCompilation.Create(
            // 唯一程序集名，避免同名不同内容的模板程序集在加载时发生标识冲突而返回错误类型
            assemblyName: $"HttpClientGen.Templates.{Guid.NewGuid():N}",
            references: _references,
            syntaxTrees: new[] { syntaxTree },
            options: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                optimizationLevel: OptimizationLevel.Release,
                assemblyIdentityComparer: AssemblyIdentityComparer.Default));

        using var ms = new MemoryStream();
        var emit = compilation.Emit(ms);
        if (!emit.Success)
        {
            var errs = string.Join("\n", emit.Diagnostics.Take(30));
            throw new InvalidOperationException($"模板 {templateName} 编译失败：\n{errs}\n\n生成的 C#：\n{generated.GeneratedCode}");
        }

        // 独立可回收上下文：共享依赖（TemplateBase / System.* 等）统一交由 Default 上下文解析，
        // 保证跨上下文的类型标识一致（typeof(TemplateBase).IsAssignableFrom 才成立）。
        var alc = new SharedDependencyLoadContext();
        ms.Position = 0;
        var assembly = alc.LoadFromStream(ms);
        lock (_loadContexts) _loadContexts.Add(alc);

        return assembly.GetTypes().First(t => typeof(TemplateBase).IsAssignableFrom(t) && !t.IsAbstract);
    }

    /// <summary>移除 Razor 生成的 hosting 特性，避免当前环境缺少 Microsoft.AspNetCore.Razor.Hosting 的运行时编译错误。</summary>
    private static string StripHostingAttributes(string code)
    {
        var lines = code.Split('\n')
            .Where(line => !line.Contains("[assembly: global::Microsoft.AspNetCore.Razor.Hosting.")
                        && !line.Contains("global::Microsoft.AspNetCore.Razor.Hosting."));
        return string.Join("\n", lines);
    }

    private static string LoadTemplate(string templateName)
    {
        var assembly = typeof(RazorRenderer).Assembly;
        var names = assembly.GetManifestResourceNames();
        var full = names.FirstOrDefault(n => n.EndsWith($".{templateName}.cshtml", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException($"嵌入模板未找到：{templateName}。可用资源：{string.Join(",", names)}");

        using var stream = assembly.GetManifestResourceStream(full)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        _typeCache.Clear();
        lock (_loadContexts)
        {
            foreach (var alc in _loadContexts)
            {
                try { alc.Unload(); } catch { /* 忽略卸载失败 */ }
            }
            _loadContexts.Clear();
        }
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* 忽略清理失败 */ }
    }

    /// <summary>可回收上下文：自身只承载编译出的模板程序集，其余依赖回退到 Default 上下文以保证类型一致。</summary>
    private sealed class SharedDependencyLoadContext : AssemblyLoadContext
    {
        public SharedDependencyLoadContext() : base(isCollectible: true) { }

        protected override Assembly Load(AssemblyName assemblyName)
            => Default.LoadFromAssemblyName(assemblyName);
    }
}