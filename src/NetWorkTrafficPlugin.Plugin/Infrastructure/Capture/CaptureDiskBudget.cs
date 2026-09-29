namespace NetWorkTrafficPlugin.Infrastructure.Capture;

/// <summary>
/// 托管根的物理占用与当前会话预留共同计费。只扫描带本插件标记的直接子目录，拒绝重解析点，
/// 不递归进入用户任意目录。写失败后的预留保守保留到本次运行结束，不能把可能存在的半文件当免费空间。
/// </summary>
public sealed class CaptureDiskBudget
{
    public const long RootLimit = 4L << 30;
    private readonly object _sync = new();
    private readonly long _existing, _sessionLimit, _rootLimit;
    private long _reserved;
    public CaptureDiskBudget(string root, long sessionLimit, long rootLimit = RootLimit)
    {
        Directory.CreateDirectory(root);
        EnsureRegular(root);
        if (sessionLimit < 0 || sessionLimit > 2L << 30 || rootLimit < 0 || rootLimit > RootLimit) throw new ArgumentOutOfRangeException(nameof(sessionLimit));
        _sessionLimit = sessionLimit; _rootLimit = rootLimit;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            EnsureRegular(directory);
            if (!File.Exists(Path.Combine(directory, ".network-traffic-session"))) continue;
            foreach (var path in Directory.EnumerateFiles(directory)) { EnsureRegular(path); _existing = checked(_existing + new FileInfo(path).Length); }
        }
    }
    public long Reserved { get { lock (_sync) return _reserved; } }
    public int Reserve(int wanted)
    {
        lock (_sync)
        {
            if (wanted < 0) throw new ArgumentOutOfRangeException(nameof(wanted));
            var allowed = (int)Math.Max(0, Math.Min(wanted, Math.Min(_sessionLimit - _reserved, _rootLimit - _existing - _reserved)));
            _reserved += allowed; return allowed;
        }
    }
    public void ReleaseReservation(int bytes) { lock (_sync) { if (bytes < 0 || bytes > _reserved) throw new ArgumentOutOfRangeException(nameof(bytes)); _reserved -= bytes; } }
    public static void EnsureRegular(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("记录路径及其父目录不允许重解析点。");
    }
}
