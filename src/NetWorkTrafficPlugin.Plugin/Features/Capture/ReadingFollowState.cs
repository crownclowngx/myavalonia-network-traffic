using CommunityToolkit.Mvvm.ComponentModel;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// 一个阅读区域的跟随意图；列表与事件各持有一份，不能共用开关。
/// 这里只处理身份和水位，不持有控件、网络句柄或无限增长的已见 ID 集合。
/// 程序恢复视口不调用 Pause，避免布局反馈把主动跟随误判为用户滚动。
/// </summary>
public sealed partial class ReadingFollowState : ObservableObject
{
    [ObservableProperty] private bool _following = true;
    [ObservableProperty] private int _pending;
    private long _watermark;
    public void Reset(long latest = 0) { _watermark = latest; Pending = 0; }
    public void Pause(long latest) { if (Following) Reset(latest); Following = false; }
    public void Resume(long latest) { Reset(latest); Following = true; }
    public void Observe(IEnumerable<long> retained)
    {
        var values = retained.ToArray();
        if (Following) Reset(values.DefaultIfEmpty().Max());
        else Pending = values.Count(value => value > _watermark);
    }
}

/// <summary>锚点保存身份而不是集合索引；插入新请求不会让旧索引指向另一行。</summary>
public sealed record ReadingAnchor(Guid RecordId, long Sequence, double Offset)
{
    public Guid? Resolve(IEnumerable<(Guid Id, long Sequence)> retained)
    {
        var rows = retained.ToArray();
        if (rows.Any(row => row.Id == RecordId)) return RecordId;
        // 淘汰时优先选择最接近的较旧行。只恢复视口，绝不替用户改变选中项。
        return rows.Where(row => row.Sequence < Sequence).OrderByDescending(row => row.Sequence)
            .Concat(rows.Where(row => row.Sequence > Sequence).OrderBy(row => row.Sequence))
            .Select(row => (Guid?)row.Id).FirstOrDefault();
    }
}
