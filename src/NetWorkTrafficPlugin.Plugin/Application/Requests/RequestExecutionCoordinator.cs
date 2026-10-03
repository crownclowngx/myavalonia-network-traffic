using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Requests;

public enum RequestExecutionState { Accepted, Sending, Receiving, Completed, Cancelled, TimedOut, Failed, Interrupted }
public enum RequestDispatchState { NotSent, DispatchStarted, ResponseHeadersReceived }
public sealed record RequestExecutionSnapshot(Guid AttemptId, RequestExecutionState State, RequestDispatchState Dispatch,
    int? StatusCode, long ResponseBytes, string Message);

public interface IDebugRequestRecord
{
    void RequestBytes(ReadOnlyMemory<byte> bytes);
    void Headers(RequestTransportResponse response);
    void ResponseBytes(ReadOnlyMemory<byte> bytes);
    void Complete(TransportState state, string? failure);
}
public interface IDebugRecordWriter
{
    IDebugRequestRecord Begin(Guid attemptId, PreparedRequest request);
}

/// <summary>一次执行身份不与URL或正文哈希混用。重复意图是否允许由预览令牌决定，结果未知绝不获得自动重试资格。</summary>
public sealed class RequestAttempt
{
    private readonly CancellationTokenSource _cancel = new();
    private readonly object _sync = new();
    private bool _finished;
    private RequestExecutionSnapshot _snapshot;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Guid Id { get; } = Guid.NewGuid();
    public Task Completion => _completion.Task;
    public RequestExecutionSnapshot Snapshot => Volatile.Read(ref _snapshot);
    internal CancellationToken Token => _cancel.Token;
    internal RequestAttempt() => _snapshot = new(Id, RequestExecutionState.Accepted, RequestDispatchState.NotSent, null, 0, "已接纳，尚未发送。");
    internal void Update(RequestExecutionSnapshot value) => Volatile.Write(ref _snapshot, value);
    public void Cancel() { lock (_sync) if (!_finished) _cancel.Cancel(); }
    internal void Finish() { lock (_sync) { _finished = true; _cancel.Dispose(); _completion.TrySetResult(); } }
}

