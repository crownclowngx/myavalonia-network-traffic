using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Features.Capture;

public sealed partial class CaptureWorkspace
{
    [ObservableProperty] private int _transportIndex;
    [ObservableProperty] private bool _queryPending, _advancedVisible;
    [ObservableProperty] private string _filterError = "", _emptyText = "请先启动代理并接入客户端。";
    public bool IsComposing { get; set; }
    public bool IsDisposed => _disposed;
    public long SourceRevision { get; private set; }
    public long AppliedQueryRevision { get; private set; }
    private double _splitRatio = .4;
    // 比例属于页面，不写进共享捕获；拒绝 NaN，避免用户拖动后的宽窄切换产生非法 GridLength。
    public double SplitRatio { get => _splitRatio; set => _splitRatio = double.IsFinite(value) ? Math.Clamp(value, .25, .65) : .4; }
    public string BodySummary => BodyStatus.Split('\n')[0];
    // 编码默认值属于说明；截断、解码失败与写入原因必须仍然常驻可见，不能全部藏进 tooltip。
    public string BodyWarning => string.Join(" · ", BodyStatus.Split('\n').Skip(1).Where(line => !string.IsNullOrWhiteSpace(line) && line != "未声明编码，使用 UTF-8。"));
    public bool HasBodyWarning => BodyWarning.Length > 0;
    partial void OnBodyStatusChanged(string value) { OnPropertyChanged(nameof(BodySummary)); OnPropertyChanged(nameof(BodyWarning)); OnPropertyChanged(nameof(HasBodyWarning)); }
    partial void OnBodyTextChanged(string value) => Body.Raw = value;
    public string[] TransportChoices { get; } = ["全部传输", "接收中", "传输失败"];
    public string[] DirectionChoices { get; } = ["响应", "请求"];
    public ObservableCollection<FilterChip> Chips { get; } = [];
    public bool HasChips => Chips.Count > 0;
    public bool HasFilterError => FilterError.Length > 0;
    public bool HasMessage => Message.Length > 0;
    public bool IsEmpty => Rows.Count == 0;
    public bool IsLive => Session?.IsArchive != true;
    public bool HasSelection => Selected is not null;
    public bool HasFragment => BodyText.Length > 0 && HasStream;
    public int DirectionIndex { get => RequestSelected ? 1 : 0; set => RequestSelected = value == 1; }
    public string HeadersText => RequestSelected ? RequestHeadersText : ResponseHeadersText;
    public string DisplayAddress => _record is null ? "" : RevealSensitive ? _record.EffectiveUri : _record.EffectiveUri.Split('?')[0];
    public string SelectedTitle => Selected is { } row ? $"{row.Method} {row.DisplayPath} · {row.StatusText} · {row.ElapsedMilliseconds:N0} ms" : "选择一条请求";
    public long LatestSequence => Rows.Select(row => row.Sequence).DefaultIfEmpty().Max();
    public long LatestEvent => Events.Select(item => item.Sequence).DefaultIfEmpty().Max();
    public string EventFollowLabel => EventReading.Following ? "暂停事件跟随" : $"新增可见 {EventReading.Pending} 条 · 最新事件";
    public bool ShowBodyText => !HasImage && !HasStream && !Body.ShowTree;
    public bool ShowJsonTree => HasJson && Body.ShowTree;
    public IRelayCommand PreviousCommand { get; private set; } = null!;
    public IRelayCommand NextCommand { get; private set; } = null!;
    public IRelayCommand ToggleEventsCommand { get; private set; } = null!;
    public IRelayCommand<string> RemoveChipCommand { get; private set; } = null!;
    public IRelayCommand ToggleAdvancedCommand { get; private set; } = null!;
    public IRelayCommand DismissMessageCommand { get; private set; } = null!;
    public IRelayCommand FindNextCommand { get; private set; } = null!;

