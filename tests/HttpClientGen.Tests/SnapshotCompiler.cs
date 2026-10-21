using System.Reflection;
using HttpClientGen.Core.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace HttpClientGen.Tests;

/// <summary>
/// 把生成的 .cs 产物在内存中用 Roslyn 整体编译，并在语义层断言关键类型/方法存在。
/// 无需 dotnet CLI，纯内存验证生成代码可编译且结构正确。
/// </summary>
internal static class SnapshotCompiler
{
    public static CSharpCompilation Compile(IReadOnlyDictionary<string, string> files)
    {
        var refs = new List<MetadataReference>();

        // 信任平台程序集（System.*, System.Net.Http, System.Text.Json 等）
        var trusted = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? string.Empty)
            .Split(Path.PathSeparator)
            .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        refs.AddRange(trusted);

        // AddHttpClient 扩展所在程序集
        refs.Add(MetadataReference.CreateFromFile(typeof(
            Microsoft.Extensions.DependencyInjection.HttpClientFactoryServiceCollectionExtensions).Assembly.Location));
        refs.Add(MetadataReference.CreateFromFile(typeof(
            Microsoft.Extensions.DependencyInjection.IServiceCollection).Assembly.Location));

        var trees = files
            .Select(kv => CSharpSyntaxTree.ParseText(kv.Value, new CSharpParseOptions(LanguageVersion.Preview)))
            .ToList();

        return CSharpCompilation.Create(
            "GeneratedSnapshot",
            trees,
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    public static IReadOnlyList<Diagnostic> Errors(Compilation compilation)
        => compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();

    /// <summary>收集所有类型符号（类/接口/枚举，含嵌套）。</summary>
    public static IReadOnlyList<ITypeSymbol> AllTypes(Compilation compilation)
    {
        var types = new List<ITypeSymbol>();
        foreach (var tree in compilation.SyntaxTrees)
        {
            var sm = compilation.GetSemanticModel(tree);
            var declared = tree.GetRoot()
                .DescendantNodes()
                .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeDeclarationSyntax>()
                .Select(n => sm.GetDeclaredSymbol(n))
                .OfType<ITypeSymbol>();
            types.AddRange(declared);
        }
        return types;
    }

    /// <summary>查找静态类上的方法（忽略所在类型名）。</summary>
    public static IMethodSymbol? FindStaticMethod(Compilation compilation, string methodName)
        => AllTypes(compilation)
            .Where(t => t.IsStatic)
            .SelectMany(t => t.GetMembers().OfType<IMethodSymbol>())
            .FirstOrDefault(m => m.Name == methodName);
}