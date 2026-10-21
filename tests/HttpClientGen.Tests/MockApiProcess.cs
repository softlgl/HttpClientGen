using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace HttpClientGen.Tests;

/// <summary>
/// 真实启动 MockApi（Kestrel 监听空闲端口），等待 OpenAPI 文档就绪；释放时终止进程。
/// </summary>
internal sealed class MockApiProcess : IDisposable
{
    private readonly Process _process;

    public Uri BaseAddress { get; }

    private MockApiProcess(Process process, Uri baseAddress)
    {
        _process = process;
        BaseAddress = baseAddress;
    }

    public static MockApiProcess Start()
    {
        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";

        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(RepoLocator.MockApiDll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add(baseUrl);

        var process = Process.Start(psi) ?? throw new InvalidOperationException("启动 MockApi 失败");
        var uri = new Uri(baseUrl);
        WaitReady(uri);
        return new MockApiProcess(process, uri);
    }

    private static void WaitReady(Uri baseUrl)
    {
        var swagger = new Uri(baseUrl, "openapi/v1.json");
        var deadline = DateTime.UtcNow.AddSeconds(15);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (http.GetAsync(swagger).Result.StatusCode == HttpStatusCode.OK)
                    return;
            }
            catch
            {
                // 服务未就绪，重试
            }
            Thread.Sleep(200);
        }
        throw new TimeoutException($"MockApi 未能就绪：{swagger}");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public void Dispose()
    {
        if (!_process.HasExited)
        {
            try { _process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
            _process.Dispose();
        }
    }
}