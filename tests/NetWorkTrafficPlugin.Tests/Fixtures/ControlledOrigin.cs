using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;
using NetWorkTrafficPlugin.Infrastructure.Proxy;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

public sealed record OriginReceipt(string Target, string Method, string Path, string Host, string Protocol,
    string? Sni, string BodyHash, Dictionary<string, string[]> Headers);

/// <summary>真实回环端点；用流式生成器提供大资源，绝不为测试创建 1 GiB 数组。</summary>
public sealed class ControlledOrigin : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly X509Certificate2 _certificate;
    private readonly ConcurrentDictionary<string, string> _sni = [];
    public static readonly byte[] Block = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
    public readonly ConcurrentQueue<OriginReceipt> Receipts = [];
    private int _requestHeadersReceived;
    public int RequestHeadersReceived => Volatile.Read(ref _requestHeadersReceived);
    public readonly TaskCompletionSource ContinueStream = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly TaskCompletionSource StreamExited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Uri Address { get; private set; } = null!;

    private ControlledOrigin(string target, X509Certificate2 root, string certificateName, bool https, bool expired)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={certificateName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        if (IPAddress.TryParse(certificateName, out var address)) san.AddIpAddress(address); else san.AddDnsName(certificateName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        var from = DateTimeOffset.UtcNow.AddMinutes(-3);
        // 过期负例仍落在签发者有效期内；真实 TLS 时钟负责拒绝已过期的叶证书。
        using var signed = request.Create(root, from, expired ? DateTimeOffset.UtcNow.AddMinutes(-1) : DateTimeOffset.UtcNow.AddDays(1), RandomNumberGenerator.GetBytes(16));
        using var keyed = signed.CopyWithPrivateKey(key);
        // Windows Schannel 不接受此签发路径产生的 ephemeral key。仅在内存中重载 PFX，
        // 使用由证书 Dispose 回收的临时密钥容器；不使用 PersistKeySet、不安装系统信任。
        _certificate = X509CertificateLoader.LoadPkcs12(keyed.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.DefaultKeySet);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            // 受控大上传超过 Kestrel 默认 30MB 限制；测试自身仍由调用方截止和固定大小约束。
            options.Limits.MaxRequestBodySize = null;
            options.Listen(IPAddress.Loopback, 0, listen =>
            {
            listen.Protocols = https ? HttpProtocols.Http1AndHttp2 : HttpProtocols.Http1;
            if (https) listen.UseHttps(tls => tls.ServerCertificateSelector = (connection, name) =>
            {
                if (connection is not null) _sni[connection.ConnectionId] = name ?? "";
                return _certificate;
            });
            });
        });
        _app = builder.Build();
        _app.Run(async context =>
        {
            Interlocked.Increment(ref _requestHeadersReceived);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(context.Request.Body, context.RequestAborted));
            var receipt = new OriginReceipt(target, context.Request.Method,
                context.Features.Get<IHttpRequestFeature>()!.RawTarget!, context.Request.Host.Value ?? "", context.Request.Protocol,
                _sni.GetValueOrDefault(context.Connection.Id), hash,
                context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.Select(v => v ?? "").ToArray(), StringComparer.OrdinalIgnoreCase));
            Receipts.Enqueue(receipt);
            var path = context.Request.Path.Value;
            if (path == "/sse")
            {
                context.Response.ContentType = "text/event-stream";
                var bytes = System.Text.Encoding.UTF8.GetBytes("data: 中文😀\r\n\r\n");
                foreach (var b in bytes) { await context.Response.Body.WriteAsync(new[] { b }, context.RequestAborted); await context.Response.Body.FlushAsync(context.RequestAborted); }
                await ContinueStream.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
                await context.Response.WriteAsync("data: second\n\n", context.RequestAborted);
            }
            else if (path == "/stream")
            {
                context.Response.ContentType = "text/event-stream";
                try
                {
                    while (!context.RequestAborted.IsCancellationRequested)
                    {
                        await context.Response.WriteAsync("data: 心跳\n\n", context.RequestAborted);
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        await Task.Delay(100, context.RequestAborted);
                    }
                }
                finally { StreamExited.TrySetResult(); }
            }
            else if (path == "/binary" || path == "/short")
            {
                var size = long.Parse(context.Request.Query["size"].ToString());
                if (context.Request.Query["unknown"] != "1") context.Response.ContentLength = size;
                context.Response.ContentType = "application/octet-stream";
                var send = path == "/short" ? size / 2 : size;
                for (long offset = 0; offset < send; offset += Block.Length)
                {
                    await context.Response.Body.WriteAsync(Block.AsMemory(0, (int)Math.Min(Block.Length, send - offset)), context.RequestAborted);
                    if (offset == 0 && context.Request.Query["hold"] == "1")
                    {
                        await context.Response.Body.FlushAsync(context.RequestAborted);
                        await ContinueStream.Task.WaitAsync(TimeSpan.FromSeconds(10), context.RequestAborted);
                    }
                }
                if (path == "/short") { await context.Response.Body.FlushAsync(); context.Abort(); }
            }
            else if (path == "/delay") await Task.Delay(TimeSpan.FromSeconds(60), context.RequestAborted);
            else if (path == "/silent")
            {
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: first\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                try { await Task.Delay(TimeSpan.FromSeconds(60), context.RequestAborted); }
                finally { StreamExited.TrySetResult(); }
            }
            else if (path == "/redirect") { context.Response.StatusCode = 302; context.Response.Headers.Location = "/echo"; }
            else if (path == "/head") context.Response.ContentLength = 123;
            else if (path is "/gzip" or "/br")
            {
                using var compressed = new MemoryStream();
                using (Stream encoder = path == "/br" ? new System.IO.Compression.BrotliStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true) : new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, true))
                    encoder.Write(System.Text.Encoding.UTF8.GetBytes("压缩响应正文"));
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.ContentEncoding = path == "/br" ? "br" : "gzip";
                context.Response.ContentLength = compressed.Length;
                await context.Response.Body.WriteAsync(compressed.ToArray(), context.RequestAborted);
            }
            else if (path == "/empty") context.Response.StatusCode = 204;
            else if (path == "/not-modified") context.Response.StatusCode = 304;
            else if (path == "/range")
            {
                context.Response.StatusCode = 206; context.Response.ContentType = "video/mp4";
                context.Response.Headers.ContentRange = "bytes 0-3/1000";
                context.Response.Headers.Append("Set-Cookie", new Microsoft.Extensions.Primitives.StringValues(["a=1; Expires=Wed, 21 Oct 2030 07:28:00 GMT", "b=2"]));
                await context.Response.Body.WriteAsync("part"u8.ToArray(), context.RequestAborted);
            }
            else await context.Response.WriteAsJsonAsync(receipt, context.RequestAborted);
        });
    }

    public static async Task<ControlledOrigin> StartAsync(string target, X509Certificate2 root, string certificateName = "localhost", bool https = true, bool expired = false)
    {
        var origin = new ControlledOrigin(target, root, certificateName, https, expired);
        try
        {
            await origin._app.StartAsync();
            origin.Address = new(origin._app.Urls.Single());
            return origin;
        }
        catch { await origin.DisposeAsync(); throw; }
    }

    public async ValueTask DisposeAsync()
    {
        ContinueStream.TrySetResult();
        using var closing = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(closing.Token);
        await _app.DisposeAsync();
        _certificate.Dispose();
    }
}
