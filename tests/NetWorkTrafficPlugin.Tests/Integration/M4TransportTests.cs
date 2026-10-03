using System.Net;
using System.Net.Sockets;
using System.Text;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

[Collection("M4Resources")]
public sealed class M4TransportTests
{
    [Theory]
    [InlineData(401)] [InlineData(407)] [InlineData(307)] [InlineData(308)]
    public async Task 认证挑战与保持方法重定向不隐式重发(int status)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var address = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/intent");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = 0;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(timeout.Token); using var reader = new StreamReader(socket.GetStream());
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(timeout.Token))) { }
            Interlocked.Increment(ref received);
            var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Length: 0\r\nConnection: close\r\nLocation: {address}\r\nWWW-Authenticate: Basic realm=synthetic\r\nProxy-Authenticate: Basic realm=synthetic\r\n\r\n");
            await socket.GetStream().WriteAsync(bytes, timeout.Token);
        });
        await using var result = await new HttpRequestTransport().SendAsync(new("POST", address, [], null, null), timeout.Token);
        await server; Assert.Equal(status, result.StatusCode); Assert.Equal(1, received); Assert.False(listener.Pending());
        ProbeEvidence.Write("m4-send", "challenge-" + status, new { status, requests = received, pendingConnection = listener.Pending(), protocol = result.Protocol });
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task 真实目标协议转义和正文与冻结输入一致(bool https)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M4", root, https: https);
        using var body = new MemoryRequestBody(Encoding.UTF8.GetBytes("中文+%"));
        var address = new UriBuilder(origin.Address) { Host = "localhost", Path = "/echo", Query = "a=1&a=%2B&word=%E4%B8%AD" }.Uri;
        var transport = new HttpRequestTransport(testRoot: root, resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        await using var response = await transport.SendAsync(new("POST", address, [new("X-Test", "one")], body, "text/plain; charset=utf-8"), default);
        using var reader = new StreamReader(response.Body); await reader.ReadToEndAsync();
        var receipt = Assert.Single(origin.Receipts);
        Assert.Equal("HTTP/1.1", receipt.Protocol); Assert.Equal("POST", receipt.Method);
        Assert.Equal(address.PathAndQuery, receipt.Path); Assert.Equal(body.Digest, receipt.BodyHash);
        Assert.Equal(address.Authority, receipt.Host); if (https) Assert.Equal("localhost", receipt.Sni);
        Assert.Equal(1, origin.RequestHeadersReceived);
        ProbeEvidence.Write("m4-send", https ? "https" : "http", new { requests = origin.RequestHeadersReceived, receipt, frozenHash = body.Digest, path = address.PathAndQuery });
    }

    [Fact]
    public async Task 重定向和发送后断开都不会产生第二次请求()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M4", root, https: false);
        var transport = new HttpRequestTransport();
        await using (var response = await transport.SendAsync(new("GET", new(origin.Address, "/redirect"), [], null, null), default))
            Assert.Equal(302, response.StatusCode);
        Assert.Equal(1, origin.RequestHeadersReceived);

        // 端点确实收到完整头之后立即断开，客户端不能把结果未知解释为可以再尝试。
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var accepted = 0;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(); Interlocked.Increment(ref accepted);
            using var input = new StreamReader(socket.GetStream());
            while (!string.IsNullOrEmpty(await input.ReadLineAsync())) { }
        });
        var target = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/post");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<HttpRequestException>(() => transport.SendAsync(new("POST", target, [], null, null), timeout.Token));
        await server; Assert.Equal(1, accepted); Assert.False(listener.Pending());
    }

    [Fact]
    public async Task 别名自代理拒绝而普通回环服务可用()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M4", root, https: false);
        var transport = new HttpRequestTransport(() => origin.Address.Port, resolve: (_, _) => Task.FromResult(new[] { IPAddress.Loopback }));
        await Assert.ThrowsAnyAsync<Exception>(() => transport.SendAsync(new("GET", new($"http://alias.example.test:{origin.Address.Port}/"), [], null, null), default));
        Assert.Equal(0, origin.RequestHeadersReceived);
    }

    [Fact]
    public async Task 首事件在结束前可读取消后真正关闭连接()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M4", root, https: false);
        using var cancellation = new CancellationTokenSource();
        await using (var response = await new HttpRequestTransport().SendAsync(new("GET", new(origin.Address, "/silent"), [], null, null), cancellation.Token))
        {
            var buffer = new byte[128]; Assert.True(await response.Body.ReadAsync(buffer) > 0);
            var pending = response.Body.ReadAsync(buffer, cancellation.Token).AsTask(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        await origin.StreamExited.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task 错误证书不能因测试根注入而绕过名称检查()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("M4", root, certificateName: "other.example.test");
        await Assert.ThrowsAsync<HttpRequestException>(() => new HttpRequestTransport(testRoot: root).SendAsync(new("GET", origin.Address, [], null, null), default));
        Assert.Equal(0, origin.RequestHeadersReceived);
    }
}
