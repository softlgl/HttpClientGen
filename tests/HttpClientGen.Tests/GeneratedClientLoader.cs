using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;

namespace HttpClientGen.Tests;

/// <summary>
/// 端到端加载器：用 HttpClientGen CLI 从真实 MockApi 的 OpenAPI 地址生成客户端源码
/// → Roslyn 编译为 dll → 装入独立 AssemblyLoadContext，并提供反射调用辅助。
/// </summary>
internal sealed class GeneratedClientLoader : IDisposable
{
    private readonly AssemblyLoadContext _alc;
    private readonly string _tempDir;

    public Assembly Assembly { get; }

    private GeneratedClientLoader(AssemblyLoadContext alc, Assembly assembly, string tempDir)
    {
        _alc = alc;
        Assembly = assembly;
        _tempDir = tempDir;
    }

    /// <summary>从 HTTP swagger 地址生成并装载客户端程序集。</summary>
    public static GeneratedClientLoader LoadFromUrl(string openapiUrl, string @namespace)
    {
        var temp = Path.Combine(Path.GetTempPath(), "HttpClientGenTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            GenerateWithCli(openapiUrl, @namespace, temp);

            // 生成物按模块分布于子目录，收集所有 .cs
            var files = Directory.GetFiles(temp, "*.cs", SearchOption.AllDirectories)
                .ToDictionary(f => Path.GetFileName(f)!, File.ReadAllText);
            AssertNonEmpty(files);

            var compilation = SnapshotCompiler.Compile(files);
            var errors = SnapshotCompiler.Errors(compilation);
            if (errors.Count > 0)
                throw new InvalidOperationException(
                    $"生成代码编译失败：\n{string.Join("\n", errors.Take(10).Select(e => e.ToString()))}");

            using var ms = new MemoryStream();
            var emit = compilation.Emit(ms);
            if (!emit.Success)
                throw new InvalidOperationException(
                    $"生成代码 Emit 失败：\n{string.Join("\n", emit.Diagnostics.Take(10))}");

            ms.Position = 0;
            var alc = new AssemblyLoadContext("gen_" + Guid.NewGuid().ToString("N"), isCollectible: true);
            var asm = alc.LoadFromStream(ms);
            return new GeneratedClientLoader(alc, asm, temp);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void AssertNonEmpty(IReadOnlyDictionary<string, string> files)
    {
        if (files.Count == 0)
            throw new InvalidOperationException("CLI 未生成任何客户端源码");
    }

    private static void GenerateWithCli(string openapiUrl, string @namespace, string outDir)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(RepoLocator.CliDll);
        psi.ArgumentList.Add(openapiUrl);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outDir);
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(@namespace);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("启动 HttpClientGen 失败");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"HttpClientGen 生成失败（退出码 {p.ExitCode}）：\n{stdout}\n{stderr}");
    }

    /// <summary>实例化生成的具体客户端（构造注入 HttpClient）。</summary>
    public object CreateClient(string typeName, HttpClient http)
    {
        var type = Assembly.GetType(typeName, throwOnError: true)!;
        var ctor = type.GetConstructor(new[] { typeof(HttpClient) })
            ?? throw new MissingMethodException(typeName, ".ctor(HttpClient)");
        return ctor.Invoke(new object[] { http });
    }

    /// <summary>创建生成的 DTO（如 NewPet），并用 setter 注入字段。</summary>
    public object CreateDto(string typeName, Action<object> setter)
    {
        var type = Assembly.GetType(typeName, throwOnError: true)!;
        var dto = Activator.CreateInstance(type)!;
        setter(dto);
        return dto;
    }

    /// <summary>反射调用客户端方法并 await，返回 Task 的 Result（无返回值时返回 null）。</summary>
    public static async Task<object?> InvokeAsync(object target, string method, params object?[] args)
    {
        var mi = target.GetType().GetMethod(method)
            ?? throw new MissingMethodException(target.GetType().FullName, method);
        var task = (Task)mi.Invoke(target, args)!;
        await task.ConfigureAwait(false);
        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    /// <summary>读取对象属性值（含枚举等任意属性）。</summary>
    public static object? Prop(object obj, string property)
        => obj.GetType().GetProperty(property)?.GetValue(obj);

    public void Dispose()
    {
        TryDelete(_tempDir);
        _alc.Unload();
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* 忽略清理失败 */ }
    }
}