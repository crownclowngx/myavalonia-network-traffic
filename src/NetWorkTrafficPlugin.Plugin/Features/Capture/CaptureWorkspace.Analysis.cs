using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Features.Capture;

public sealed partial class CaptureWorkspace
{
    private Guid? _analysisDetailId;
    /// <summary>
    /// 列表选择只属于可见行，详情可以由分组按身份打开隐藏行。
    /// 分开这两个入口，避免ListBox把“不在ItemsSource”的选择清空后连带抹去详情。
    /// </summary>
    public TrafficSummary? ListSelected
    {
        get => Selected is { } selected && _rows.TryGetValue(selected.RecordId, out var row) ? row : null;
        set
        {
            if (value is null && _analysisDetailId is not null) return;
            if (value?.RecordId != _analysisDetailId) _analysisDetailId = null;
            Selected = value;
        }
    }
    /// <summary>
    /// 分组按记录身份进入现有详情，不借用旧列表下标。分析可能包含被列表条件隐藏的记录，
    /// 此处只打开详情，不改写列表筛选；读取完成后再次核对数据源及页面寿命。
    /// </summary>
    public async Task<bool> OpenAnalyzedRecordAsync(ICaptureSession source, Guid id)
    {
        if (_disposed || !ReferenceEquals(Session, source)) return false;
        var record = await source.ReadAsync(id, _closing.Token);
        if (_disposed || !ReferenceEquals(Session, source) || record is null) return false;
        var uri = new Uri(record.OriginalUri);
        _analysisDetailId = _rows.ContainsKey(id) ? null : id;
        Selected = new(record.RecordId, record.SessionId, record.Sequence, record.Revision, record.StartedUtc, record.Method, uri.Host, uri.AbsolutePath,
            record.StatusCode, record.ElapsedMilliseconds, record.RequestBody.ObservedBytes + record.ResponseBody.ObservedBytes,
            record.RequestBody.CommittedBytes + record.ResponseBody.CommittedBytes, record.ResponseBody.Transport, record.ResponseBody.Capture, record.MetadataPartial);
        ShowDetails = true; await DetailTask;
        if (_analysisDetailId is not null) Message = "从分组打开的请求不在当前列表范围；筛选保持不变。";
        return !_disposed && ReferenceEquals(Session, source) && Selected?.RecordId == id;
    }
}