    private void InitializeInteraction()
    {
        PreviousCommand = new RelayCommand(() => Navigate(-1), () => SelectionIndex > 0);
        NextCommand = new RelayCommand(() => Navigate(1), () => SelectionIndex >= 0 && SelectionIndex + 1 < Rows.Count);
        ToggleEventsCommand = new RelayCommand(() => { if (EventReading.Following) EventReading.Pause(LatestEvent); else EventReading.Resume(LatestEvent); });
        ToggleAdvancedCommand = new RelayCommand(() => AdvancedVisible = !AdvancedVisible);
        RemoveChipCommand = new RelayCommand<string>(RemoveChip);
        DismissMessageCommand = new RelayCommand(() => { Feedback.Dismiss(); Message = ""; });
        FindNextCommand = new RelayCommand(() => Body.FindNext());
        ListReading.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ReadingFollowState.Following)) OnPropertyChanged(nameof(Follow));
            OnPropertyChanged(nameof(FollowLabel));
        };
        EventReading.PropertyChanged += (_, _) => OnPropertyChanged(nameof(EventFollowLabel));
        Feedback.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(OperationFeedback.Text)) Message = Feedback.Text; };
        Body.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(BodyTextPresentation.ShowTree)) { UpdateJsonTree(); NotifyBodyView(); }
        };
    }
    private int SelectionIndex => Selected is { } selected ? Rows.IndexOf(selected) : -1;
    private void Navigate(int offset)
    {
        var index = SelectionIndex + offset;
        if (index >= 0 && index < Rows.Count) { ListReading.Pause(LatestSequence); Selected = Rows[index]; }
    }
    private void NotifySelection()
    {
        OnPropertyChanged(nameof(HasSelection)); OnPropertyChanged(nameof(SelectedTitle));
        PreviousCommand?.NotifyCanExecuteChanged(); NextCommand?.NotifyCanExecuteChanged();
    }
    private void NotifyBodyView() { OnPropertyChanged(nameof(ShowBodyText)); OnPropertyChanged(nameof(ShowJsonTree)); }
    private void UpdateJsonTree()
    {
        // 树只在显示时物化，离开即释放节点；隐藏 TreeView 并不会自动释放 ItemsSource。
        if (!Body.ShowTree) { JsonNodes.Clear(); return; }
        if (JsonNodes.Count == 0 && Body.Formatted.Length > 0)
            foreach (var node in JsonPreviewNode.Parse(Body.Formatted)) JsonNodes.Add(node);
    }
    partial void OnHasImageChanged(bool value) => NotifyBodyView();
    partial void OnHasJsonChanged(bool value) => NotifyBodyView();
    partial void OnHasStreamChanged(bool value) { NotifyBodyView(); OnPropertyChanged(nameof(HasFragment)); }
    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));
    partial void OnFilterErrorChanged(string value) => OnPropertyChanged(nameof(HasFilterError));
    partial void OnTransportIndexChanged(int value)
    {
        _syncingFilter = true;
        try { OnlyReceiving = value == 1; OnlyFailed = value == 2; }
        finally { _syncingFilter = false; }
        QueryChanged();
    }
    private void UpdateChips(CaptureQuery query)
    {
        var next = new List<FilterChip>();
        if (query.Keyword.Length > 0) next.Add(new("keyword", "关键字：" + query.Keyword));
        if (query.Methods is { Length: > 0 }) next.Add(new("method", string.Join(",", query.Methods)));
        if (query.StatusClass is { } status) next.Add(new("status", status + "xx"));
        if (query.StatusCode is { } code) next.Add(new("status", code.ToString()));
        if (query.Transport is not null) next.Add(new("transport", TransportChoices[TransportIndex]));
        if (query.From is not null || query.To is not null) next.Add(new("time", "时间范围"));
        if (query.AfterSequence > 0) next.Add(new("range", "仅看清空后"));
        if (Chips.SequenceEqual(next)) return;
        Chips.Clear(); foreach (var chip in next) Chips.Add(chip);
        OnPropertyChanged(nameof(HasChips));
    }
    private void RemoveChip(string? key)
    {
        switch (key)
        {
            case "keyword": Keyword = ""; break;
            case "method": MethodFilter = ""; break;
            case "status": StatusIndex = 0; StatusCodeFilter = ""; break;
            case "transport": TransportIndex = 0; break;
            case "time": FromText = ToText = ""; break;
            case "range": _afterSequence = 0; QueryChanged(); break;
        }
    }
}
