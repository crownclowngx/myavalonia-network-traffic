using System.Net;
using System.Net.Http.Json;
using System.Text;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

[Collection("ProxyProtocol")]
public sealed class ProxyProtocolTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 普通请求真实经过代理且内容不变(bool https)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, "localhost", https);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var client = proxy.Client();
        var uri = new UriBuilder(origin.Address) { Host = "localhost", Path = "/a%2Fb", Query = "q=1&q=%E4%B8%AD" }.Uri;
        using var response = await client.PostAsync(uri, new StringContent("中文正文", Encoding.UTF8));
        response.EnsureSuccessStatusCode();
        var received = (await response.Content.ReadFromJsonAsync<OriginReceipt>())!;
        Assert.Equal("A", received.Target);
        Assert.Equal("/a%2Fb?q=1&q=%E4%B8%AD", received.Path);
        Assert.Equal("POST", received.Method);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("中文正文"))), received.BodyHash);
        await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().Recent.Count == 1);
        Assert.Single(proxy.Runtime.GetMetrics().Recent);
        using var get = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/echo?empty=&q=2&q=3"));
        get.Headers.Add("X-M0-Multi", new[] { "alpha", "beta" });
        using var getResponse = await client.SendAsync(get);
        var getReceipt = (await getResponse.Content.ReadFromJsonAsync<OriginReceipt>())!;
        Assert.Equal("GET", getReceipt.Method);
        Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([])), getReceipt.BodyHash);
        Assert.Contains("alpha", string.Join(',', getReceipt.Headers["X-M0-Multi"]));
        Assert.Contains("beta", string.Join(',', getReceipt.Headers["X-M0-Multi"]));
        ProbeEvidence.Write("protocol-probes", https ? "P02" : "P01", new { https, requested = "1.1", clientProtocol = response.Version.ToString(), receipt = received, getReceipt });
    }

    [Fact]
    public async Task 双端严格证书验证拒绝错误名称和不信任根()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var untrusted = proxy.Client(false);
        await Assert.ThrowsAsync<HttpRequestException>(() => untrusted.GetAsync(new UriBuilder(origin.Address) { Host = "localhost" }.Uri));
        using var client = proxy.Client();
        // 端点证书只有 localhost，访问 IP 必须失败，不能用自定义根连名称错误也一并放行。
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(origin.Address));
        Assert.Empty(origin.Receipts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HTTPS映射实际到B并保留SNI与未命中请求(bool h2)
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var a = await ControlledOrigin.StartAsync("A", root, "127.0.0.1");
        await using var b = await ControlledOrigin.StartAsync("B", root);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(Mapping: new("127.0.0.1", $"https://localhost:{b.Address.Port}", "B"), UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var client = proxy.Client();
        var requests = Enumerable.Range(0, 12).Select(async i =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(a.Address, $"/echo?q={i}&q=%2F"));
            if (h2) { request.Version = HttpVersion.Version20; request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower; }
            request.Content = new StringContent($"body-{i}");
            if (i % 2 == 0) request.Headers.Add("X-M0-Route", "B");
            using var response = await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            Assert.Equal(HttpVersion.Version11, response.Version);
            var receipt = (await response.Content.ReadFromJsonAsync<OriginReceipt>())!;
            Assert.Equal(i % 2 == 0 ? "B" : "A", receipt.Target);
            Assert.Equal($"/echo?q={i}&q=%2F", receipt.Path);
            Assert.Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"body-{i}"))), receipt.BodyHash);
            if (i % 2 == 0) { Assert.Equal("localhost", receipt.Sni); Assert.Equal($"localhost:{b.Address.Port}", receipt.Host); }
        });
        await Task.WhenAll(requests);
        Assert.Equal(6, a.Receipts.Count);
        ProbeEvidence.Write("protocol-probes", h2 ? "R01-R04-P08-fallback" : "R01-R04-h1", new
        {
            assessmentPassed = true, supported = !h2, requested = h2 ? "2.0-or-lower" : "1.1", observed = "1.1",
            exactHttp2Rejected = true, mappingHttp2Supported = false, aReceipts = a.Receipts.ToArray(), bReceipts = b.Receipts.ToArray(), proxy.Info.ProtocolPolicy
        });
        Assert.Equal(6, b.Receipts.Count);
        Assert.False(proxy.Info.Http2Enabled);
        using var exact = new HttpRequestMessage(HttpMethod.Get, new Uri(a.Address, "/echo")) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        await Assert.ThrowsAsync<HttpRequestException>(() => client.SendAsync(exact));
        Assert.Equal(6, a.Receipts.Count);
    }

    [Fact]
    public async Task SSE在端点结束之前收到首事件且跨块字符完整()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root, https: false);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new());
        using var client = proxy.Client();
        using var response = await client.GetAsync(new Uri(origin.Address, "/sse"), HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var first = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var clientFirstUtc = DateTimeOffset.UtcNow;
        Assert.Equal("data: 中文😀", first);
        Assert.False(origin.ContinueStream.Task.IsCompleted);
        Assert.True(proxy.Runtime.GetMetrics().ReceivedBytes > 0);
        var recorderObservedUtc = DateTimeOffset.UtcNow;
        origin.ContinueStream.SetResult();
        Assert.Contains("second", await reader.ReadToEndAsync());
        ProbeEvidence.Write("stream-probes", "S01-S02", new { clientFirstUtc, recorderObservedUtc, completedUtc = DateTimeOffset.UtcNow,
            first, incrementalBeforeCompletion = true, observedBytes = proxy.Runtime.GetMetrics().ReceivedBytes });
    }

    [Fact]
    public async Task HTTP2记录两端实际协议并独立取消一个流()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var origin = await ControlledOrigin.StartAsync("A", root);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var client = proxy.Client();
        var uri = new UriBuilder(origin.Address) { Host = "localhost", Path = "/echo" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Equal(HttpVersion.Version20, response.Version);
        Assert.Equal("HTTP/2", (await response.Content.ReadFromJsonAsync<OriginReceipt>())!.Protocol);
        await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().Recent.Any(o => o.CompletedUtc is not null));
        var observed = proxy.Runtime.GetMetrics().Recent.Single();
        Assert.Equal("2.0", observed.ClientProtocol);
        Assert.Equal("2.0", observed.OriginProtocol);
        using var cancel = new CancellationTokenSource();
        using var slow = new HttpRequestMessage(HttpMethod.Get, new Uri(uri, "/delay")) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        var waiting = client.SendAsync(slow, cancel.Token);
        await ProxyFixture.WaitUntilAsync(() => origin.Receipts.Any(r => r.Path == "/delay"));
        using var parallel = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        using var completed = await client.SendAsync(parallel);
        completed.EnsureSuccessStatusCode();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Equal(HttpVersion.Version20, completed.Version);
        await proxy.Runtime.StopAsync();
        Assert.Single(proxy.Runtime.GetMetrics().Recent.Select(o => o.ClientConnectionId).Distinct());
        ProbeEvidence.Write("protocol-probes", "P07-P09", new { assessmentPassed = true, supported = true, requested = "2.0-exact",
            clientProtocol = response.Version.ToString(), originProtocol = observed.OriginProtocol, isolatedCancellation = true,
            receipts = origin.Receipts.ToArray() });
    }

    [Fact]
    public async Task 映射后的SSE与大文件保持增量和完整哈希()
    {
        using var root = ProxyCertificates.CreateRoot();
        await using var a = await ControlledOrigin.StartAsync("A", root, "127.0.0.1");
        await using var b = await ControlledOrigin.StartAsync("B", root);
        await using var proxy = new ProxyFixture();
        await proxy.StartAsync(new(Mapping: new("127.0.0.1", $"https://localhost:{b.Address.Port}"), UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
        using var client = proxy.Client();
        using var sse = await client.GetAsync(new Uri(a.Address, "/sse"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await sse.Content.ReadAsStreamAsync());
        Assert.Equal("data: 中文😀", await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(b.ContinueStream.Task.IsCompleted); b.ContinueStream.SetResult(); await reader.ReadToEndAsync();
        const long size = 128L << 20;
        using var binary = await client.GetAsync(new Uri(a.Address, $"/binary?size={size}"), HttpCompletionOption.ResponseHeadersRead);
        var digest = await TransferDigest.ReadAsync(await binary.Content.ReadAsStreamAsync());
        Assert.Equal(size, digest.Bytes); Assert.Equal(TransferDigest.Expected(size), digest.Hash);
        Assert.Empty(a.Receipts); Assert.Equal(2, b.Receipts.Count);
        ProbeEvidence.Write("protocol-probes", "R07", new { digest, incremental = true, aReceipts = a.Receipts.ToArray(), bReceipts = b.Receipts.ToArray(), observed = "1.1" });
    }
}
