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
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _detailCancellation, _queryCancellation, _saveCancellation;
    private ICaptureSession? _opened, _observed;
    private TrafficRecord? _record;
    private ImagePreview? _image;
    private IDisposable? _previewReservation;
    private long _selectionRevision, _queryRevision, _afterSequence, _detailOperation;
    private bool _disposed, _refreshing, _detailBusy, _updatingRows;
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
    [ObservableProperty] private bool _onlyReceiving, _onlyFailed, _follow = true, _revealSensitive;
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
    public string FollowLabel => Follow ? "暂停跟随" : "继续跟随";
    // 窄屏详情让出筛选栏的空间；返回列表即恢复筛选，启停始终由外层常驻工具栏提供。
    public bool ShowFilters => !IsCompact || !ShowDetails;
    public CaptureWorkspace(CaptureSessionStore store, ICaptureArchive archive, BoundedImageDecoder images)
    {
        (_store, _archive, _images) = (store, archive, images);
        ClearFilterCommand = new RelayCommand(() => { Keyword = ""; MethodFilter = StatusCodeFilter = ""; MethodIndex = StatusIndex = 0; OnlyReceiving = OnlyFailed = false; FromText = ToText = ""; _afterSequence = 0; QueryChanged(); });
        ClearViewCommand = new RelayCommand(() => { _afterSequence = Session?.Snapshot().Records.Select(r => r.Sequence).DefaultIfEmpty().Max() ?? 0; QueryChanged(); });
        ToggleFollowCommand = new RelayCommand(() => Follow = !Follow);
        BackCommand = new RelayCommand(() => ShowDetails = false);
        LiveCommand = new RelayCommand(() => { _opened = null; IsReadOnly = false; _observed = null; QueryChanged(); Selected = null; });
        CancelSaveCommand = new RelayCommand(() => _saveCancellation?.Cancel());
        SaveCommand = new AsyncRelayCommand(SaveAsync);
        OpenCommand = new AsyncRelayCommand(() => OpenAsync(false));
        RecoverCommand = new AsyncRelayCommand(() => OpenAsync(true));
        ExportCommand = new AsyncRelayCommand(ExportAsync);
    }
    partial void OnKeywordChanged(string value) => QueryChanged();
    partial void OnMethodIndexChanged(int value) => QueryChanged();
    partial void OnStatusIndexChanged(int value) => QueryChanged();
    partial void OnOnlyReceivingChanged(bool value) => QueryChanged();
    partial void OnOnlyFailedChanged(bool value) => QueryChanged();
    partial void OnFromTextChanged(string value) => QueryChanged();
    partial void OnToTextChanged(string value) => QueryChanged();
    partial void OnMethodFilterChanged(string value) => QueryChanged();
    partial void OnStatusCodeFilterChanged(string value) => QueryChanged();
    partial void OnFollowChanged(bool value) => OnPropertyChanged(nameof(FollowLabel));
    partial void OnSelectedChanged(TrafficSummary? value)
    {
        if (_updatingRows) return;
        _renderedBody = null;
        _selectionRevision++; _detailCancellation?.Cancel(); _record = null; ClearPreview();
        if (value is not null) { ShowDetails = true; DetailTask = LoadDetailsAsync(value.RecordId, _selectionRevision); }
    }
    partial void OnRequestSelectedChanged(bool value)
    {
        _renderedBody = null;
        _selectionRevision++; _detailCancellation?.Cancel(); ClearPreview();
        if (Selected is not null) DetailTask = LoadDetailsAsync(Selected.RecordId, _selectionRevision);
    }
    partial void OnRevealSensitiveChanged(bool value) { if (_record is not null) RenderFields(_record); }
    private void QueryChanged()
    {
        _queryRevision++; _queryChangedUtc = DateTimeOffset.UtcNow;
        _queryCancellation?.Cancel(); Rows.Clear(); _rows.Clear();
    }
    public async Task RefreshAsync()
    {
        if (_disposed || _refreshing || DateTimeOffset.UtcNow - _queryChangedUtc < TimeSpan.FromMilliseconds(200)) return;
        var session = Session;
        if (session is null) return;
        _refreshing = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);
        _queryCancellation = cancellation;
        var revision = _queryRevision;
        try
        {
            if (!ReferenceEquals(_observed, session)) { _observed = session; Rows.Clear(); _rows.Clear(); Selected = null; _afterSequence = 0; }
            DateTimeOffset? ParseTime(string value) => string.IsNullOrWhiteSpace(value) ? null : DateTimeOffset.TryParse(value, out var result) ? result : throw new ArgumentException("时间格式无效，请使用日期时间或留空。");
            var methods = string.IsNullOrWhiteSpace(MethodFilter) ? MethodIndex <= 0 ? null : new[] { MethodChoices[Math.Min(MethodIndex, MethodChoices.Length - 1)] } : MethodFilter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            int? statusCode = string.IsNullOrWhiteSpace(StatusCodeFilter) ? null : int.TryParse(StatusCodeFilter, out var parsedCode) ? parsedCode : throw new ArgumentException("状态码应为 100～599，或留空。");
            var query = new CaptureQuery(Keyword, methods,
                StatusIndex == 0 ? null : StatusIndex, OnlyFailed ? TransportState.Failed : OnlyReceiving ? TransportState.Receiving : null,
                ParseTime(FromText), ParseTime(ToText), _afterSequence, StatusCode: statusCode);
            var target = await session.QueryAsync(query, cancellation.Token);
            if (_disposed || revision != _queryRevision || !ReferenceEquals(session, Session)) return;
            ApplyRows(target);
            var snapshot = session.Snapshot();
            Status = $"{(session.IsArchive ? "只读快照" : snapshot.IsRunning ? "实时捕获" : "已停止")} · 匹配 {target.Length:N0} / 保留 {snapshot.Records.Length:N0} · 累计 {snapshot.Total:N0} · 淘汰 {snapshot.Evicted:N0}";
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
        { Message = "选中记录已不在当前范围。"; Selected = null; _selectionRevision++; _detailCancellation?.Cancel(); _record = null; _renderedBody = null; ClearPreview(); }
    }
    public async Task LoadDetailsAsync(Guid id, long revision)
    {
        var session = Session; if (session is null || _disposed) return;
        _detailCancellation?.Cancel();
        var operation = ++_detailOperation;
        bool IsCurrent() => Current(id, revision) && operation == _detailOperation;
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
            memory = _store.ContentBudget.TryReserve(maximum * 2L + (2 << 20));
            if (memory is null) { BodyText = "内容内存预算已满，请稍后再试。"; return; }
            var bytes = await session.ReadBodyAsync(body, maximum, cancellation.Token);
            var preview = await Task.Run(() => ContentPreviewReader.Parse(body, bytes, bytes.LongLength < body.CommittedBytes), cancellation.Token);
            if (preview.ImageBytes is not null) image = await _images.DecodeAsync(preview.ImageBytes, cancellation.Token);
            if (!IsCurrent()) return;
            BodyText = preview.Text; _previewDiagnostic = preview.Diagnostic ?? ""; BodyStatus += "\n" + _previewDiagnostic;
            HasJson = preview.JsonTree is not null;
            if (preview.JsonTree is not null) foreach (var node in JsonPreviewNode.Parse(preview.JsonTree)) JsonNodes.Add(node);
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
        CookiesText = RevealSensitive ? string.Join(Environment.NewLine, HttpFieldParser.Cookies(record.RequestHeaders.Concat(record.ResponseHeaders)).Select(p => p.Raw)) : "Cookie 值默认遮罩；勾选“显示原值”后可查看或复制。";
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
    }
    private async Task SaveAsync()
    {
        var session = Session; if (session is null) { Message = "没有可保存的捕获。"; return; }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _saveCancellation = cancellation; Saving = true;
        try { var result = await _archive.SaveAsync(session, ArchivePath, cancellation.Token); if (!_disposed) Message = $"已保存 {result.Records} 条记录、{result.BodyBytes:N0} 字节；存档包含已捕获的真实请求内容。"; }
        catch (OperationCanceledException) { if (!_disposed) Message = "保存已取消，未发布半成品。"; }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException) { if (!_disposed) Message = $"保存未完成：{ex.Message}"; }
        finally { if (ReferenceEquals(_saveCancellation, cancellation)) { _saveCancellation = null; Saving = false; } }
    }
    private async Task OpenAsync(bool recover)
    {
        try
        {
            var opened = recover ? await _archive.RecoverAsync(ArchivePath, _closing.Token) : await _archive.OpenAsync(ArchivePath, _closing.Token);
            if (_disposed) return; _opened = opened; IsReadOnly = true; Selected = null; _observed = null; QueryChanged();
            Message = "已打开只读记录；不会启动代理、重发或补下载。";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException or JsonException) { if (!_disposed) Message = $"打开失败：{ex.Message}"; }
    }
    private async Task ExportAsync()
    {
        if (_record is null) return;
        var body = RequestSelected ? _record.RequestBody : _record.ResponseBody;
        try { using var lease = Session?.AcquireReadLease(); await _archive.ExportBodyAsync(body, ExportPath, _closing.Token); if (!_disposed) Message = $"已导出已保存的 {body.CommittedBytes:N0} 字节（{body.Capture}），没有补下载。"; }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException) { if (!_disposed) Message = $"导出失败：{ex.Message}"; }
    }
    private void ClearPreview()
    {
        ImageSource = null; _image?.Dispose(); _image = null; _previewReservation?.Dispose(); _previewReservation = null;
        JsonNodes.Clear(); Events.Clear(); HasImage = HasJson = HasStream = false; BodyText = "";
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _selectionRevision++; _closing.Cancel(); _detailCancellation?.Cancel(); _queryCancellation?.Cancel();
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
