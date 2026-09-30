using CommunityToolkit.Mvvm.ComponentModel;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// 页面异步操作的反馈水位。旧操作完成或旧提示到期不能覆盖后发操作。
/// 到期由页面已有时钟检查，测试可传入确定时间；不为每条提示创建后台 Timer。
/// </summary>
public sealed partial class OperationFeedback : ObservableObject
{
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private bool _isError;
    public bool Hold { get; set; }
    private long _revision;
    private DateTimeOffset? _expires;
    public long Begin() { _expires = null; return ++_revision; }
    public void Complete(long revision, string text, bool error = false, DateTimeOffset? now = null)
    {
        if (revision != _revision) return;
        Text = text; IsError = error;
        _expires = error ? null : (now ?? DateTimeOffset.UtcNow).AddSeconds(3);
    }
    public void Tick(DateTimeOffset now)
    {
        if (Hold && _expires is not null) { _expires = now.AddSeconds(3); return; }
        if (_expires is { } expires && now >= expires) { Text = ""; _expires = null; }
    }
    public void Dismiss() { Begin(); Text = ""; IsError = false; }
}
