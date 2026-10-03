using System.Net;
using System.Net.Http.Json;
using System.Text;
using NetWorkTrafficPlugin.Application.Rewrite;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Titanium.Web.Proxy;
using Titanium.Web.Proxy.Models;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

/// <summary>
/// V6-02 先验证真实库的公开读取接口。这里的固定候选只证明底层能力，后续必须由 DSL 规则链重跑业务场景。
/// 端点计数在读取全部正文之前增加，因此能发现“已经发头、只是尚未发完 Body”的假零发送。
/// </summary>
[Collection("ProxyProtocol")]
public sealed class M3BodyCapabilityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 无Expect的停滞上传也不能在取消期限内释放(bool terminateOnCancellation)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        using var proxy = new ProxyServer(null, null, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificate = root;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeRequest += async (_, args) =>
        {
            using var deadline = new CancellationTokenSource(300); started.TrySetResult();
            try { await new TitaniumBodyCapabilityReader(args, terminateOnCancellation).ReadAsync(deadline.Token); finished.TrySetResult(false); }
            catch (OperationCanceledException) { finished.TrySetResult(true); args.TerminateSession(); }
        };
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false); proxy.AddEndPoint(endpoint); proxy.Start(false);
        using var client = new System.Net.Sockets.TcpClient(); await client.ConnectAsync(IPAddress.Loopback, endpoint.Port);
        // 发送合法请求头后保持连接且不发 Body，独立证明问题不限于 Expect 握手。
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"POST {new Uri(origin.Address, "/echo")} HTTP/1.1\r\nHost: {origin.Address.Authority}\r\nContent-Length: 2\r\nContent-Type: application/json\r\n\r\n"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var releaseWindow = Task.Delay(1300);
        Assert.Same(releaseWindow, await Task.WhenAny(finished.Task, releaseWindow)); Assert.False(finished.Task.IsCompleted);
        client.Close(); Assert.True(await finished.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        proxy.Stop(); Assert.Equal(0, origin.RequestHeadersReceived);
        ProbeEvidence.Write("m3-capability", "stalled-body-" + terminateOnCancellation,
            new { cancellationWithinDeadline = false, terminateOnCancellation, originHeaders = origin.RequestHeadersReceived });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 固定版本的严格Continue与取消期限存在能力缺口(bool terminateOnCancellation)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        using var proxy = new ProxyServer(null, null, false, false)
        { EnableHttp2 = false, CompatibilityMode100Continue = true, Enable100ContinueBehaviour = false };
        proxy.CertificateManager.RootCertificate = root;
        var started = new TaskCompletionSource<System.Diagnostics.Stopwatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeRequest += async (_, args) =>
        {
            using var deadline = new CancellationTokenSource(300);
            started.TrySetResult(System.Diagnostics.Stopwatch.StartNew());
            try { await new TitaniumBodyCapabilityReader(args, terminateOnCancellation).ReadAsync(deadline.Token); observed.TrySetResult(false); }
            catch (OperationCanceledException) { observed.TrySetResult(true); args.TerminateSession(); }
        };
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false); proxy.AddEndPoint(endpoint); proxy.Start(false);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{endpoint.Port}") { BypassProxyOnLocal = false },
            // 禁止用客户端短时间后自行发送 Body 来掩盖代理缺少本地 100 Continue 的事实。
            Expect100ContinueTimeout = TimeSpan.FromSeconds(10)
        }) { Timeout = TimeSpan.FromSeconds(3) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/echo"))
        { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        request.Headers.ExpectContinue = true;
        var sending = client.SendAsync(request);
        var elapsed = await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        // 300ms 取消后给足 1 秒释放窗口，任务仍未退出即证明不能满足 V6 取消门禁。
        var releaseWindow = Task.Delay(1300);
        Assert.Same(releaseWindow, await Task.WhenAny(observed.Task, releaseWindow));
        Assert.False(observed.Task.IsCompleted);
        client.CancelPendingRequests();
        var failure = await Record.ExceptionAsync(() => sending);
        Assert.True(failure is HttpRequestException or OperationCanceledException, failure?.GetType().Name);
        Assert.True(await observed.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        var readCancellationMilliseconds = elapsed.ElapsedMilliseconds;
        proxy.Stop(); Assert.Equal(0, origin.RequestHeadersReceived);
        // 这是缺口回归证据，绝不是 B06 已完成。完整 M3 门禁必须拒绝此版本的该能力结论。
        ProbeEvidence.Write("m3-capability", "strict-continue-" + terminateOnCancellation, new { supported = false, cancellationWithinDeadline = false, terminateOnCancellation, readCancellationMilliseconds, originHeaders = origin.RequestHeadersReceived });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task 发送前有界读取后替换正文(bool chunked, bool expectContinue)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("B", root, https: false);
        using var proxy = new ProxyServer(null, null, false, false) { EnableHttp2 = false, NetworkFailureRetryAttempts = 0 };
        proxy.CertificateManager.RootCertificate = root;
        byte[]? original = null;
        proxy.BeforeRequest += async (_, args) =>
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            original = await new TitaniumBodyCapabilityReader(args).ReadAsync(timeout.Token);
            Assert.Equal(0, origin.RequestHeadersReceived);
            args.SetRequestBody(Encoding.UTF8.GetBytes("{\"value\":\"中文\"}"));
        };
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false); proxy.AddEndPoint(endpoint); proxy.Start(false);
        using var client = new HttpClient(new SocketsHttpHandler
        {
            Proxy = new WebProxy($"http://127.0.0.1:{endpoint.Port}") { BypassProxyOnLocal = false },
            Expect100ContinueTimeout = TimeSpan.FromMilliseconds(50)
        }) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/echo"))
        { Content = new StringContent(new string('x', RewriteLimits.BodyBytes), Encoding.UTF8, "application/json") };
        request.Headers.TransferEncodingChunked = chunked; request.Headers.ExpectContinue = expectContinue;
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var receipt = (await response.Content.ReadFromJsonAsync<OriginReceipt>())!;
        Assert.Equal(RewriteLimits.BodyBytes, original!.Length);
        Assert.Equal("B", receipt.Target); Assert.Single(origin.Receipts);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("{\"value\":\"中文\"}"))), receipt.BodyHash);
        Assert.Equal(1, origin.RequestHeadersReceived);
        proxy.Stop();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 已知或未知长度超限均不发送上游(bool chunked)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        using var proxy = new ProxyServer(null, null, false, false) { EnableHttp2 = false };
        proxy.CertificateManager.RootCertificate = root;
        var failed = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        proxy.BeforeRequest += async (_, args) =>
        {
            try { using var timeout = new CancellationTokenSource(2000); await new TitaniumBodyCapabilityReader(args).ReadAsync(timeout.Token); }
            catch (Exception ex) { failed.TrySetResult(ex); args.TerminateSession(); }
        };
        var endpoint = new ExplicitProxyEndPoint(IPAddress.Loopback, 0, false); proxy.AddEndPoint(endpoint); proxy.Start(false);
        using var client = new HttpClient(new SocketsHttpHandler { Proxy = new WebProxy($"http://127.0.0.1:{endpoint.Port}") { BypassProxyOnLocal = false } }) { Timeout = TimeSpan.FromSeconds(8) };
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/echo")) { Content = new ByteArrayContent(new byte[RewriteLimits.BodyBytes + 1]) };
        request.Headers.TransferEncodingChunked = chunked;
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(request));
        var failure = await failed.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(failure is RewriteFailure || failure.GetType().Name == "BodySizeLimitExceededException", failure.GetType().Name);
        proxy.Stop(); Assert.Equal(0, origin.RequestHeadersReceived); Assert.Empty(origin.Receipts);
    }
}
