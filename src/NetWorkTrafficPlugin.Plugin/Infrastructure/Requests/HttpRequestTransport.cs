using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Proxy;

namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>
/// 每次执行只建立一条新连接，不共享可能被服务器关闭的旧池连接。ConnectCallback 同时承担最终地址校验和连接，
/// 避免“预检一个 DNS 结果、客户端再次解析另一个结果”。即便底层尝试重连，也拒绝第二次建立连接，不能透明重发。
/// 这里不解析环境或改写规则，不负责 UI、结果保存及重试；TLS 名称始终来自请求最终主机。
/// </summary>
public sealed class HttpRequestTransport(Func<int?>? proxyPort = null, X509Certificate2? testRoot = null,
    Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) : IRequestTransport
{
    public async Task<RequestTransportResponse> SendAsync(RequestWireInput request, CancellationToken token, Action<ReadOnlyMemory<byte>>? requestBytes = null)
    {
        token.ThrowIfCancellationRequested();
        RequestValidation.Method(request.Method, request.Body is not null);
        RequestValidation.Address(request.Address.AbsoluteUri);
        foreach (var header in request.Headers) RequestValidation.Header(header.Name, header.Value);
        var connectionAttempts = 0;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false, Credentials = null,
            PreAuthenticate = false, AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(RequestLimits.HeaderSeconds), MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.Zero, MaxConnectionsPerServer = 1,
            ConnectCallback = async (context, cancellation) =>
            {
                if (Interlocked.Increment(ref connectionAttempts) != 1)
                    throw new IOException("同一执行不允许建立第二条连接或自动重发。");
                var addresses = resolve is null
                    ? await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellation).ConfigureAwait(false)
                    : await resolve(context.DnsEndPoint.Host, cancellation).ConfigureAwait(false);
                if (addresses.Length is 0 or > 16) throw new RequestFailure("RQ010", "目标地址为空或超过解析上限。");
                // 代理当前只监听回环。多地址结果中有自代理候选就整次拒绝，不偷偷选择另一个地址。
                if (proxyPort?.Invoke() == context.DnsEndPoint.Port && addresses.Any(IsLoopback))
                    throw new RequestFailure("RQ010", "目标指向本插件代理监听端点，已阻止回流。");
                var endpoint = new IPEndPoint(addresses[0], context.DnsEndPoint.Port);
                var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(endpoint, cancellation).ConfigureAwait(false);
                    // 用户可能在 DNS 期间启动代理。连接完成但尚未交给 HTTP 层时再次检查实际远端。
                    if (proxyPort?.Invoke() == endpoint.Port && IsLoopback(endpoint.Address))
                        throw new RequestFailure("RQ010", "目标已成为本插件代理端点，尚未发送请求。");
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        };
        if (testRoot is not null)
            handler.SslOptions = new SslClientAuthenticationOptions
            { RemoteCertificateValidationCallback = (_, certificate, _, errors) => ProxyCertificates.ValidateWithRoot(certificate, errors, testRoot) };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Address)
        { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        HttpResponseMessage? response = null;
        try
        {
            if (request.Body is not null)
            {
                message.Content = new ObservedContent(await request.Body.OpenAsync(token).ConfigureAwait(false), request.Body.Length, requestBytes);
                message.Content.Headers.ContentLength = request.Body.Length;
                message.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.MediaType ?? "application/octet-stream");
            }
            foreach (var header in request.Headers)
                if (!message.Headers.TryAddWithoutValidation(header.Name, header.Value) &&
                    !(message.Content?.Headers.TryAddWithoutValidation(header.Name, header.Value) ?? false))
                    throw new RequestFailure("RQ003", "该头字段不适用于当前正文模式。");
            message.Headers.ConnectionClose = true;
            response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            var headers = response.Headers.Concat(response.Content.Headers)
                .SelectMany(h => h.Value.Select(value => new HeaderField(h.Key, value))).ToArray();
            var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            return new((int)response.StatusCode, response.Version.ToString(), headers, stream, new ResponseOwner(response, message, client));
        }
        catch { response?.Dispose(); message.Dispose(); client.Dispose(); throw; }
    }
    private static bool IsLoopback(IPAddress address) => IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
    /// <summary>先写入传输流再通知记录器；记录失败不得触发请求重试。字节回调只证明客户端提交，不证明远端业务完成。</summary>
    private sealed class ObservedContent(Stream input, long length, Action<ReadOnlyMemory<byte>>? observe) : HttpContent
    {
        protected override bool TryComputeLength(out long value) { value = length; return true; }
        protected override Task SerializeToStreamAsync(Stream output, TransportContext? context) => SerializeToStreamAsync(output, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream output, TransportContext? context, CancellationToken token)
        {
            var buffer = new byte[65536]; long sent = 0; int read;
            while ((read = await input.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                sent += read; if (sent > length) throw new IOException("正文比冻结长度更长。");
                await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false); observe?.Invoke(buffer.AsMemory(0, read));
            }
            if (sent != length) throw new IOException("正文比冻结长度更短。");
        }
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ResponseOwner(HttpResponseMessage response, HttpRequestMessage request, HttpClient client) : IDisposable
    {
        public void Dispose() { response.Dispose(); request.Dispose(); client.Dispose(); }
    }
}
