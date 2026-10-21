using System.Text;
using CommandLine;
using HttpClientGen.Core.Generators;
using HttpClientGen.Core.Parsing;

return await Parser.Default.ParseArguments<Options>(args)
    .MapResult(
        opts => Run(opts),
        _ => Task.FromResult(1));

static async Task<int> Run(Options o)
{
    string json;
    try
    {
        json = await LoadSpecAsync(o.Spec);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"读取规格失败：{ex.Message}");
        return 2;
    }

    try
    {
        var catalog = OpenApiParser.Parse(json, o.Namespace, o.BaseUrl, o.Scheme);

        var dir = Path.GetFullPath(o.Output);
        Directory.CreateDirectory(dir);

        int count = 0;
        using (var generator = new CodeGenerator())
        {
            var files = generator.Generate(catalog);
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(dir, name);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                await File.WriteAllTextAsync(path, content, Encoding.UTF8);
                Console.WriteLine($"  ✓ {name}");
                count++;
            }
        }

        var auth = catalog.RequiresAuth ? $"{catalog.AuthScheme} ({catalog.ApiKeyLocation ?? "n/a"})" : "无";
        Console.WriteLine($"\n完成：API {catalog.Title} v{catalog.Version}");
        Console.WriteLine($"  模型 {catalog.Models.Count} 个 / 枚举 {catalog.Enums.Count} 个 / 客户端 {catalog.Clients.Count} 个");
        Console.WriteLine($"  鉴权：{auth}　输出：{dir}　生成文件 {count} 个");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"生成失败：{ex.Message}");
        Console.Error.WriteLine(ex);
        return 1;
    }
}

/// <summary>按输入加载 OpenAPI 文档内容：本地文件或 http(s) 远程地址均可。</summary>
static async Task<string> LoadSpecAsync(string source)
{
    if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HttpClientGen/1.0");
        var resp = await client.GetAsync(uri);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    if (!File.Exists(source))
        throw new FileNotFoundException($"未找到文件：{source}");
    return await File.ReadAllTextAsync(source, Encoding.UTF8);
}

internal sealed class Options
{
    [Value(0, MetaName = "spec", Required = true, HelpText = "Swagger/OpenAPI 来源：本地 JSON 路径或 http(s) 远程地址")]
    public string Spec { get; set; } = string.Empty;

    [Option('o', "output", Default = "Generated", HelpText = "输出目录")]
    public string Output { get; set; } = "Generated";

    [Option('n', "namespace", Default = "Generated.ApiClient", HelpText = "生成代码的命名空间")]
    public string Namespace { get; set; } = "Generated.ApiClient";

    [Option('u', "base-url", HelpText = "基础地址（默认取 OpenAPI servers）")]
    public string? BaseUrl { get; set; }

    [Option('s', "scheme", HelpText = "强制鉴权方案：bearer | apikey")]
    public string? Scheme { get; set; }
}