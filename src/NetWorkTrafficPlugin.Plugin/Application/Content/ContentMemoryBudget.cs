namespace NetWorkTrafficPlugin.Application.Content;

/// <summary>所有内容消费者共用一个额度，原子预留失败就降级；不能每个缓存各自宣称有 32 MiB。</summary>
public sealed class ContentMemoryBudget(long limit = 32L << 20)
{
    private long _reserved, _peak;
    public long Reserved => Interlocked.Read(ref _reserved);
    public long Peak => Interlocked.Read(ref _peak);
    public IDisposable? TryReserve(long bytes)
    {
        if (bytes < 0 || bytes > limit) return null;
        while (true)
        {
            var current = Reserved;
            if (bytes > limit - current) return null;
            if (Interlocked.CompareExchange(ref _reserved, current + bytes, current) != current) continue;
            long peak;
            do { peak = Peak; if (peak >= current + bytes) break; } while (Interlocked.CompareExchange(ref _peak, current + bytes, peak) != peak);
            return new Reservation(this, bytes);
        }
    }
    private sealed class Reservation(ContentMemoryBudget owner, long bytes) : IDisposable
    {
        private ContentMemoryBudget? _owner = owner;
        public void Dispose() { var value = Interlocked.Exchange(ref _owner, null); if (value is not null) Interlocked.Add(ref value._reserved, -bytes); }
    }
}
