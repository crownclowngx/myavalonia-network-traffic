using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Rules;
using NetWorkTrafficPlugin.Features.Rules;

namespace NetWorkTrafficPlugin.Features.Analysis;

/// <summary>
/// 页面只协调草稿、执行、选择和保存。分析在工作线程运行，结果提交复核源/草稿/操作代次；
/// Cancel不能撤回已经排队的UI回调，因此不能只依赖CancellationToken。
/// </summary>
public sealed partial class AnalysisWorkspace : ObservableObject, IDisposable
{
    private readonly AnalysisQueryEngine _engine;
    private readonly IRuleLibraryStore _store;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _operation;
    private IAnalysisSource? _source;
    private AnalysisResult? _result;
    private RuleRevision? _applied;
    private RuleRevision? _draftRevision;
    private RuleLibrary _library = RuleLibrary.Empty;
    private Guid? _editingView;
    private long _generation, _libraryGeneration;
    private bool _disposed;
    public RuleEditorModel Editor { get; } = new();
    public ObservableCollection<TrafficGroup> Groups { get; } = [];
    public ObservableCollection<AnalyzedRequest> Requests { get; } = [];
    public ObservableCollection<SavedView> SavedViews { get; } = [];
    public AnalysisResult? Result => _result;
    public RuleRevision? AppliedRevision => _applied;
    public Task CurrentTask { get; private set; } = Task.CompletedTask;
    public long LateResultsDiscarded { get; private set; }
    public DateTimeOffset TimelineStart { get; private set; }
    public double TimelineMilliseconds { get; private set; } = 1;
    [ObservableProperty] private bool _busy, _showEditor, _showTimeline, _revealValues, _includeContext, _sortByCount, _hasUpdates;
    [ObservableProperty] private bool _hasLibraryUpdates;
    [ObservableProperty] private string _status = "选择实时捕获或只读存档后，应用规则分析。", _previewText = "", _selectionText = "", _viewName = "调用过程";
    [ObservableProperty] private string _resultDescription = "";
    [ObservableProperty] private string _paneTitle = "选择一个业务组";
    [ObservableProperty] private string _windowText = "", _sourcesText = "capture", _columnsText = "";
    [ObservableProperty] private TrafficGroup? _selectedGroup;
    [ObservableProperty] private AnalyzedRequest? _selectedRequest;
    [ObservableProperty] private SavedView? _selectedView;
    public event Action<AnalyzedRequest>? OpenRequest;
    public IRelayCommand ValidateCommand { get; }
    public IAsyncRelayCommand ApplyCommand { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand SaveAsCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand DeleteViewCommand { get; }
    public IRelayCommand LoadViewCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public IRelayCommand OpenRequestCommand { get; }
    public IRelayCommand ShowUnknownCommand { get; }
    public IRelayCommand ShowContextCommand { get; }
    public IRelayCommand InsertFieldCommand { get; }
    public IRelayCommand GenerateFieldCommand { get; }
    public AnalysisWorkspace(AnalysisQueryEngine engine, IRuleLibraryStore store)
    {
        (_engine, _store) = (engine, store);
        ValidateCommand = new RelayCommand(() => Editor.Validate());
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, AsyncRelayCommandOptions.AllowConcurrentExecutions);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        SaveAsCommand = new AsyncRelayCommand(async () => { _editingView = null; await SaveAsync(); });
        ReloadCommand = new AsyncRelayCommand(ReloadAsync);
        DeleteViewCommand = new AsyncRelayCommand(DeleteSelectedViewAsync);
        LoadViewCommand = new RelayCommand(LoadSelectedView);
        CancelCommand = new RelayCommand(() => { _generation++; _operation?.Cancel(); Status = "已取消，保留上次成功结果。"; });
        ClearCommand = new RelayCommand(Clear);
        OpenRequestCommand = new RelayCommand(() => { if (SelectedRequest is { } request && _result?.SourceId == _source?.InstanceId) OpenRequest?.Invoke(request); else Status = "记录已换源，请重新分析。"; });
        ShowUnknownCommand = new RelayCommand(() => ShowSpecialRows(_result?.Unresolved ?? [], "未确定身份（不计业务组）"));
        ShowContextCommand = new RelayCommand(() => ShowSpecialRows(_result?.Context ?? [], "窗口前在途上下文（不计主统计）"));
        GenerateFieldCommand = new RelayCommand(() => FormAction(false));
        InsertFieldCommand = new RelayCommand(() => FormAction(true));
    }
    private void FormAction(bool insert)
    { try { if (insert) Editor.InsertFragment(); else Editor.GenerateFragment(); } catch (ArgumentException ex) { Status = ex.Message; } }
    public void SetSource(IAnalysisSource? source, long revision = 0)
    {
        ObserveLibraryGeneration();
        if (_source?.InstanceId == source?.InstanceId) { HasUpdates = _result is not null && _result.SnapshotRevision != revision; return; }
        _generation++; _operation?.Cancel(); _source = source; ClearResult(); RevealValues = false; PreviewText = "";
        Status = source is null ? "当前没有捕获数据源。" : source.IsArchive ? "只读存档；历史窗口按最后请求定位，重算不发网。" : "实时源已选择；点击应用/刷新固定一次快照。";
    }
    // 复用页面已有的刷新节拍，只比较一个共享代次，不添加文件监听器或长寿命事件订阅。
    public void ObserveLibraryGeneration() => HasLibraryUpdates = _store.KnownGeneration > _library.Generation;
    public Task ApplyAsync()
    {
        // 合并为最新一次意图；下一项在旧任务退出后开始，不并行跑同一页的两个候选。
        var prior = CurrentTask; var generation = ++_generation; _operation?.Cancel();
        return CurrentTask = ApplyAfterAsync(prior, generation);
    }
    private async Task ApplyAfterAsync(Task prior, long generation)
    {
        try { await prior; } catch (OperationCanceledException) { }
        if (_disposed || generation != _generation) return;
        var compiled = Editor.Validate(); if (!compiled.Success) { Status = "草稿无效，显示上次成功结果。"; return; }
        if (_source is null) { Status = "请先选择实时捕获或只读存档。"; return; }
        var source = _source; var editorGeneration = Editor.Generation;
        var revision = _draftRevision?.SourceText == Editor.Text ? _draftRevision : RuleRevision.Create(Editor.Text);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _operation = cancellation; Busy = true;
        AnalysisResult? result = null;
        try
        {
            var options = Options(compiled.Program!);
            result = await Task.Run(() => _engine.RunAsync(source, compiled.Program!, revision.RevisionId, revision.SourceHash, options, cancellation.Token), cancellation.Token);
            if (_disposed || generation != _generation || source.InstanceId != _source?.InstanceId || editorGeneration != Editor.Generation)
            { LateResultsDiscarded++; return; }
            var selectedId = SelectedGroup?.Id;
            ClearResult(); _result = result; result = null; _applied = revision; RevealValues = false;
            foreach (var group in _result.Groups) Groups.Add(group);
            HasUpdates = false;
            ResultDescription = $"{(source.IsArchive ? "只读存档，按最后请求定位" : "实时快照")} · 来源 {string.Join(',', options.Sources ?? ["capture"])} · 修订 {revision.RevisionId.ToString("N")[..8]}\n" +
                $"{_result.From:u} 至 {_result.To:u} [开始,结束)\n{_result.Coverage.Description}";
            Status = _result.Coverage.Retained == 0 ? "当前保留范围没有记录。" :
                _result.Coverage.FilterTrue == 0 ? "本次范围没有匹配请求。" :
                _result.Groups.Count == 0 ? "匹配请求的分组身份全部未确定。" : "分析完成；数据变化后需手动刷新。";
            if (Groups.Count > 0) SelectedGroup = Groups.FirstOrDefault(g => g.Id == selectedId) ?? Groups[0]; else if (_result.Unresolved.Count > 0) ShowUnknownCommand.Execute(null);
        }
        catch (OperationCanceledException) { if (generation == _generation) Status = "分析取消或超过10秒截止；显示上次成功结果。"; }
        catch (Exception ex) when (ex is AnalysisLimitException or IOException or ArgumentException or InvalidOperationException)
        { if (generation == _generation) Status = $"分析未提交：{ex.Message}；显示上次成功结果。"; }
        finally { result?.Dispose(); if (ReferenceEquals(_operation, cancellation)) { _operation = null; Busy = false; } }
    }
    public Task PreviewAsync()
    {
        var prior = CurrentTask;
        var generation = ++_generation;
        _operation?.Cancel();
        return CurrentTask = PreviewAfterAsync(prior, generation);
    }
    private async Task PreviewAfterAsync(Task prior, long generation)
    {
        try { await prior; } catch (OperationCanceledException) { }
        if (_disposed || generation != _generation) return;
        var compiled = Editor.Validate();
        if (!compiled.Success || _source is null) return;
        var source = _source; var editorGeneration = Editor.Generation;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _operation = cancellation; Busy = true;
        try
        {
            var text = await Task.Run(() => _engine.PreviewAsync(source, compiled.Program!, cancellation.Token), cancellation.Token);
            if (!_disposed && generation == _generation && editorGeneration == Editor.Generation && source.InstanceId == _source?.InstanceId) PreviewText = text;
        }
        catch (Exception ex) when (ex is OperationCanceledException or AnalysisLimitException or IOException)
        { if (!_disposed && generation == _generation) Status = "预览未完成：" + ex.GetType().Name; }
        finally { if (ReferenceEquals(_operation, cancellation)) { _operation = null; Busy = false; } }
    }
    private AnalysisOptions Options(DslProgram program)
    {
        AnalysisWindow? window = null;
        if (!string.IsNullOrWhiteSpace(WindowText))
        {
            var text = $"dsl 1; view \"窗口\" {{ where true; within {WindowText}; group by original.host; timeline by startedAt; }}";
            var compile = DslCompiler.Compile(text); if (!compile.Success) throw new ArgumentException("窗口请输入 last(30m) 或含时区的 range(\"开始\",\"结束\")。");
            window = compile.Program!.Window;
        }
        var sources = SourcesText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var columns = string.IsNullOrWhiteSpace(ColumnsText) ? null : ColumnsText.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return new(window, sources, IncludeContext, SortByCount, columns);
    }
    public async Task ReloadAsync()
    {
        var generation = ++_libraryGeneration;
        try
        {
            var library = await Task.Run(() => _store.LoadAsync(_closing.Token), _closing.Token);
            if (_disposed || generation != _libraryGeneration) return; _library = library; FillViews(); Status = "规则库已加载；选择视图后载入草稿，再手动应用。";
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { if (!_disposed) Status = "规则库未加载，保留当前内存内容：" + ex.GetType().Name; }
    }
    public async Task SaveAsync()
    {
        var compiled = Editor.Validate(); if (!compiled.Success) return;
        var editorGeneration = Editor.Generation; var generation = ++_libraryGeneration;
        try
        {
            var revision = RuleRevision.Create(Editor.Text, _draftRevision?.RevisionId);
            var view = new SavedView(_editingView ?? Guid.NewGuid(), ViewName, revision.RevisionId, Options(compiled.Program!));
            var expected = _library.Generation;
            var candidate = _library.Save(revision, view);
            var saved = await Task.Run(() => _store.CommitAsync(candidate, expected, _closing.Token), _closing.Token);
            if (_disposed || generation != _libraryGeneration) return;
            _library = saved; FillViews();
            if (editorGeneration == Editor.Generation) { _editingView = view.ViewId; _draftRevision = revision; }
            Status = $"视图已保存，库代次 {saved.Generation}；不会替换任何页面的已应用修订。";
        }
        catch (Exception ex) when (ex is IOException or RuleLibraryConflictException or ArgumentException or OperationCanceledException)
        { if (!_disposed) Status = $"未保存：{ex.Message}"; }
    }
    private void FillViews() { SavedViews.Clear(); foreach (var view in _library.Views) SavedViews.Add(view); ObserveLibraryGeneration(); }
    private async Task DeleteSelectedViewAsync()
    {
        if (SelectedView is not { } view) return;
        var expected = _library.Generation;
        var generation = ++_libraryGeneration;
        var candidate = _library with { Views = _library.Views.Where(v => v.ViewId != view.ViewId).ToArray() };
        try
        {
            var saved = await Task.Run(() => _store.CommitAsync(candidate, expected, _closing.Token), _closing.Token);
            if (_disposed || generation != _libraryGeneration) return;
            _library = saved; FillViews();
            if (_editingView == view.ViewId) _editingView = null;
            Status = "已删除选中的保存视图；既有修订和本页已应用结果仍可读。";
        }
        catch (Exception ex) when (ex is IOException or RuleLibraryConflictException or OperationCanceledException)
        { if (!_disposed) Status = "删除未完成：" + ex.Message; }
    }
    private void LoadSelectedView()
    {
        if (SelectedView is not { } view) return;
        var revision = _library.Revisions.Single(r => r.RevisionId == view.RuleRevisionId);
        _draftRevision = revision;
        Editor.Text = revision.SourceText; _editingView = view.ViewId; ViewName = view.Name;
        var window = view.Options.Window;
        WindowText = window?.Last is { } last ? $"last({(int)last.TotalSeconds}s)" : window is not null ? $"range(\"{window.From:O}\",\"{window.To:O}\")" : "";
        SourcesText = string.Join(',', view.Options.Sources ?? ["capture"]); ColumnsText = string.Join(',', view.Options.Columns ?? []);
        IncludeContext = view.Options.IncludeContext; SortByCount = view.Options.SortByCount;
        Status = "视图已载入草稿；点击应用后才分析。";
    }
    partial void OnSelectedGroupChanged(TrafficGroup? value) { if (value is not null) ShowRows(value.Requests, value.Description); }
    private void ShowSpecialRows(IEnumerable<AnalyzedRequest> rows, string title)
    {
        // 特殊范围没有对应的正常组。清除旧选择，使再次点击原组也能触发选择变化。
        SelectedGroup = null;
        ShowRows(rows, title);
    }
    private void ShowRows(IEnumerable<AnalyzedRequest> rows, string label)
    {
        PaneTitle = label;
        Requests.Clear(); foreach (var row in rows) Requests.Add(row);
        // 共同时间轴只在组切换时计算一次；虚拟行绘制不得逐行重复扫描一万条请求。
        TimelineStart = Requests.Count == 0 ? DateTimeOffset.UnixEpoch : Requests.Min(r => r.StartedUtc);
        TimelineMilliseconds = Requests.Count == 0 ? 1 : Math.Max(1, Requests.Max(r => (r.StartedUtc - TimelineStart).TotalMilliseconds +
            (double.IsFinite(r.ElapsedMilliseconds) ? Math.Max(0, r.ElapsedMilliseconds) : 0)));
        SelectedRequest = Requests.FirstOrDefault(); ShowTimeline = true; SelectionText = label; RefreshSelection();
    }
    partial void OnSelectedRequestChanged(AnalyzedRequest? value) => RefreshSelection();
    partial void OnRevealValuesChanged(bool value) => RefreshSelection();
    private void RefreshSelection()
    {
        if (SelectedRequest is not { } row) return;
        SelectionText = row.Description + $"\n已观察耗时：{(double.IsFinite(row.ElapsedMilliseconds) && row.ElapsedMilliseconds >= 0 ? row.ElapsedMilliseconds.ToString("N1") + " ms" : "未知")}" +
            (row.Transport == Application.Proxy.TransportState.Receiving ? "（快照时仍接收）" : "") +
            (row.TimingWarning.Length == 0 ? "" : "\n" + row.TimingWarning) +
            (row.Unresolved is null ? "" : "\n未确定：" + row.Unresolved) + "\n" +
            string.Join("\n", row.Fields.Select(f => $"{f.Key}: {f.Value.Display(RevealValues)} [{f.Value.Type.Kind}/{f.Value.State}] · " +
                string.Join(", ", (f.Value.Origins ?? []).Select(o => $"{o.Location}[{o.Path}]"))));
    }
    private void ClearResult() { Groups.Clear(); Requests.Clear(); SelectedGroup = null; SelectedRequest = null; _result?.Dispose(); _result = null; SelectionText = ""; ResultDescription = ""; PaneTitle = "选择一个业务组"; }
    private void Clear() { _generation++; _operation?.Cancel(); ClearResult(); RevealValues = false; PreviewText = ""; Status = "已清除本页分析结果，抓包继续。"; }
    public void Dispose() { if (_disposed) return; _disposed = true; _generation++; Editor.Dispose(); _closing.Cancel(); _operation?.Cancel(); ClearResult(); OpenRequest = null; }
}
