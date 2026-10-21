namespace HttpClientGen.Tests;

/// <summary>
/// 从测试输出目录向上定位仓库根（含 HttpClientGen.slnx），再推算各项目 bin 输出路径。
/// 供真实进程测试启动 MockApi 与 HttpClientGen CLI 使用。
/// </summary>
internal static class RepoLocator
{
    public static string Root { get; } = FindRoot();

    public static string MockApiDll =>
        Path.Combine(Root, "samples", "MockApi", "bin", "Debug", "net10.0", "MockApi.dll");

    public static string CliDll =>
        Path.Combine(Root, "src", "HttpClientGen", "bin", "Debug", "net10.0", "HttpClientGen.dll");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "HttpClientGen.slnx")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("未找到仓库根（缺少 HttpClientGen.slnx）");
    }
}