using CommunityToolkit.Mvvm.ComponentModel;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// 只管理已有有限文本的呈现与字面量查找，不读取文件、不重新解压、不修改保存正文。
/// 查找只保存当前命中位置，避免常见短词生成与全文大小成比例的匹配集合。
/// 原文与格式化字符串由已有内容预算的租约覆盖，页面清预览时同步清空。
/// </summary>
public sealed partial class BodyTextPresentation : ObservableObject
{
    [ObservableProperty] private int _mode;
    [ObservableProperty] private string _raw = "", _formatted = "", _query = "";
    [ObservableProperty] private bool _findVisible, _wrap = true;
    [ObservableProperty] private int _matchStart = -1;
    [ObservableProperty] private string _findStatus = "";
    public string[] Modes { get; } = ["格式化", "树形", "原文"];
    public bool CanFormat => Formatted.Length > 0;
    public bool ShowTree => CanFormat && Mode == 1;
    public string Text => CanFormat && Mode == 0 ? Formatted : Raw;
    partial void OnModeChanged(int value) { MatchStart = -1; Changed(); }
    partial void OnRawChanged(string value) => Changed();
    partial void OnFormattedChanged(string value) => Changed();
    partial void OnQueryChanged(string value) { MatchStart = -1; FindStatus = ""; }
    private void Changed()
    {
        OnPropertyChanged(nameof(Text)); OnPropertyChanged(nameof(CanFormat)); OnPropertyChanged(nameof(ShowTree));
    }
    public void Clear() { Raw = Formatted = ""; MatchStart = -1; FindStatus = ""; }
    public bool FindNext()
    {
        if (Query.Length is 0 or > 256) { FindStatus = "请输入 1～256 字符；只查找当前有限预览。"; return false; }
        if (ShowTree) Mode = 0;
        var start = Math.Min(Text.Length, Math.Max(0, MatchStart + 1));
        var found = Text.IndexOf(Query, start, StringComparison.OrdinalIgnoreCase);
        if (found < 0 && start > 0) found = Text.IndexOf(Query, StringComparison.OrdinalIgnoreCase);
        MatchStart = found;
        FindStatus = found < 0 ? "当前有限预览中未找到。" : "已定位；仅查找当前有限预览。";
        return found >= 0;
    }
}
