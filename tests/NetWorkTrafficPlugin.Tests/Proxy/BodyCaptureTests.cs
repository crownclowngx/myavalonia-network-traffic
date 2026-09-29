using System.Text;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Proxy;

public sealed class BodyCaptureTests
{
    private sealed class Sink : IBodyRecordSink
    {
        public readonly MemoryStream Bytes = new();
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Slow, Fail;
        public int Closed;
        public async ValueTask WriteAsync(Guid id, ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            Entered.TrySetResult();
            if (Slow) await Release.Task.WaitAsync(token);
            if (Fail) throw new IOException("合成磁盘满");
            await Bytes.WriteAsync(bytes, token);
        }
        public ValueTask CompleteAsync(Guid id) { Closed++; return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(0, 0, CaptureState.Complete)]
    [InlineData(4, 4, CaptureState.Complete)]
    [InlineData(5, 4, CaptureState.Partial)]
    public async Task 正文限额精确边界且记录保持连续前缀(int sent, int stored, CaptureState state)
    {
        var sink = new Sink();
        var recorder = new BoundedBodyRecorder(new(BodyBytes: 4), sink);
        var entry = recorder.Begin(Guid.NewGuid(), sent, CaptureMode.BoundedBody);
        var input = Enumerable.Range(0, sent).Select(i => (byte)i).ToArray();
        recorder.Observe(entry, input);
        recorder.Complete(entry, TransportState.Completed);
        await recorder.DisposeAsync();
        var result = recorder.Snapshot(entry);
        Assert.Equal(sent, result.ObservedBytes); Assert.Equal(stored, result.StoredBytes);
        Assert.Equal(state, result.CaptureState); Assert.Equal(input.Take(stored), sink.Bytes.ToArray());
    }

    [Fact]
    public async Task 慢盘队列满后不阻塞且后续块不拼接为完整正文()
    {
        var sink = new Sink { Slow = true };
        await using var recorder = new BoundedBodyRecorder(new(QueueBytes: 4), sink);
        var entry = recorder.Begin(Guid.NewGuid(), null, CaptureMode.BoundedBody);
        recorder.Observe(entry, new byte[] { 1, 2, 3, 4 });
        await sink.Entered.Task;
        recorder.Observe(entry, new byte[] { 5 });
        Assert.Equal(4, recorder.Metrics.Queued);
        sink.Release.SetResult();
        await ProxyFixture.WaitUntilAsync(() => recorder.Metrics.Queued == 0);
        recorder.Observe(entry, new byte[] { 6 });
        recorder.Complete(entry, TransportState.Completed);
        await recorder.DisposeAsync();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, sink.Bytes.ToArray());
        Assert.Equal(6, recorder.Snapshot(entry).ObservedBytes);
        Assert.Equal(CaptureState.Partial, recorder.Snapshot(entry).CaptureState);
    }

    [Theory]
    [InlineData(TransportState.Cancelled)]
    [InlineData(TransportState.Failed)]
    [InlineData(TransportState.Completed)]
    public async Task 部分和未知长度不会伪装完整(TransportState state)
    {
        var recorder = new BoundedBodyRecorder(new(), new Sink());
        var entry = recorder.Begin(Guid.NewGuid(), 5, CaptureMode.BoundedBody);
        recorder.Observe(entry, new byte[] { 1 });
        recorder.Complete(entry, state);
        await recorder.DisposeAsync();
        Assert.Equal(CaptureState.Partial, recorder.Snapshot(entry).CaptureState);
        Assert.NotEqual(TransportState.Completed, recorder.Snapshot(entry).TransportState);
    }

    [Fact]
    public async Task 磁盘失败与全局配额不会丢失观察字节()
    {
        var recorder = new BoundedBodyRecorder(new(DiskBytes: 2), new Sink { Fail = true });
        var entry = recorder.Begin(Guid.NewGuid(), null, CaptureMode.BoundedBody);
        recorder.Observe(entry, new byte[] { 1, 2, 3 });
        recorder.Complete(entry, TransportState.Completed);
        await recorder.DisposeAsync();
        var result = recorder.Snapshot(entry);
        Assert.Equal(3, result.ObservedBytes); Assert.Equal(0, result.StoredBytes);
        Assert.Equal(CaptureState.Failed, result.CaptureState); Assert.Null(result.ExpectedLength);
    }

    [Fact]
    public async Task 高频小块与待关闭记录的数量也受限()
    {
        var sink = new Sink { Slow = true };
        var recorder = new BoundedBodyRecorder(new(MaxActiveRequests: 1), sink);
        var first = recorder.Begin(Guid.NewGuid(), null, CaptureMode.BoundedBody);
        for (var i = 0; i < 2000; i++) recorder.Observe(first, new byte[] { 1 });
        var second = recorder.Begin(Guid.NewGuid(), null, CaptureMode.BoundedBody);
        recorder.Observe(second, new byte[] { 2 });
        Assert.True(recorder.Metrics.Queued <= 257);
        Assert.Equal(CaptureState.Partial, recorder.Snapshot(second).CaptureState);
        recorder.Complete(first, TransportState.Completed); recorder.Complete(second, TransportState.Completed);
        sink.Release.SetResult();
        await recorder.DisposeAsync();
        Assert.True(sink.Bytes.Length <= 257);
    }

    [Fact]
    public void SSE逐字节解析保持Unicode多行空行心跳与尾部状态()
    {
        var parser = new SseEventAssembler();
        var events = new List<string>();
        foreach (var b in Encoding.UTF8.GetBytes(":心跳\r\ndata: 中文😀\r\ndata: second\r\n\r\ndata: tail")) parser.Append(new[] { b }, events.Add);
        Assert.Equal(new[] { "中文😀\nsecond" }, events);
        Assert.True(parser.HasIncompleteEvent);
        parser.Append(Encoding.UTF8.GetBytes("\n\n"), events.Add);
        Assert.Equal("tail", events[1]);
        Assert.False(parser.HasIncompleteEvent);
        var bounded = new SseEventAssembler(8);
        bounded.Append(Encoding.UTF8.GetBytes("data: 超长事件超过上限"), _ => throw new Exception("不应发出"));
        Assert.True(bounded.Truncated);
    }
}
