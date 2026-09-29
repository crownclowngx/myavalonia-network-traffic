using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>真实适配器测试夹具。客户端显式代理且不绕过回环，严格验证本轮根和目标名称。</summary>
public sealed class ProxyFixture : IAsyncDisposable
{
    public readonly TitaniumProxyRuntime Runtime = new();
    private X509Certificate2? _clientRoot;
    public ProxyRuntimeInfo Info { get; private set; } = null!;
    public async Task StartAsync(ProxyOptions options)
    {
        Info = await Runtime.StartAsync(options, CancellationToken.None);
        _clientRoot = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(Info.PublicRootCertificate));
    }
    public HttpClient Client(bool trust = true)
    {
        var handler = new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{Info.Port}") { BypassProxyOnLocal = false },
            UseProxy = true, AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None,
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = trust ? (_, certificate, _, errors) => ProxyCertificates.ValidateWithRoot(certificate, errors, _clientRoot!) : null
            }
        };
        return new(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }
    public async ValueTask DisposeAsync()
    {
        await Runtime.DisposeAsync();
        _clientRoot?.Dispose();
    }
    public static string PublicRoot(X509Certificate2 root) => Convert.ToBase64String(root.Export(X509ContentType.Cert));
    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
