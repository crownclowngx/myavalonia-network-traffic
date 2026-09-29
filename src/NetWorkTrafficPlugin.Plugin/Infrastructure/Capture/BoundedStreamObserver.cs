using System.Threading.Channels;
using NetWorkTrafficPlugin.Application.Content;

namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 流式预览的独立后台队列。队列不足时该流标记缺口并停止解析，不能跳过字节后继续拼事件。
/// 解析器和已完成事件的锁只在后台/读取时使用，网络入口只预留和复制有限块。
/// 活动流最多二十条；已完成最多两条，避免历史列表把事件环复制一万份。
/// </summary>
internal sealed class BoundedStreamObserver : IAsyncDisposable
{
    private sealed class State(bool ndjson, IDisposable reservation)
    {
        public readonly StreamingContentParser Parser = new(ndjson);
        public readonly object Sync = new();
        public bool Gap, End, Normal, Closed;
        public int Pending;
        public readonly IDisposable Reservation = reservation;
    }
    private readonly object _sync = new();
    private readonly Dictionary<Guid, State> _states = [];
    private readonly Queue<Guid> _completed = [];
    private readonly Channel<(Guid Id, byte[]? Bytes)> _queue = Channel.CreateBounded<(Guid, byte[]?)>(128);
    private readonly Task _worker;
    private int _bytes;
    private readonly ContentMemoryBudget _budget;
    public BoundedStreamObserver(ContentMemoryBudget budget) { _budget = budget; _worker = Task.Run(RunAsync); }
    public void Register(Guid id, bool ndjson)
    {
        lock (_sync)
        {
            if (_states.ContainsKey(id)) return;
            // 包含字符环、组装器、对象和该流排队份额；不是只计 UTF-8 payload。
            var reservation = _budget.TryReserve(768 << 10);
            if (reservation is not null) _states.Add(id, new(ndjson, reservation));
        }
    }
    public void Observe(Guid id, ReadOnlySpan<byte> bytes)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state) || state.Gap || state.End) return;
            if (bytes.Length > 65536 || _bytes + bytes.Length > 1 << 20) { state.Gap = true; return; }
            var owned = bytes.ToArray();
            if (!_queue.Writer.TryWrite((id, owned))) { state.Gap = true; return; }
            _bytes += owned.Length; state.Pending++;
        }
    }
    public void Complete(Guid id, bool normal)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(id, out var state)) return;
            state.End = true; state.Normal = normal;
            // 满队列时无需再创建异步等待任务：后台每消费一块都会检查所有有限终态。
            _queue.Writer.TryWrite((id, null));
        }
    }
    private async Task RunAsync()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync())
        {
            State? state;
            lock (_sync) _states.TryGetValue(item.Id, out state);
            if (state is not null && item.Bytes is not null)
            {
                lock (state.Sync) state.Parser.Append(item.Bytes);
                lock (_sync) { _bytes -= item.Bytes.Length; state.Pending--; }
            }
            FinishReady();
        }
        FinishReady();
    }
    private void FinishReady()
    {
        lock (_sync)
        {
            foreach (var (id, state) in _states)
            {
                if (state.Gap) lock (state.Sync) state.Parser.MarkGap();
                if (!state.End || state.Pending != 0 || state.Closed) continue;
                lock (state.Sync) state.Parser.Complete(state.Normal);
                state.Closed = true; _completed.Enqueue(id);
            }
            while (_completed.Count > 2)
                if (_states.Remove(_completed.Dequeue(), out var old)) old.Reservation.Dispose();
        }
    }
    public StreamPreview? Snapshot(Guid id)
    {
        State? state;
        lock (_sync) _states.TryGetValue(id, out state);
        if (state is null) return null;
        lock (state.Sync) return state.Parser.Snapshot() with { Gap = state.Gap };
    }
    public async ValueTask DisposeAsync()
    {
        _queue.Writer.TryComplete(); await _worker.ConfigureAwait(false);
        lock (_sync) { foreach (var state in _states.Values) state.Reservation.Dispose(); _states.Clear(); }
    }
}
