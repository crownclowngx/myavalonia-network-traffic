using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// View 管几何、焦点和用户输入来源，Model 管身份与跟随意图。刷新前按身份取锚点，
/// 刷新后只恢复同一查询和同一次用户意图；旧异步刷新不得把用户刚拖动的滚动条拉回。
/// 所有布局恢复同步合并在一个 UI Tick 内，不为每条新记录投递 Dispatcher 委托。
/// </summary>
public partial class CaptureView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private CaptureWorkspace? _model;
    private ScrollViewer? _listScroll;
    private bool _attached, _refreshing, _layoutBusy, _feedbackHovered;
    private Control? _fileReturnFocus;
    private long _focusRevision;
    private long _gestureRevision, _latestList, _latestEvent;
    private bool? _compact;
    public IWorkspaceFilePicker FilePicker { get; set; }
    public double LastAnchorError { get; private set; }
    public int ScrollRestorations { get; private set; }
    public CaptureView()
    {
        InitializeComponent();
        FilePicker = new WorkspaceFilePicker(() => TopLevel.GetTopLevel(this));
        _timer.Tick += async (_, _) => await RefreshUiAsync();
        DataContextChanged += (_, _) => BindModel();
        KeyDown += HandleKeys;
        AddHandler(PointerWheelChangedEvent, UserWheel, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, UserPointer, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, args) =>
        {
            // 拖条过程中也递增意图代次，让等待中的刷新放弃旧锚点。
            if (args.GetCurrentPoint(this).Properties.IsLeftButtonPressed) _gestureRevision++;
        }, RoutingStrategies.Tunnel);
        BodyTextBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty)
            {
                if (_model is { } model && BodyTextBox.SelectionStart != BodyTextBox.SelectionEnd)
                    model.EventReading.Pause(model.LatestEvent);
            }
        };
    }
    private void BindModel()
    {
        if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.Body.PropertyChanged -= BodyChanged; _model.EventReading.PropertyChanged -= EventFollowChanged; }
        _model = DataContext as CaptureWorkspace; _compact = null; _latestList = _latestEvent = -1;
        if (_model is not null) { _model.PropertyChanged += ModelChanged; _model.Body.PropertyChanged += BodyChanged; _model.EventReading.PropertyChanged += EventFollowChanged; }
        LayoutPanes();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(CaptureWorkspace.FileDialogVisible))
        {
            if (_model?.FileDialogVisible == true) _fileReturnFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
            var generation = ++_focusRevision;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_attached || generation != _focusRevision || _model is null) return;
                if (_model.FileDialogVisible) ArchivePathBox.Focus();
                else if (_fileReturnFocus?.IsEffectivelyVisible == true) _fileReturnFocus.Focus();
                else if (_model.AdvancedVisible) AdvancedMethods.Focus();
                else if (BodyTextBox.IsEffectivelyVisible) BodyTextBox.Focus();
                else FilterBox.Focus();
            });
        }
        if (args.PropertyName == nameof(CaptureWorkspace.AdvancedVisible) && _model?.AdvancedVisible == false)
        { if (_model.Body.FindVisible) BodyFindBox.Focus(); else FilterBox.Focus(); }
        if (args.PropertyName is nameof(CaptureWorkspace.ShowDetails) or nameof(CaptureWorkspace.IsCompact)) LayoutPanes();
        if (args.PropertyName == nameof(CaptureWorkspace.Selected) && !_refreshing && _model?.Selected is { } row)
            TrafficList.ScrollIntoView(row);
        if (args.PropertyName == nameof(CaptureWorkspace.Follow) && _model?.Follow == true) _latestList = -1;

        if (args.PropertyName == nameof(CaptureWorkspace.ShowDetails) && _model?.ShowDetails == false)
        { TrafficList.Focus(); if (_model.Selected is { } selected) TrafficList.ScrollIntoView(selected); }
    }
    private void EventFollowChanged(object? sender, PropertyChangedEventArgs args)
    {
        // 只有明确恢复跟随才强制定位；普通状态文案刷新不能打断无新增内容的阅读。
        if (args.PropertyName == nameof(ReadingFollowState.Following) && _model?.EventReading.Following == true) _latestEvent = -1;
    }
    private void BodyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(BodyTextPresentation.Wrap)) BodyTextBox.TextWrapping = _model?.Body.Wrap == false ? Avalonia.Media.TextWrapping.NoWrap : Avalonia.Media.TextWrapping.Wrap;
        if (args.PropertyName == nameof(BodyTextPresentation.MatchStart) && _model?.Body.MatchStart is >= 0)
        {
            // 设置 CaretIndex 会折叠选区，必须先定位光标再建立命中范围。
            BodyTextBox.CaretIndex = _model.Body.MatchStart + _model.Body.Query.Length;
            BodyTextBox.SelectionStart = _model.Body.MatchStart;
            BodyTextBox.SelectionEnd = _model.Body.MatchStart + _model.Body.Query.Length;
        }
    }
    private Task? _pendingRefresh;
    // 同一页面只运行一个刷新；显式调用者等待已有批次，而不是误以为一次忙碌中的空返回已完成刷新。
    public Task RefreshUiAsync()
    {
        if (_pendingRefresh is { IsCompleted: false }) return _pendingRefresh;
        return _pendingRefresh = RefreshUiCoreAsync();
    }
    private async Task RefreshUiCoreAsync()
    {
        var model = _model;
        if (!_attached || model is null || _refreshing) return;
        _refreshing = true;
        _listScroll ??= TrafficList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        var gesture = _gestureRevision; var query = model.AppliedQueryRevision; var source = model.Session;
        var anchor = CaptureListAnchor();
        var eventAnchor = CaptureEventAnchor();
        var selection = model.Selected?.RecordId; var direction = model.RequestSelected;
        try
        {
            // 使用公开 TextPresenter 的组合文本状态；输入法未提交时保留上次有效查询。
            model.Feedback.Hold = _feedbackHovered || FeedbackBar.IsKeyboardFocusWithin;
            model.IsComposing = FilterBox.GetVisualDescendants().OfType<TextPresenter>().Any(p => !string.IsNullOrEmpty(p.PreeditText));
            await model.RefreshAsync();
            if (!_attached || !ReferenceEquals(model, _model) || model.IsDisposed || gesture != _gestureRevision) return;
            UpdateLayout();
            if (model.Follow && model.Rows.Count > 0 && _latestList != model.LatestSequence)
                TrafficList.ScrollIntoView(model.Rows[0]);
            else if (!model.Follow && ReferenceEquals(source, model.Session) && query == model.AppliedQueryRevision && anchor is not null)
                RestoreListAnchor(anchor);
            if (model.HasStream && selection == model.Selected?.RecordId && direction == model.RequestSelected)
            {
                if (model.EventReading.Following && _latestEvent != model.LatestEvent) BodyScroll.ScrollToEnd();
                else if (!model.EventReading.Following && eventAnchor is not null) RestoreEventAnchor(eventAnchor.Value);
            }
            _latestList = model.LatestSequence; _latestEvent = model.LatestEvent;
        }
        finally { _refreshing = false; }
    }
    public ReadingAnchor? CaptureListAnchor()
    {
        if (_listScroll is null) return null;
        return TrafficList.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (Item: item, Point: item.TranslatePoint(default, _listScroll)))
            .Where(pair => pair.Point is { } point && point.Y + pair.Item.Bounds.Height > 0 && point.Y < _listScroll.Viewport.Height)
            .OrderBy(pair => pair.Point!.Value.Y)
            .Select(pair => pair.Item.DataContext is TrafficSummary row ? new ReadingAnchor(row.RecordId, row.Sequence, pair.Point!.Value.Y) : null).FirstOrDefault();
    }
    private void RestoreListAnchor(ReadingAnchor anchor)
    {
        if (_model is null || _listScroll is null) return;
        var id = anchor.Resolve(_model.Rows.Select(row => (row.RecordId, row.Sequence)));
        var row = _model.Rows.FirstOrDefault(item => item.RecordId == id);
        if (row is null) return;
        var container = TrafficList.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is TrafficSummary data && data.RecordId == id);
        if (container is null)
        {
            TrafficList.ScrollIntoView(row); UpdateLayout();
            container = TrafficList.GetVisualDescendants().OfType<ListBoxItem>().FirstOrDefault(item => item.DataContext is TrafficSummary data && data.RecordId == id);
        }
        if (container?.TranslatePoint(default, _listScroll) is not { } point) return;
        _listScroll.Offset = new(_listScroll.Offset.X, Math.Max(0, _listScroll.Offset.Y + point.Y - anchor.Offset));
        UpdateLayout(); ScrollRestorations++;
        LastAnchorError = id == anchor.RecordId && container.TranslatePoint(default, _listScroll) is { } after ? Math.Abs(after.Y - anchor.Offset) : 0;
    }
    private (long Sequence, double Y)? CaptureEventAnchor()
    {
        return StreamEvents.GetVisualDescendants().OfType<ContentPresenter>()
            .Where(item => item.DataContext is StreamEvent && item.TranslatePoint(default, BodyScroll) is { } p && p.Y + item.Bounds.Height > 0)
            .Select(item => ((StreamEvent)item.DataContext!, item.TranslatePoint(default, BodyScroll)!.Value.Y))
            .OrderBy(pair => pair.Y).Select(pair => ((long Sequence, double Y)?)(pair.Item1.Sequence, pair.Y)).FirstOrDefault();
    }
    private void RestoreEventAnchor((long Sequence, double Y) anchor)
    {
        var entries = StreamEvents.GetVisualDescendants().OfType<ContentPresenter>().Where(item => item.DataContext is StreamEvent).ToArray();
        var entry = entries.FirstOrDefault(item => ((StreamEvent)item.DataContext!).Sequence == anchor.Sequence) ??
            entries.OrderBy(item => ((StreamEvent)item.DataContext!).Sequence).FirstOrDefault();
        if (entry?.TranslatePoint(default, BodyScroll) is { } point)
            BodyScroll.Offset = new(BodyScroll.Offset.X, Math.Max(0, BodyScroll.Offset.Y + point.Y - anchor.Y));
    }
    private static bool Inside(object? source, Control parent) => source is Visual visual && (ReferenceEquals(visual, parent) || visual.GetVisualAncestors().Contains(parent));
    private void UserWheel(object? sender, PointerWheelEventArgs args)
    {
        if (_model is null) return;
        _gestureRevision++;
        if (Inside(args.Source, TrafficList) && args.Delta.Y < 0 && _listScroll is { } scroll && scroll.Extent.Height > scroll.Viewport.Height + 2) _model.ListReading.Pause(_model.LatestSequence);
        if (Inside(args.Source, BodyScroll) && args.Delta.Y > 0 && BodyScroll.Offset.Y > 2) _model.EventReading.Pause(_model.LatestEvent);
    }
    private void UserPointer(object? sender, PointerPressedEventArgs args)
    {
        if (_model is null) return;
        _gestureRevision++;
        if (args.Source is Visual visual && visual.GetVisualAncestors().Any(item => item is ScrollBar))
        {
            if (Inside(args.Source, TrafficList)) _model.ListReading.Pause(_model.LatestSequence);
            if (Inside(args.Source, BodyScroll)) _model.EventReading.Pause(_model.LatestEvent);
        }
        if (Inside(args.Source, StreamEvents)) _model.EventReading.Pause(_model.LatestEvent);
    }
    // 顶部工具栏也是当前 Document 的一部分。外框在未消费的冒泡阶段转交局部快捷键，
    // 不能使用全局注册，否则会抢 Host 命令；子控件已经消费的键也不再次执行。
    public void RoutePageKey(KeyEventArgs args) { if (!args.Handled) HandleKeys(this, args); }
    private void HandleKeys(object? sender, KeyEventArgs args)
    {
        if (_model is not { } model) return;
        if (model.FileDialogVisible && args.Key != Key.Escape) return;
        if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.L)
        { model.BackCommand.Execute(null); FilterBox.Focus(); args.Handled = true; }
        else if (args.KeyModifiers == KeyModifiers.Control && args.Key == Key.F)
        { ShowFind(this, new()); args.Handled = true; }
        else if (args.Key == Key.Escape)
        {
            if (model.FileDialogVisible) model.CloseFileDialog();
            else if (model.AdvancedVisible) model.AdvancedVisible = false;
            else if (model.Body.FindVisible) { model.Body.FindVisible = false; BodyTextBox.Focus(); }
            else model.BackCommand.Execute(null);
            args.Handled = true;
        }
        else if (Inside(args.Source, TrafficList) && args.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End)
        { _gestureRevision++; model.ListReading.Pause(model.LatestSequence); }
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    { base.OnPropertyChanged(change); if (change.Property == BoundsProperty && MainGrid is not null) LayoutPanes(); }
    private void LayoutPanes()
    {
        if (_model is null || MainGrid is null || _layoutBusy) return;
        _layoutBusy = true;
        try
        {
            var compact = Bounds.Width < 720;
            if (_compact != compact)
            {
                if (_compact == false && MainGrid.Bounds.Width > 0) _model.SplitRatio = Math.Clamp(MainGrid.ColumnDefinitions[0].ActualWidth / MainGrid.Bounds.Width, .25, .65);
                MainGrid.ColumnDefinitions = compact ? new("*") : new(FormattableString.Invariant($"{_model.SplitRatio}*,6,{1 - _model.SplitRatio}*"));
                if (!compact) { MainGrid.ColumnDefinitions[0].MinWidth = 260; MainGrid.ColumnDefinitions[2].MinWidth = 360; }
                _compact = compact;
            }
            _model.IsCompact = compact; Grid.SetColumn(DetailsPane, compact ? 0 : 2);
            PaneSplitter.IsVisible = !compact;
            ListPane.IsVisible = !compact || !_model.ShowDetails; DetailsPane.IsVisible = !compact || _model.ShowDetails;
        }
        finally { _layoutBusy = false; }
    }
    private void RemoveFilterChip(object? sender, RoutedEventArgs args) { if (sender is Button { Tag: string key }) _model?.RemoveChipCommand.Execute(key); }
    private void ShowFind(object? sender, RoutedEventArgs args) { if (_model is { } model) { if (model.HasImage || model.HasStream) { model.Feedback.Complete(model.Feedback.Begin(), "此视图请先选择可用文本；正文查找仅作用于当前文本预览。", true); return; } model.Body.FindVisible = true; if (model.Body.ShowTree) model.Body.Mode = 0; DetailsTabs.SelectedIndex = 0; BodyFindBox.Focus(); } }
    private void CloseFind(object? sender, RoutedEventArgs args) { if (_model is { } model) model.Body.FindVisible = false; BodyTextBox.Focus(); }
    private void BeginExport(object? sender, RoutedEventArgs args) => _model?.BeginFileOperation(WorkspaceFileKind.Export);
    private void CloseFile(object? sender, RoutedEventArgs args) => _model?.CloseFileDialog();
    private async void BrowseFile(object? sender, RoutedEventArgs args)
    {
        if (_model is not { } model) return;
        var revision = model.FileDialogRevision;
        await model.BrowseFileAsync(FilePicker);
        // 浏览按钮在等待时禁用会失去焦点；取消返回后仍须能用 Esc 关闭当前弹层。
        // 仅恢复同页同操作，换页/关闭/新操作后的迟到返回不能抢走用户焦点。
        if (_attached && ReferenceEquals(model, _model) && !model.IsDisposed && model.FileDialogVisible && revision == model.FileDialogRevision) ArchivePathBox.Focus();
    }
    private async void CopyAddress(object? sender, RoutedEventArgs args)
    {
        if (_model is not { } model) return;
        var operation = model.Feedback.Begin();
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) throw new NotSupportedException("当前宿主没有剪贴板。");
            await clipboard.SetTextAsync(model.DisplayAddress);
            if (!model.IsDisposed) model.Feedback.Complete(operation, "已复制当前显示的地址。");
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        { if (!model.IsDisposed) model.Feedback.Complete(operation, ex.Message, true); }
    }
    private void HoldFeedback(object? sender, PointerEventArgs args) { _feedbackHovered = true; if (_model is not null) _model.Feedback.Hold = true; }
    private void ReleaseFeedback(object? sender, PointerEventArgs args) { _feedbackHovered = false; if (_model is not null) _model.Feedback.Hold = FeedbackBar.IsKeyboardFocusWithin; }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs args)
    { base.OnAttachedToVisualTree(args); _attached = true; BindModel(); _timer.Start(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs args)
    {
        _attached = false; _gestureRevision++; _timer.Stop();
        if (_model is not null) { _model.PropertyChanged -= ModelChanged; _model.Body.PropertyChanged -= BodyChanged; _model.EventReading.PropertyChanged -= EventFollowChanged; }
        _listScroll = null; base.OnDetachedFromVisualTree(args);
    }
}