/// <summary>
/// 只有接纳点持短锁，网络和文件读取都在锁外。令牌消费、并发占位和在途登记构成一次原子操作；
/// 页面关闭只取消自己的Attempt，容器退出才撤销全部执行。完成任务代表真实清理结束，不是放弃等待。
/// </summary>
public sealed class RequestExecutionCoordinator(IRequestTransport transport, IDebugRecordWriter records, TimeProvider? clock = null, IRequestHistoryStore? history = null) : IAsyncDisposable, IDisposable
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, RequestAttempt> _active = [];
    private bool _stopping;
    public int Active { get { lock (_sync) return _active.Count; } }
    public RequestAttempt Start(PreparedRequest prepared, Guid draftId, long revision, RequestEnvironment? environment)
    {
        RequestAttempt attempt;
        lock (_sync)
        {
            if (_stopping || _active.Count >= RequestLimits.Active) throw new RequestFailure("RQ009", "正在退出或已有四项主动请求，请稍后再试。");
            prepared.Consume(draftId, revision, environment);
            attempt = new(); _active.Add(attempt.Id, attempt);
        }
        _ = ExecuteAsync(attempt, prepared);
        return attempt;
    }
    private async Task ExecuteAsync(RequestAttempt attempt, PreparedRequest prepared)
    {
        IDebugRequestRecord? record = null;
        var state = TransportState.Failed;
        var started = _clock.GetUtcNow();
        Task SaveHistory(CancellationToken token) => history?.WriteAsync(new(attempt.Id, started, prepared.Wire.Method, prepared.SafeAddress,
            prepared.Origin is null ? TrafficSource.Manual : TrafficSource.Replay, prepared.Origin, prepared.EnvironmentId, prepared.EnvironmentRevision, attempt.Snapshot), token) ?? Task.CompletedTask;
        try
        {
            record = records.Begin(attempt.Id, prepared);
            await SaveHistory(attempt.Token).ConfigureAwait(false);
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(RequestLimits.LifetimeSeconds), _clock);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(attempt.Token, lifetime.Token);
            cancel.Token.ThrowIfCancellationRequested();
            attempt.Update(attempt.Snapshot with { State = RequestExecutionState.Sending, Dispatch = RequestDispatchState.DispatchStarted, Message = "正在发送；取消不代表服务器未执行。" });
            await SaveHistory(cancel.Token).ConfigureAwait(false);
            using var headersTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(RequestLimits.HeaderSeconds), _clock);
            using var headersCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, headersTimeout.Token);
            await using var response = await transport.SendAsync(prepared.Wire, headersCancel.Token, record.RequestBytes).ConfigureAwait(false);
            // 头期限只用于收头。后续读取使用独立空闲预算，不把持续流误限制为30秒总时长。
            headersTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
            record.Headers(response);
            attempt.Update(attempt.Snapshot with { State = RequestExecutionState.Receiving, Dispatch = RequestDispatchState.ResponseHeadersReceived,
                StatusCode = response.StatusCode, Message = "接收中" });
            await SaveHistory(cancel.Token).ConfigureAwait(false);
            var buffer = new byte[65536]; long observed = 0;
            while (true)
            {
                using var idle = new CancellationTokenSource(TimeSpan.FromSeconds(RequestLimits.IdleSeconds), _clock);
                using var readCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token, idle.Token);
                var read = await response.Body.ReadAsync(buffer, readCancel.Token).ConfigureAwait(false);
                if (read == 0) break;
                observed += read; record.ResponseBytes(buffer.AsMemory(0, read));
                attempt.Update(attempt.Snapshot with { ResponseBytes = observed });
            }
            state = TransportState.Completed;
            attempt.Update(attempt.Snapshot with { State = RequestExecutionState.Completed, Message = "响应传输完成；HTTP状态与正文保存完整性分别显示。" });
        }
        catch (OperationCanceledException)
        {
            var user = attempt.Token.IsCancellationRequested; state = TransportState.Cancelled;
            attempt.Update(attempt.Snapshot with { State = user ? RequestExecutionState.Cancelled : RequestExecutionState.TimedOut,
                Message = attempt.Snapshot.Dispatch == RequestDispatchState.NotSent ? "尚未发送。" : "请求已取消或超时，远端结果可能未知；不会自动重发。" });
        }
        catch (Exception)
        {
            // 后台任务拥有完整异常边界，任何适配器故障都必须进入可观测终态；异常文本可能含URL或凭据，不能写进历史。
            attempt.Update(attempt.Snapshot with { State = RequestExecutionState.Failed,
                Message = attempt.Snapshot.Dispatch == RequestDispatchState.NotSent ? "接纳后记录初始化失败，尚未发送。" : "传输失败，远端结果可能未知；不会自动重发。" });
        }
        finally
        {
            try
            {
                try { record?.Complete(state, state == TransportState.Completed ? null : attempt.Snapshot.Message); }
                catch (Exception) { attempt.Update(attempt.Snapshot with { Message = attempt.Snapshot.Message + " 记录收尾失败。" }); }
                try { await SaveHistory(CancellationToken.None).ConfigureAwait(false); }
                catch (Exception)
                { attempt.Update(attempt.Snapshot with { Message = attempt.Snapshot.Message + " 历史保存失败，当前网络事实保留。" }); }
            }
            finally
            {
                try { prepared.Complete(); }
                catch (Exception) { attempt.Update(attempt.Snapshot with { Message = attempt.Snapshot.Message + " 正文租约释放失败。" }); }
                finally { lock (_sync) _active.Remove(attempt.Id); attempt.Finish(); }
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        RequestAttempt[] tasks;
        lock (_sync) { _stopping = true; tasks = _active.Values.ToArray(); }
        foreach (var task in tasks) task.Cancel();
        await Task.WhenAll(tasks.Select(t => t.Completion)).ConfigureAwait(false);
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
