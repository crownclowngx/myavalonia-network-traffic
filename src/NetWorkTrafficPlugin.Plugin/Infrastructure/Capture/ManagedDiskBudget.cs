namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 同一插件容器的代理、调试会话与正文资产共用4GiB总额。预留在入队/复制之前完成，失败才归还。
/// 根重开采用物理占用与已知预留的较大者，避免把旧会话重复计费；删除后的高水位在本容器内保守保留，
/// 下次容器启动重新扫描。33MiB固定留给两个8MiB索引及各自原子提交临时副本、资产小型元信息，不扩大原根上限。
/// </summary>
public sealed class ManagedDiskBudget(long limit = CaptureDiskBudget.RootLimit)
{
    private readonly object _sync = new();
    private readonly Dictionary<string, long> _roots = new(StringComparer.OrdinalIgnoreCase);
    private const long Metadata = 33L << 20;
    public long Reserved { get { lock (_sync) return Metadata + _roots.Values.Sum(); } }
    public void Register(string root, long existing)
    {
        if (existing < 0) throw new ArgumentOutOfRangeException(nameof(existing));
        lock (_sync) { root = Path.GetFullPath(root); _roots[root] = Math.Max(existing, _roots.GetValueOrDefault(root)); }
    }
    public int Reserve(string root, int wanted)
    {
        if (wanted < 0) throw new ArgumentOutOfRangeException(nameof(wanted));
        lock (_sync)
        {
            root = Path.GetFullPath(root); var allowed = (int)Math.Min(wanted, Math.Max(0, limit - Reserved));
            _roots[root] = _roots.GetValueOrDefault(root) + allowed; return allowed;
        }
    }
    public void Release(string root, long bytes)
    {
        lock (_sync)
        {
            root = Path.GetFullPath(root); var current = _roots.GetValueOrDefault(root);
            if (bytes < 0 || bytes > current) throw new ArgumentOutOfRangeException(nameof(bytes)); _roots[root] = current - bytes;
        }
    }
}
