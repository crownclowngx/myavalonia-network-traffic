using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Content;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// 每 Document 一份页面投影。共享服务管理捕获；本类只管理筛选、选择、读取取消与显示租约。
/// 异步结果在 await 之后再次核对选择代次，避免取消无法撤回已排队 UI 委托造成串页。
/// </summary>
public sealed partial class CaptureWorkspace : ObservableObject, IDisposable
{
    private readonly CaptureSessionStore _store;
    private readonly ICaptureArchive _archive;
    private readonly BoundedImageDecoder _images;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _detailCancellation, _queryCancellation, _saveCancellation;
    private ICaptureSession? _opened, _observed;
    private TrafficRecord? _record;
    private ImagePreview? _image;
    private IDisposable? _previewReservation;
    private long _selectionRevision, _queryRevision, _afterSequence, _detailOperation;
    private bool _disposed, _refreshing, _detailBusy, _updatingRows, _syncingFilter;
    private BodyDescriptor? _renderedBody;
    private string _previewDiagnostic = "";
    private DateTimeOffset _queryChangedUtc;
    private readonly Dictionary<Guid, TrafficSummary> _rows = [];
    public ObservableCollection<TrafficSummary> Rows { get; } = [];
    public ObservableCollection<JsonPreviewNode> JsonNodes { get; } = [];
    public ObservableCollection<StreamEvent> Events { get; } = [];
    public string[] MethodChoices { get; } = ["全部方法", "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public string[] StatusChoices { get; } = ["全部状态", "1xx", "2xx", "3xx", "4xx", "5xx"];
    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private int _methodIndex, _statusIndex;
    [ObservableProperty] private bool _onlyReceiving, _onlyFailed, _revealSensitive;
    [ObservableProperty] private TrafficSummary? _selected;
    [ObservableProperty] private bool _requestSelected;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowFilters))] private bool _isCompact, _showDetails;
    [ObservableProperty] private string _status = "手动启动代理后，请求会出现在这里。";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _overview = "选择一条请求查看详情。";
    [ObservableProperty] private string _queryText = "", _requestHeadersText = "", _responseHeadersText = "", _cookiesText = "";
    [ObservableProperty] private string _bodyText = "", _bodyStatus = "", _streamStatus = "";
    [ObservableProperty] private Bitmap? _imageSource;
    [ObservableProperty] private bool _hasImage, _hasJson, _hasStream, _saving, _isReadOnly;
    [ObservableProperty] private string _archivePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "NetworkTraffic", "capture-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
    [ObservableProperty] private string _exportPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "body-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".bin");
    [ObservableProperty] private string _fromText = "", _toText = "";
    [ObservableProperty] private string _methodFilter = "", _statusCodeFilter = "";
    public IRelayCommand ClearFilterCommand { get; }
    public IRelayCommand ClearViewCommand { get; }
    public IRelayCommand ToggleFollowCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand LiveCommand { get; }
    public IRelayCommand CancelSaveCommand { get; }
    public IAsyncRelayCommand SaveCommand { get; }
    public IAsyncRelayCommand OpenCommand { get; }
    public IAsyncRelayCommand RecoverCommand { get; }
    public IAsyncRelayCommand ExportCommand { get; }
    public ICaptureSession? Session => _opened ?? _store.Current;
    public Task DetailTask { get; private set; } = Task.CompletedTask;
    public ReadingFollowState ListReading { get; } = new();
    public ReadingFollowState EventReading { get; } = new();
    public BodyTextPresentation Body { get; } = new();
    public OperationFeedback Feedback { get; } = new();
    // 保留既有调用入口，但只代表列表跟随；事件现在使用独立状态。
    public bool Follow { get => ListReading.Following; set { if (value) ListReading.Resume(LatestSequence); else ListReading.Pause(LatestSequence); } }
    public string FollowLabel => Follow ? "暂停跟随" : $"新增可见 {ListReading.Pending} 条 · 回到最新";
    // 窄屏详情让出筛选栏的空间；返回列表即恢复筛选，启停始终由外层常驻工具栏提供。
    public bool ShowFilters => !IsCompact || !ShowDetails;
    public CaptureWorkspace(CaptureSessionStore store, ICaptureArchive archive, BoundedImageDecoder images, TimeProvider? clock = null)
    {
        (_store, _archive, _images) = (store, archive, images);
        _clock = clock ?? TimeProvider.System;
        ClearFilterCommand = new RelayCommand(() => { Keyword = ""; MethodFilter = StatusCodeFilter = ""; MethodIndex = StatusIndex = 0; TransportIndex = 0; FromText = ToText = ""; QueryChanged(); });
        ClearViewCommand = new RelayCommand(() => { _afterSequence = Session?.Snapshot().Records.Select(r => r.Sequence).DefaultIfEmpty().Max() ?? 0; QueryChanged(); });
        ToggleFollowCommand = new RelayCommand(() => Follow = !Follow);
        BackCommand = new RelayCommand(() => ShowDetails = false);
        LiveCommand = new RelayCommand(() => { _openRevision++; Feedback.Dismiss(); _opened = null; IsReadOnly = false; _observed = null; QueryChanged(); Selected = null; OnPropertyChanged(nameof(IsLive)); });
        CancelSaveCommand = new RelayCommand(() => _saveCancellation?.Cancel());
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        OpenCommand = new AsyncRelayCommand(() => OpenAsync(false));
        RecoverCommand = new AsyncRelayCommand(() => OpenAsync(true));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
        InitializeInteraction();
    }
    partial void OnKeywordChanged(string value) => QueryChanged();
    partial void OnMethodIndexChanged(int value)
    {
        if (!_syncingFilter) MethodFilter = value > 0 && value < MethodChoices.Length ? MethodChoices[value] : "";
        QueryChanged();
    }
    partial void OnStatusIndexChanged(int value) { if (value > 0) StatusCodeFilter = ""; QueryChanged(); }
    partial void OnOnlyReceivingChanged(bool value) { if (!_syncingFilter) TransportIndex = value ? 1 : OnlyFailed ? 2 : 0; }
    partial void OnOnlyFailedChanged(bool value) { if (!_syncingFilter) TransportIndex = value ? 2 : OnlyReceiving ? 1 : 0; }
    partial void OnFromTextChanged(string value) => QueryChanged();
    partial void OnToTextChanged(string value) => QueryChanged();
    partial void OnMethodFilterChanged(string value)
    {
        _syncingFilter = true;
        try { MethodIndex = Math.Max(0, Array.FindIndex(MethodChoices, item => item.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase))); }
        finally { _syncingFilter = false; }
        QueryChanged();
    }
    partial void OnStatusCodeFilterChanged(string value) { if (!string.IsNullOrWhiteSpace(value)) StatusIndex = 0; QueryChanged(); }
    partial void OnSelectedChanged(TrafficSummary? value)
    {
        if (_updatingRows) return;
        if (value is not null && value.Sequence < LatestSequence) ListReading.Pause(LatestSequence);
        EventReading.Resume(0); EventReading.Reset();
        _renderedBody = null;
        _selectionRevision++; _detailCancellation?.Cancel(); _record = null; ClearPreview(); BodyStatus = StreamStatus = "";
        NotifySelection();
        if (value is not null) { ShowDetails = true; DetailTask = LoadDetailsAsync(value.RecordId, _selectionRevision); }
        else { Overview = "选择一条请求查看详情。"; QueryText = RequestHeadersText = ResponseHeadersText = CookiesText = ""; BodyStatus = ""; }
    }
    partial void OnRequestSelectedChanged(bool value)
    {
        _renderedBody = null;
        EventReading.Resume(0); EventReading.Reset();
        _selectionRevision++; _detailCancellation?.Cancel(); ClearPreview();
        if (Selected is not null) DetailTask = LoadDetailsAsync(Selected.RecordId, _selectionRevision);
        OnPropertyChanged(nameof(DirectionIndex)); OnPropertyChanged(nameof(HeadersText));
    }
    partial void OnRevealSensitiveChanged(bool value) { if (_record is not null) RenderFields(_record); }
    private void QueryChanged()
    {
        _queryRevision++; _queryChangedUtc = _clock.GetUtcNow();
        // 输入中的草稿不能先清空列表。等新查询成功后按身份应用，既防闪烁又保留旧选择。
        _queryCancellation?.Cancel(); QueryPending = true; ListReading.Reset(LatestSequence);
    }
    public async Task RefreshAsync()
    {
        if (_disposed) return;
        Feedback.Tick(_clock.GetUtcNow());
        if (_refreshing || IsComposing || _clock.GetUtcNow() - _queryChangedUtc < TimeSpan.FromMilliseconds(200)) return;
        var session = Session;
        if (session is null) { QueryPending = false; return; }
        _refreshing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _queryCancellation = cancellation;
        var revision = _queryRevision;
        try
        {
            if (!ReferenceEquals(_observed, session))
            {
                _observed = session; Rows.Clear(); _rows.Clear(); Selected = null; _afterSequence = 0;
                RevealSensitive = false; RequestSelected = false; ListReading.Resume(0); EventReading.Resume(0);
                SourceRevision++; OnPropertyChanged(nameof(IsLive));
            }
            CaptureQuery query;
            try { query = new CaptureFilterInput(Keyword, MethodFilter, StatusIndex, StatusCodeFilter, TransportIndex, FromText, ToText, _afterSequence).Build(); }
            catch (ArgumentException ex) { FilterError = ex.Message + " 尚未应用，仍显示上次结果。"; QueryPending = false; return; }
            FilterError = "";
            var target = await session.QueryAsync(query, cancellation.Token);
            if (_disposed || revision != _queryRevision || !ReferenceEquals(session, Session)) return;
            ApplyRows(target);
            QueryPending = Rows.Count != target.Length || target.Any(row => !_rows.TryGetValue(row.RecordId, out var applied) || applied != row);
            if (!QueryPending)
            {
                // 换筛选重建当前结果水位。扩大范围后出现的历史请求不能冒充暂停期间新到达的记录。
                if (AppliedQueryRevision != revision) ListReading.Reset(target.Select(row => row.Sequence).DefaultIfEmpty().Max());
                AppliedQueryRevision = revision; UpdateChips(query);
            }
            ListReading.Observe(target.Select(row => row.Sequence)); NotifySelection();
            var snapshot = session.Snapshot();
            Status = $"{(session.IsArchive ? "只读快照" : snapshot.IsRunning ? "实时捕获" : "已停止")} · 匹配 {target.Length:N0} / 保留 {snapshot.Records.Length:N0} · 累计 {snapshot.Total:N0} · 淘汰 {snapshot.Evicted:N0}";
            EmptyText = target.Length == 0 ? snapshot.Records.Length > 0 ? "没有匹配请求。可清除筛选或恢复本页范围。" :
                session.IsArchive ? "这个只读存档没有记录。" : snapshot.IsRunning ? "等待请求。请配置客户端代理，或打开接入诊断。" : "尚无记录。请先启动代理并接入客户端。" : "";
            OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(IsLive));
            if (snapshot.Diagnostic is not null) Message = snapshot.Diagnostic;
            if (Selected is not null && !_detailBusy) await (DetailTask = LoadDetailsAsync(Selected.RecordId, _selectionRevision));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or ArgumentException or InvalidOperationException) { if (!_disposed) Message = $"读取捕获失败：{ex.Message}"; }
        finally { if (ReferenceEquals(_queryCancellation, cancellation)) _queryCancellation = null; _refreshing = false; }
    }
    private void ApplyRows(TrafficSummary[] target)
    {
        var selectedId = Selected?.RecordId;
        _updatingRows = true;
        try
        {
        var identities = target.Select(r => r.RecordId).ToHashSet(); var changed = 0;
        foreach (var old in Rows.Where(r => !identities.Contains(r.RecordId)).Take(200).ToArray())
        { Rows.Remove(old); _rows.Remove(old.RecordId); changed++; }
        // 先按目标顺序接纳有限新行；下一次时钟继续补齐，虚拟列表不会一次创建一万控件。
        foreach (var row in target)
        {
            if (changed >= 200) break;
            if (!_rows.TryGetValue(row.RecordId, out var old))
            {
                var index = 0; while (index < Rows.Count && Rows[index].Sequence > row.Sequence) index++;
                Rows.Insert(index, row); _rows[row.RecordId] = row; changed++;
            }
            else if (old != row)
            {
                var index = Rows.IndexOf(old);
                if (index >= 0) { Rows[index] = row; _rows[row.RecordId] = row; changed++; }
            }
        }
        // 替换不可变摘要时 ListBox 可能暂时清除 SelectedItem；按身份恢复，不能按旧索引误选另一条。
        if (selectedId is { } id && _rows.TryGetValue(id, out var updated)) Selected = updated;
        }
        finally { _updatingRows = false; }
        if (selectedId is { } removed && !_rows.ContainsKey(removed))
        { Message = "选中记录已不在当前范围。"; if (Selected is null) OnSelectedChanged(null); else Selected = null; }
    }
    public async Task LoadDetailsAsync(Guid id, long revision)
    {
        var session = Session; if (session is null || _disposed) return;
        _detailCancellation?.Cancel();
        var operation = ++_detailOperation;
        bool IsCurrent() => Current(id, revision) && operation == _detailOperation && ReferenceEquals(session, Session);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _detailCancellation = cancellation; _detailBusy = true;
        IDisposable? memory = null; ImagePreview? image = null;
        try
        {
            var record = await session.ReadAsync(id, cancellation.Token);
            if (record is null || !IsCurrent()) return;
            _record = record; RenderFields(record);
            var body = RequestSelected ? record.RequestBody : record.ResponseBody;
            BodyStatus = $"{body.Transport} / {body.Capture} · 已观察 {body.ObservedBytes:N0} B · 已保存 {body.CommittedBytes:N0} B · 总长 {(body.ExpectedBytes?.ToString("N0") ?? "未知")}\n{body.Reason}";
            if (body == _renderedBody) BodyStatus += "\n" + _previewDiagnostic;
            var stream = !RequestSelected ? session.Stream(id) : null;
            if (stream is not null) { RenderStream(stream); return; }
            if (body == _renderedBody) return;
            // 每次切换先归还旧大对象；完成后只转移给仍有效的选择。文件读取/解析在后台进行。
            ClearPreview();
            var maximum = body.ContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? 4 << 20 : 1 << 20;
            // 输入、解码/格式化字符串以及有界 JSON 树共用一份保守预留；不按显示模式各建缓存。
            memory = _store.ContentBudget.TryReserve(maximum * 2L + (6 << 20));
            if (memory is null) { BodyText = "内容内存预算已满，请稍后再试。"; return; }
            var bytes = await session.ReadBodyAsync(body, maximum, cancellation.Token);
            var preview = await Task.Run(() => ContentPreviewReader.Parse(body, bytes, bytes.LongLength < body.CommittedBytes), cancellation.Token);
            if (preview.ImageBytes is not null) image = await _images.DecodeAsync(preview.ImageBytes, cancellation.Token);
            if (!IsCurrent()) return;
            BodyText = preview.Text; _previewDiagnostic = preview.Diagnostic ?? ""; BodyStatus += "\n" + _previewDiagnostic;
            Body.Raw = preview.Text; Body.Formatted = preview.JsonTree ?? "";
            HasJson = preview.JsonTree is not null;
            UpdateJsonTree();
            if (preview.Stream is not null) RenderStream(preview.Stream);
            if (image is not null) { _image = image; ImageSource = image.Bitmap; HasImage = true; image = null; }
            _previewReservation = memory; memory = null;
            _renderedBody = body;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or NotSupportedException)
        { if (IsCurrent()) BodyText = $"预览不可用（{ex.GetType().Name}）；已有记录和转发状态不变。"; }
        finally
        {
            memory?.Dispose(); image?.Dispose();
            if (ReferenceEquals(_detailCancellation, cancellation)) { _detailCancellation = null; _detailBusy = false; }
        }
    }
    private bool Current(Guid id, long revision) => !_disposed && revision == _selectionRevision && Selected?.RecordId == id;
    private void RenderFields(TrafficRecord record)
    {
        string Target(string uri) => RevealSensitive ? uri : uri.Split('?')[0];
        Overview = $"{record.Method}  {record.StatusCode?.ToString() ?? "无响应状态"}  {record.ElapsedMilliseconds:N0} ms\n原始：{Target(record.OriginalUri)}\n实际：{Target(record.EffectiveUri)}\n客户端 HTTP/{record.ClientProtocol} → 上游 HTTP/{record.OriginProtocol ?? "未知"}\n来源 {record.Source} · {record.RecordId}\n{(record.MetadataPartial ? "元信息记录不完整。" : "")}{record.Failure?.Message}";
        QueryText = string.Join(Environment.NewLine, HttpFieldParser.Query(record.OriginalUri).Select(p => $"{p.Name} = {(RevealSensitive || !HttpFieldParser.Sensitive(p.Name) ? p.Value ?? "（无等号）" : "••••")} {p.Diagnostic}"));
        RequestHeadersText = HttpFieldParser.Display(record.RequestHeaders, RevealSensitive);
        ResponseHeadersText = HttpFieldParser.Display(record.ResponseHeaders, RevealSensitive);
        CookiesText = RevealSensitive ? "请求 Cookie\n" + string.Join(Environment.NewLine, HttpFieldParser.Cookies(record.RequestHeaders).Select(p => p.Raw)) +
            "\n\n响应 Set-Cookie\n" + string.Join(Environment.NewLine, HttpFieldParser.Cookies(record.ResponseHeaders).Select(p => p.Raw)) : "请求 Cookie / 响应 Set-Cookie 默认遮罩；显示敏感字段后可查看或复制。";
        OnPropertyChanged(nameof(HeadersText)); OnPropertyChanged(nameof(DisplayAddress)); NotifySelection();
    }
    private void RenderStream(StreamPreview stream)
    {
        HasStream = true;
        // 暂停跟随不停止解析；列表更新依然有界，View 只暂停滚动动作。
        var first = stream.Events.FirstOrDefault()?.Sequence ?? long.MaxValue;
        while (Events.Count > 0 && Events[0].Sequence < first) Events.RemoveAt(0);
        var last = Events.LastOrDefault()?.Sequence ?? 0;
        foreach (var item in stream.Events) if (item.Sequence > last) Events.Add(item);
        StreamStatus = $"累计 {stream.Total} · 保留 {stream.Events.Length} · 淘汰 {stream.Evicted} · 无效 {stream.Invalid}" +
            (stream.Incomplete ? " · 有未完成片段" : "") + (stream.Gap ? " · 预览有缺口，连续解析已停止" : "");
        BodyText = stream.Incomplete ? stream.Fragment ?? "未完成片段超限。" : "";
        EventReading.Observe(stream.Events.Select(item => item.Sequence));
        OnPropertyChanged(nameof(EventFollowLabel)); OnPropertyChanged(nameof(HasFragment));
    }
    private async Task SaveAsync()
    {
        var feedback = Feedback.Begin();
        var session = Session; if (session is null) { Feedback.Complete(feedback, "没有可保存的捕获。", true); return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _saveCancellation = cancellation; Saving = true;
        Feedback.Complete(feedback, "正在保存会话快照…", true);
        try { var result = await _archive.SaveAsync(session, ArchivePath, cancellation.Token); if (!_disposed) Feedback.Complete(feedback, $"已保存 {result.Records} 条记录、{result.BodyBytes:N0} 字节；包含真实记录内容。"); }
        catch (OperationCanceledException) { if (!_disposed) Feedback.Complete(feedback, "保存已取消，未发布半成品。"); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException) { if (!_disposed) Feedback.Complete(feedback, $"保存未完成：{ex.Message}", true); }
        finally { if (ReferenceEquals(_saveCancellation, cancellation)) { _saveCancellation = null; if (!_disposed) Saving = false; } }
    }
    private async Task OpenAsync(bool recover)
    {
        var feedback = Feedback.Begin(); var source = Session; var generation = ++_openRevision;
        try
        {
            var opened = recover ? await _archive.RecoverAsync(ArchivePath, _closing.Token) : await _archive.OpenAsync(ArchivePath, _closing.Token);
            if (_disposed || generation != _openRevision || !ReferenceEquals(source, Session)) return;
            _opened = opened; IsReadOnly = true; Selected = null; _observed = null; QueryChanged(); OnPropertyChanged(nameof(IsLive));
            Feedback.Complete(feedback, "已打开只读记录；不会启动代理、重发或补下载。");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or JsonException) { if (!_disposed) Feedback.Complete(feedback, $"打开失败：{ex.Message}", true); }
    }
    private async Task ExportAsync()
    {
        if (_record is null) return;
        var feedback = Feedback.Begin();
        var body = RequestSelected ? _record.RequestBody : _record.ResponseBody;
        try { using var lease = Session?.AcquireReadLease(); await _archive.ExportBodyAsync(body, ExportPath, _closing.Token); if (!_disposed) Feedback.Complete(feedback, $"已导出已保存的 {body.CommittedBytes:N0} 字节（{body.Capture}），没有补下载。"); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException) { if (!_disposed) Feedback.Complete(feedback, $"导出失败：{ex.Message}", true); }
    }
    private void ClearPreview()
    {
        ImageSource = null; _image?.Dispose(); _image = null; _previewReservation?.Dispose(); _previewReservation = null;
        JsonNodes.Clear(); Events.Clear(); Body.Clear(); HasImage = HasJson = HasStream = false; BodyText = "";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _selectionRevision++; _closing.Cancel(); _detailCancellation?.Cancel(); _queryCancellation?.Cancel(); _saveCancellation?.Cancel();
        ClearPreview(); Rows.Clear(); _rows.Clear(); _opened = null; _closing.Dispose();
    }
}

public sealed record JsonPreviewNode(string Label, IReadOnlyList<JsonPreviewNode> Children)
{
    public static JsonPreviewNode[] Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        JsonPreviewNode Build(string name, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.Object => new(name + " { }", value.EnumerateObject().Select(p => Build(p.Name, p.Value)).ToArray()),
            JsonValueKind.Array => new(name + " [ ]", value.EnumerateArray().Select((v, i) => Build($"[{i}]", v)).ToArray()),
            _ => new(name + ": " + value.GetRawText(), [])
        };
        return [Build("$", doc.RootElement)];
    }
}
