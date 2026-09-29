using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class ProxyFailureTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 响应空闲超时只结束沉默响应而不截断活跃流(bool silent)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        await using var proxy = new ProxyFixture(); await proxy.StartAsync(new(ResponseIdleSeconds: 1));
        using var client = proxy.Client();
        using var response = await client.GetAsync(new Uri(origin.Address, silent ? "/silent" : "/stream"), HttpCompletionOption.ResponseHeadersRead);
        var received = TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync());
        if (silent)
        {
            await origin.StreamExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<IOException>(() => received);
            await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().ActiveRequests == 0);
            Assert.Equal("响应读取空闲超时。", Assert.Single(proxy.Runtime.GetMetrics().Recent).Failure);
        }
        else
        {
            // 超过三次空闲窗口仍持续有数据；这是时间语义测试，不能用假时钟代替真实库流。
            await Task.Delay(TimeSpan.FromSeconds(3));
            Assert.False(origin.StreamExited.Task.IsCompleted);
            Assert.False(received.IsCompleted);
            await proxy.Runtime.StopAsync();
            await Assert.ThrowsAnyAsync<IOException>(() => received);
        }
    }

    [Fact]
    public async Task 实际落盘失败时仍完整转发并明确记录失败()
    {
        var directory = Path.Combine(Path.GetTempPath(), "network-m0-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var notDirectory = Path.Combine(directory, "occupied"); await File.WriteAllTextAsync(notDirectory, "fixture");
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        await using var proxy = new ProxyFixture();
        try
        {
            await proxy.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: notDirectory));
            using var client = proxy.Client();
            const int size = 2 << 20;
            using var response = await client.GetAsync(new Uri(origin.Address, $"/binary?size={size}"), HttpCompletionOption.ResponseHeadersRead);
            var result = await TransferDigest.ReadAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal(size, result.Bytes); Assert.Equal(TransferDigest.Expected(size), result.Hash);
            await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().ActiveRequests == 0);
            await proxy.Runtime.StopAsync();
            var observation = Assert.Single(proxy.Runtime.GetMetrics().Recent);
            Assert.Equal(CaptureState.Failed, observation.Body.CaptureState);
            Assert.Equal(TransportState.Completed, observation.Body.TransportState);
            Assert.Equal(0, observation.Body.StoredBytes);
        }
        finally { await proxy.DisposeAsync(); TemporaryArtifacts.Delete(directory); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 上游未知根和过期证书都被拒绝(bool expired)
    {
        using var root = ProxyCertificates.CreateRoot();
        using var unrelated = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, expired: expired);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(UpstreamTestRoot: ProxyFixture.PublicRoot(expired ? root : unrelated)));
        using var client = proxy.Client();
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new UriBuilder(origin.Address) { Host = "localhost" }.Uri));
        Assert.Empty(origin.Receipts);
    }

    [Theory]
    [InlineData("refused")]
    [InlineData("certificate")]
    [InlineData("timeout")]
    public async Task B失败不会回退A或自动重发POST(string failure)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var a = await ControlledOrigin.StartAsync("A", root, "127.0.0.1");
        await using var b = await ControlledOrigin.StartAsync("B", root, failure == "certificate" ? "wrong.invalid" : "localhost");
        // 停掉专属监听形成拒绝连接，不复用任何用户进程的端口。
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start(); var closedPort = ((IPEndPoint)reserved.LocalEndpoint).Port; reserved.Stop();
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(Mapping: new("127.0.0.1", $"https://localhost:{(failure == "refused" ? closedPort : b.Address.Port)}"),
            UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var client = proxy.Client();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
        {
            using var response = await client.PostAsync(new Uri(a.Address, failure == "timeout" ? "/delay" : "/echo"), new StringContent("one-side-effect"));
            response.EnsureSuccessStatusCode(); // 库对响应头超时返回网关错误，连接/TLS 失败则由客户端抛错。
        });
        Assert.Empty(a.Receipts);
        Assert.Equal(failure == "timeout" ? 1 : 0, b.Receipts.Count);
    }

    [Fact]
    public async Task KeepAlive压缩无正文与重定向保持传输语义()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        await using var proxy = new ProxyFixture(); await proxy.StartAsync(new());
        using var client = proxy.Client();
        using var head = new HttpRequestMessage(HttpMethod.Head, new Uri(origin.Address, "/head"));
        using var headResponse = await client.SendAsync(head);
        Assert.Empty(await headResponse.Content.ReadAsByteArrayAsync());
        using var empty = await client.GetAsync(new Uri(origin.Address, "/empty"));
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        using var redirect = await client.GetAsync(new Uri(origin.Address, "/redirect"));
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        using var compressed = await client.GetAsync(new Uri(origin.Address, "/gzip"));
        var wire = await compressed.Content.ReadAsByteArrayAsync();
        Assert.Contains("gzip", compressed.Content.Headers.ContentEncoding);
        await using var gzip = new GZipStream(new MemoryStream(wire), CompressionMode.Decompress);
        Assert.Equal("压缩响应正文", await new StreamReader(gzip).ReadToEndAsync());
        await proxy.Runtime.StopAsync();
        var observations = proxy.Runtime.GetMetrics().Recent;
        Assert.Equal(4, observations.Count);
        Assert.All(observations, o => Assert.Equal(TransportState.Completed, o.Body.TransportState));
        Assert.Equal(wire.Length, observations.Single(o => o.Route.OriginalUri.EndsWith("/gzip")).Body.ObservedBytes);
        Assert.Single(observations.Select(o => o.ClientConnectionId).Distinct());
    }

    [Theory]
    [InlineData(CaptureMode.None)]
    [InlineData(CaptureMode.MetadataOnly)]
    [InlineData(CaptureMode.BoundedBody)]
    public async Task 未知长度与短读真实传输不会被有限记录截断(CaptureMode mode)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        var directory = Path.Combine(Path.GetTempPath(), "network-m0-" + Guid.NewGuid().ToString("N"));
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(Capture: mode, RecordDirectory: directory, Limits: new(BodyBytes: 1024)));
        using var client = proxy.Client();
        const int size = 2 * 1024 * 1024;
        using var complete = await client.GetAsync(new Uri(origin.Address, $"/binary?size={size}&unknown=1"), HttpCompletionOption.ResponseHeadersRead);
        Assert.Null(complete.Content.Headers.ContentLength);
        var observed = await TransferDigest.ReadAsync(await complete.Content.ReadAsStreamAsync());
        Assert.Equal(size, observed.Bytes); Assert.Equal(TransferDigest.Expected(size), observed.Hash);
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(new Uri(origin.Address, $"/short?size={size}")));
        await proxy.Runtime.StopAsync();
        var metrics = proxy.Runtime.GetMetrics();
        Assert.Equal(0, metrics.ActiveRequests); Assert.Equal(0, metrics.QueueBytes);
        if (mode == CaptureMode.None) Assert.Empty(metrics.Recent);
        else
        {
            var normal = metrics.Recent.Single(o => o.Route.OriginalUri.Contains("/binary"));
            Assert.Null(normal.Body.ExpectedLength); Assert.Equal(TransportState.Completed, normal.Body.TransportState);
            var broken = metrics.Recent.Single(o => o.Route.OriginalUri.Contains("/short"));
            Assert.NotEqual(TransportState.Completed, broken.Body.TransportState);
            Assert.NotEqual(CaptureState.Complete, broken.Body.CaptureState);
            if (mode == CaptureMode.BoundedBody)
            {
                Assert.Equal(CaptureState.Partial, normal.Body.CaptureState);
                Assert.Equal(1024, normal.Body.StoredBytes);
                using var file = File.Open(normal.Body.FilePath!, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                Assert.Equal(TransferDigest.Expected(1024), (await TransferDigest.ReadAsync(file)).Hash);
            }
        }
        TemporaryArtifacts.Delete(directory);
    }

    [Fact]
    public async Task 活动请求达到上限时拒绝新增且停止取消持续流()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        await using var proxy = new ProxyFixture(); await proxy.StartAsync(new(Limits: new(MaxActiveRequests: 1)));
        using var client = proxy.Client();
        using var stream = await client.GetAsync(new Uri(origin.Address, "/stream"), HttpCompletionOption.ResponseHeadersRead);
        using var extra = await client.GetAsync(new Uri(origin.Address, "/echo"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, extra.StatusCode);
        await proxy.Runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await origin.StreamExited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var metrics = proxy.Runtime.GetMetrics();
        Assert.Equal(0, metrics.ActiveRequests); Assert.Equal(1, metrics.RejectedRequests);
        Assert.Equal(TransportState.Cancelled, Assert.Single(metrics.Recent).Body.TransportState);
        using var listener = new TcpListener(IPAddress.Loopback, proxy.Info.Port); listener.Start();
    }
}
