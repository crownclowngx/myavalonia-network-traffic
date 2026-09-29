using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Proxy;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

public sealed class M1CaptureProtocolTests
{
    [Fact]
    public async Task HTTP2空闲能力缺口明确披露且客户端单流取消不影响持续捕获()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            using var root = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("h2-stream", root, "127.0.0.1");
            await using var proxy = new ProxyFixture(); await proxy.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: directory,
                Limits: CaptureLimits.M1(), ResponseIdleSeconds: 1, UpstreamTestRoot: ProxyFixture.PublicRoot(root)));
            using var client = proxy.Client();
            HttpRequestMessage Request(string path) => new(HttpMethod.Get, new Uri(origin.Address, path)) { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            using var silentRequest = Request("/silent"); using var activeRequest = Request("/stream");
            using var silent = await client.SendAsync(silentRequest, HttpCompletionOption.ResponseHeadersRead);
            using var active = await client.SendAsync(activeRequest, HttpCompletionOption.ResponseHeadersRead);
            var session = proxy.Runtime.Capture!;
            await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().Recent.Any(r => r.Route.OriginalUri.EndsWith("/silent") && r.Failure is not null));
            var running = Assert.Single(session.Snapshot().Records, r => r.Path == "/stream");
            var ended = Assert.Single(session.Snapshot().Records, r => r.Path == "/silent");
            Assert.Equal(TransportState.Receiving, running.Transport); Assert.Equal(TransportState.Receiving, ended.Transport);
            var idleRecord = (await session.ReadAsync(ended.RecordId, default))!;
            Assert.Equal("UnsupportedH2Cancellation", idleRecord.Failure!.Kind);
            Assert.Equal(ProxyProtocolCapabilities.Http2IdleNotice, idleRecord.Failure.Message);
            Assert.Null(idleRecord.CompletedUtc);
            Assert.NotEqual(CaptureState.Complete, idleRecord.ResponseBody.Capture);
            var observations = proxy.Runtime.GetMetrics().Recent;
            var silentConnection = observations.Single(r => r.RequestId == ended.RecordId).ClientConnectionId;
            Assert.Equal(silentConnection, observations.Single(r => r.RequestId == running.RecordId).ClientConnectionId);
            var record = (await session.ReadAsync(running.RecordId, default))!;
            Assert.Equal("2.0", record.ClientProtocol); Assert.Equal("2.0", record.OriginProtocol);
            await ProxyFixture.WaitUntilAsync(() => session.Stream(running.RecordId)?.Total >= 15);
            var beforeCancel = session.Stream(running.RecordId)!.Total;
            silent.Dispose();
            await ProxyFixture.WaitUntilAsync(() => proxy.Runtime.GetMetrics().ActiveRequests == 1);
            await ProxyFixture.WaitUntilAsync(() => session.Stream(running.RecordId)?.Total > beforeCancel);
            Assert.Equal(TransportState.Receiving, (await session.ReadAsync(running.RecordId, default))!.ResponseBody.Transport);
            // 产品降级只取消“自动空闲取消”的承诺。仍必须证明同连接隔离、诚实状态及全局停止。
            var continued = session.Stream(running.RecordId)!.Total;
            var activeCount = proxy.Runtime.GetMetrics().ActiveRequests;
            await proxy.Runtime.StopAsync();
            Assert.Equal(0, proxy.Runtime.GetMetrics().ActiveRequests);
            ProbeEvidence.Write("m1-stream", "h2-isolation", new { activeRecordId = running.RecordId, endedRecordId = ended.RecordId, record.ClientProtocol, record.OriginProtocol,
                events = continued, activeCount, idleCancellationSupported = false, idlePolicy = "notify-only", diagnostic = idleRecord.Failure.Kind,
                transportAtIdle = idleRecord.ResponseBody.Transport.ToString(), captureAtIdle = idleRecord.ResponseBody.Capture.ToString(),
                sameClientConnection = true, continuedAfterCancel = continued > beforeCancel, stoppedActiveCount = proxy.Runtime.GetMetrics().ActiveRequests });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Fact]
    public async Task 二十轮启停更换公开CA并释放目录和端口()
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            var fingerprints = new HashSet<string>();
            await using var owner = new ProxySessionCoordinator(() => new TitaniumProxyRuntime());
            for (var i = 0; i < 20; i++)
            {
                var info = await owner.StartAsync(new(Capture: CaptureMode.MetadataOnly, RecordDirectory: directory, Limits: CaptureLimits.M1()));
                using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(Convert.FromBase64String(info.PublicRootCertificate));
                Assert.False(cert.HasPrivateKey); Assert.True(fingerprints.Add(cert.GetCertHashString(HashAlgorithmName.SHA256)));
                await owner.StopAsync(); Assert.Equal(ProxyState.Stopped, owner.Status.State);
                using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, info.Port); listener.Start();
                using var file = File.Open(Path.Combine(directory, ".capture.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            ProbeEvidence.Write("m1-lifetime", "restart", new { rounds = fingerprints.Count, state = owner.Status.State.ToString(), generation = owner.Status.Generation });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task 上传首块在结束前捕获且取消不会伪造完整(bool knownLength, bool cancel)
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            using var root = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("upload", root, https: false);
            await using var proxy = new ProxyFixture(); await proxy.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: directory, Limits: CaptureLimits.M1()));
            using var client = proxy.Client(); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/echo?a=1&a=2")) { Content = new GeneratedContent(2 << 20, knownLength, first, release.Task) };
            request.Headers.ExpectContinue = true;
            var sending = client.SendAsync(request, cancellation.Token); await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var session = proxy.Runtime.Capture!;
            await ProxyFixture.WaitUntilAsync(() => session.Snapshot().Records.FirstOrDefault()?.ObservedBytes >= 65536);
            var id = Assert.Single(session.Snapshot().Records).RecordId; var receiving = (await session.ReadAsync(id, default))!;
            Assert.Equal(TransportState.Receiving, receiving.RequestBody.Transport); Assert.False(sending.IsCompleted);
            var observedBeforeEnd = receiving.RequestBody.ObservedBytes;
            if (cancel) { cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var ignored = await sending; }); }
            else { release.SetResult(); using var response = await sending; response.EnsureSuccessStatusCode(); Assert.Equal(TransferDigest.Expected(2 << 20), Assert.Single(origin.Receipts).BodyHash); }
            await proxy.Runtime.StopAsync(); var ended = (await session.ReadAsync(id, default))!;
            Assert.NotEqual(CaptureState.Complete, ended.RequestBody.Capture);
            Assert.Equal(knownLength ? 2L << 20 : null, ended.RequestBody.ExpectedBytes);
            ProbeEvidence.Write("m1-capture", $"upload-{knownLength}-{cancel}", new { knownLength, cancel, observedBeforeEnd, observed = ended.RequestBody.ObservedBytes, state = ended.RequestBody.Capture.ToString(), transport = ended.RequestBody.Transport.ToString(), receipts = origin.Receipts.Count });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }

    [Theory]
    [InlineData("/head", "HEAD", 200, 0)]
    [InlineData("/empty", "GET", 204, 0)]
    [InlineData("/not-modified", "GET", 304, 0)]
    [InlineData("/redirect", "GET", 302, 0)]
    [InlineData("/range", "GET", 206, 4)]
    public async Task 无正文重定向与分段响应保持原始传输语义(string path, string method, int status, int bytes)
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            using var root = ProxyCertificates.CreateRoot(); await using var origin = await ControlledOrigin.StartAsync("response", root, https: false);
            await using var proxy = new ProxyFixture(); await proxy.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: directory, Limits: CaptureLimits.M1()));
            using var client = proxy.Client(); using var request = new HttpRequestMessage(new(method), new Uri(origin.Address, path)); using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode); Assert.Equal(bytes, (await response.Content.ReadAsByteArrayAsync()).Length);
            await proxy.Runtime.StopAsync(); var session = proxy.Runtime.Capture!;
            var record = (await session.ReadAsync(Assert.Single(session.Snapshot().Records).RecordId, default))!;
            Assert.Equal(bytes, record.ResponseBody.ObservedBytes); Assert.Equal(CaptureState.Complete, record.ResponseBody.Capture); Assert.Single(origin.Receipts);
            if (status == 206) { Assert.Equal(2, record.ResponseHeaders.Count(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))); Assert.Contains(record.ResponseHeaders, h => h.Value == "bytes 0-3/1000"); }
            ProbeEvidence.Write("m1-capture", $"response-{status}-{method}", new { status, method, bytes, observed = record.ResponseBody.ObservedBytes, expected = record.ResponseBody.ExpectedBytes, receipts = origin.Receipts.Count });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
    [Theory]
    [InlineData(false, "gzip")]
    [InlineData(false, "br")]
    [InlineData(true, "gzip")]
    [InlineData(true, "br")]
    public async Task 双向增量捕获保存编码实体字节且解码不重复(bool http2, string encoding)
    {
        var directory = TemporaryArtifacts.Create();
        try
        {
            using var root = ProxyCertificates.CreateRoot();
            await using var origin = await ControlledOrigin.StartAsync("M1", root, "127.0.0.1");
            await using var proxy = new ProxyFixture();
            await proxy.StartAsync(new(Capture: CaptureMode.BoundedBody, RecordDirectory: directory,
                UpstreamTestRoot: ProxyFixture.PublicRoot(root), Limits: CaptureLimits.M1()));
            using var client = proxy.Client();
            using var compressed = new MemoryStream();
            using (Stream encoder = encoding == "gzip" ? new GZipStream(compressed, CompressionLevel.Fastest, true) : new BrotliStream(compressed, CompressionLevel.Fastest, true))
                encoder.Write(Encoding.UTF8.GetBytes("请求中文😀"));
            var sent = compressed.ToArray();
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin.Address, "/" + encoding + "?a=1&a=2&plus=+"))
            { Content = new ByteArrayContent(sent), Version = http2 ? HttpVersion.Version20 : HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
            request.Content.Headers.ContentEncoding.Add(encoding);
            request.Content.Headers.ContentType = new("text/plain");
            request.Headers.TryAddWithoutValidation("X-Repeated", new[] { "first", "second" });
            using var response = await client.SendAsync(request);
            var received = await response.Content.ReadAsByteArrayAsync();
            await proxy.Runtime.StopAsync();
            var session = Assert.IsType<NetWorkTrafficPlugin.Infrastructure.Capture.LiveCaptureSession>(proxy.Runtime.Capture);
            var row = Assert.Single(session.Snapshot().Records);
            var record = Assert.IsType<TrafficRecord>(await session.ReadAsync(row.RecordId, default));
            var requestBody = await session.ReadBodyAsync(record.RequestBody, 65536, default);
            var responseBody = await session.ReadBodyAsync(record.ResponseBody, 65536, default);
            Assert.Equal(sent, requestBody); Assert.Equal(received, responseBody);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(sent)), Assert.Single(origin.Receipts).BodyHash);
            Assert.Equal(CaptureState.Complete, record.RequestBody.Capture);
            Assert.Equal(CaptureState.Complete, record.ResponseBody.Capture);
            Assert.Equal(BodyRepresentation.EncodedEntity, record.RequestBody.Representation);
            Assert.Equal(http2 ? "2.0" : "1.1", record.ClientProtocol);
            Assert.Equal("压缩响应正文", ContentPreviewReader.Parse(record.ResponseBody, responseBody).Text);
            Assert.Equal("请求中文😀", ContentPreviewReader.Parse(record.RequestBody, requestBody).Text);
            ProbeEvidence.Write("m1-capture", $"encoded-{http2}-{encoding}", new
            {
                record.RecordId, record.ClientProtocol, record.OriginProtocol, representation = record.ResponseBody.Representation.ToString(),
                requestObserved = record.RequestBody.ObservedBytes, requestStored = requestBody.Length,
                responseObserved = record.ResponseBody.ObservedBytes, responseStored = responseBody.Length,
                requestHash = Convert.ToHexString(SHA256.HashData(requestBody)), responseHash = Convert.ToHexString(SHA256.HashData(responseBody)),
                receipts = origin.Receipts.Count, decoded = ContentPreviewReader.Parse(record.ResponseBody, responseBody).Text
            });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
}
