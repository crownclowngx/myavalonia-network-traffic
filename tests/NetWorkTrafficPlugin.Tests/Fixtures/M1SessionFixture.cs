using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>使用生产记录器/日志/文件系统，只有网络输入由测试控制；提供确定的写入水位等待。</summary>
public sealed class M1SessionFixture : IAsyncDisposable
{
    public string Root { get; } = TemporaryArtifacts.Create();
    public LiveCaptureSession Session { get; }
    public BoundedBodyRecorder Recorder { get; }
    private bool _stopped;
    public M1SessionFixture(CaptureLimits? limits = null)
    {
        limits ??= CaptureLimits.M1();
        var directory = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var budget = new CaptureDiskBudget(Root, limits.DiskBytes);
        Recorder = new(limits, new FileBodyRecordSink(directory, true), directory, budget);
        Session = new(directory, limits, Recorder, budget);
    }
    public async Task<(Guid Id, BoundedBodyRecorder.Entry Response)> AddAsync(byte[] bytes, bool finish = true, string type = "text/plain", string uri = "https://example.test/api?a=1&a=2", CaptureMode mode = CaptureMode.BoundedBody)
    {
        var id = Guid.NewGuid(); var response = Recorder.Begin(id, finish ? bytes.Length : null, mode);
        Session.Begin(id, "GET", new(uri, uri, false, 0), "1.1", [new("X-Test", "value"), new("Cookie", "synthetic=1")], 0, mode, response);
        Session.Response(id, 200, "1.1", [new("Content-Type", type), new("Set-Cookie", "a=1"), new("Set-Cookie", "b=2")]);
        Recorder.Observe(response, bytes); Session.ResponseBytes(id, bytes);
        if (finish) { Recorder.Complete(response, TransportState.Completed); Session.Complete(id, TransportState.Completed, null); }
        if (mode != CaptureMode.MetadataOnly && bytes.Length > 0)
            await ProxyFixture.WaitUntilAsync(() => Recorder.Snapshot(response).StoredBytes > 0 || Recorder.Snapshot(response).CaptureState == CaptureState.Failed);
        return (id, response);
    }
    public async Task StopAsync()
    {
        if (_stopped) return; _stopped = true;
        await Recorder.DisposeAsync(); await Session.DisposeAsync();
    }
    public async ValueTask DisposeAsync() { await StopAsync(); TemporaryArtifacts.Delete(Root); }
}
