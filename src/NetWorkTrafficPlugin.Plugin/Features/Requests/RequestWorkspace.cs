using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Features.Capture;

namespace NetWorkTrafficPlugin.Features.Requests;

/// <summary>
/// 页面只组织编辑与命令。协议校验、网络、库提交和对比均交给窄用例；异步提交前检查页面和编辑代次。
/// 编辑在途请求时只使下次预览失效，不改写已经接纳的字节，也不取消另一Document的请求。
/// </summary>
public sealed partial class RequestWorkspace : ObservableObject, IDisposable
{
    private readonly RequestPreparation _preparation;
    private readonly RequestExecutionCoordinator _execution;
    private readonly IRequestLibraryStore _libraryStore;
    private readonly IRequestFileImporter _files;
    private readonly ICaptureSession _session;
    private readonly IRequestHistoryStore? _history;
    private readonly RequestComparisonService _comparison;
    private readonly CancellationTokenSource _closing = new();
    private PreparedRequest? _prepared;
    private RequestAttempt? _attempt;
    private RequestLibrary _library = RequestLibrary.Empty;
    private Guid _draftId = Guid.NewGuid();
    private long _revision = 1, _operation;
    private Guid? _bodyAsset;
    private RequestOrigin? _origin;
    private TrafficRecord? _comparisonSource;
    private byte[] _comparisonBody = [];
    private byte[] _comparisonRequest = [];
    private IDisposable? _sourceReservation;
    private long _comparisonOperation;
    private RequestVariable[] _defaults = [];
    private bool _disposed;
    private bool _querySync;
    [ObservableProperty] private string _queryText = "";
    [ObservableProperty] private string _method = "GET", _address = "https://example.test/", _headersText = "", _bodyText = "", _variablesText = "";
    [ObservableProperty] private RequestBodyKind _bodyKind;
    [ObservableProperty] private string _mediaType = "application/octet-stream", _status = "编辑后先预览，再明确发送。当前规则不可用：M3尚未接通。";
    [ObservableProperty] private string _previewText = "尚未预览。", _templateName = "请求模板", _environmentName = "调试环境", _environmentText = "baseUrl=https://example.test";
    [ObservableProperty] private string _comparisonText = "选择一条来源记录后，可与本次结果进行有界对比。";
    [ObservableProperty] private RequestEnvironment? _selectedEnvironment;
    [ObservableProperty] private RequestTemplate? _selectedTemplate;
    [ObservableProperty] private bool _busy;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(VariablesMask))] private bool _revealVariables;
    public char VariablesMask => RevealVariables ? '\0' : '●';
    public string[] Methods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
    public RequestBodyKind[] BodyKinds { get; } = Enum.GetValues<RequestBodyKind>();
    public ObservableCollection<RequestTemplate> Templates { get; } = [];
    public ObservableCollection<RequestEnvironment> Environments { get; } = [];
    public ObservableCollection<RequestHistoryEntry> History { get; } = [];
    public CaptureWorkspace Results { get; }
    public IAsyncRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand SendCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearEnvironmentCommand { get; }
    public IAsyncRelayCommand ReloadCommand { get; }
    public IAsyncRelayCommand SaveTemplateCommand { get; }
    public IRelayCommand LoadTemplateCommand { get; }
    public IAsyncRelayCommand SaveEnvironmentCommand { get; }
    public IAsyncRelayCommand UpdateTemplateCommand { get; }
    public IAsyncRelayCommand DeleteTemplateCommand { get; }
    public IAsyncRelayCommand UpdateEnvironmentCommand { get; }
    public IAsyncRelayCommand DeleteEnvironmentCommand { get; }
    public IAsyncRelayCommand CompareCommand { get; }
    public RequestAttempt? CurrentAttempt => _attempt;

    public RequestWorkspace(RequestPreparation preparation, RequestExecutionCoordinator execution, IRequestLibraryStore library,
        IRequestFileImporter files, ICaptureSession session, CaptureWorkspace results, IRequestHistoryStore? history = null, RequestComparisonService? comparison = null)
    {
        (_preparation, _execution, _libraryStore, _files, _session, Results) = (preparation, execution, library, files, session, results);
        _history = history; _comparison = comparison ?? new(preparation.ContentBudget);
        PreviewCommand = new AsyncRelayCommand(() => RunAsync(PreviewAsync));
        SendCommand = new AsyncRelayCommand(() => RunAsync(SendAsync), () => !Busy && _prepared?.IsReady == true);
        CancelCommand = new RelayCommand(() => _attempt?.Cancel());
        // ComboBox选择非空环境后无法自然回到空项，显式清除仅修改本页选择并按既有代次规则撤销预览。
        ClearEnvironmentCommand = new RelayCommand(() => SelectedEnvironment = null);
        ReloadCommand = new AsyncRelayCommand(() => RunAsync(ReloadAsync));
        SaveTemplateCommand = new AsyncRelayCommand(() => RunAsync(SaveTemplateAsync));
        LoadTemplateCommand = new RelayCommand(() => { if (SelectedTemplate is { } value) Load(value.Draft); });
        SaveEnvironmentCommand = new AsyncRelayCommand(() => RunAsync(SaveEnvironmentAsync));
        UpdateTemplateCommand = new AsyncRelayCommand(() => RunAsync(t => ChangeTemplateAsync(false, t)));
        DeleteTemplateCommand = new AsyncRelayCommand(() => RunAsync(t => ChangeTemplateAsync(true, t)));
        UpdateEnvironmentCommand = new AsyncRelayCommand(() => RunAsync(t => ChangeEnvironmentAsync(false, t)));
        DeleteEnvironmentCommand = new AsyncRelayCommand(() => RunAsync(t => ChangeEnvironmentAsync(true, t)));
        CompareCommand = new AsyncRelayCommand(() => RunAsync(CompareAsync));
    }
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        // URL是Query唯一发送事实。同步不解码、不排序，保留重复键、空值和转义；禁用行仅存在编辑侧。
        if (!_querySync && e.PropertyName is nameof(Address) or nameof(QueryText))
        {
            _querySync = true;
            try
            {
                if (e.PropertyName == nameof(Address)) QueryText = RequestQueryEditor.FromAddress(Address);
                else Address = RequestQueryEditor.Apply(Address, QueryText);
            }
            finally { _querySync = false; }
        }
        if (e.PropertyName == nameof(SelectedEnvironment) && SelectedEnvironment is { } selected)
        { EnvironmentName = selected.Name; EnvironmentText = string.Join("\n", selected.Variables.Select(v => v.Name + "=" + v.Value)); }
        if (e.PropertyName == nameof(SelectedTemplate) && SelectedTemplate is { } template) TemplateName = template.Name;
        if (e.PropertyName is nameof(Method) or nameof(Address) or nameof(QueryText) or nameof(HeadersText) or nameof(BodyText) or nameof(VariablesText) or nameof(BodyKind) or nameof(MediaType) or nameof(SelectedEnvironment)) Invalidate();
    }
    private void Invalidate()
    {
        _revision++; _prepared?.Dispose(); _prepared = null;
        PreviewText = "输入已改变，请重新预览。"; SendCommand?.NotifyCanExecuteChanged();
    }
    public RequestDraft Snapshot() => new(_draftId, _revision, Method, Address, ParseFields(HeadersText, ':'), BodyKind, BodyText,
        BodyKind == RequestBodyKind.Form ? ParseFields(BodyText, '=') : null, BodyKind == RequestBodyKind.RawFile ? _bodyAsset : null,
        BodyKind is RequestBodyKind.RawFile or RequestBodyKind.Text ? MediaType : null, ParseVariables(VariablesText), _defaults, _origin, QueryEditorText: QueryText);
    private static RequestField[] ParseFields(string text, char separator)
    {
        if (text.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "编辑输入超过预算。");
        return text.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Select(line =>
        {
            var enabled = !line.StartsWith("# ", StringComparison.Ordinal);
            if (!enabled) line = line[2..];
            var split = line.IndexOf(separator);
            if (split < 1) throw new RequestFailure("RQ003", "每行请使用名称和分隔符，再填写值。");
            // Header允许名称与冒号周围的排版空白；Form中的空格属于实体数据，不能沿用Header的Trim规则。
            var name = line[..split]; var value = line[(split + 1)..].TrimEnd('\r');
            return new RequestField(separator == ':' ? name.Trim() : name, separator == ':' ? value.TrimStart(' ') : value, enabled);
        }).ToArray();
    }
    private static RequestVariable[] ParseVariables(string text) => ParseFields(text, '=').Where(v => v.Enabled).Select(v =>
        new RequestVariable(v.Name.TrimStart('!'), v.Value, v.Name.StartsWith('!'))).ToArray();
    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        if (_disposed) return;
        try { await work(_closing.Token); }
        catch (OperationCanceledException) { if (!_disposed) Status = "操作已取消。"; }
        catch (Exception ex) when (ex is RequestFailure or IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        { if (!_disposed) Status = ex is RequestFailure failure ? failure.Message : "操作未完成：" + ex.GetType().Name; }
    }
    private async Task PreviewAsync(CancellationToken token)
    {
        var operation = ++_operation; var draft = Snapshot(); var environment = SelectedEnvironment;
        _prepared?.Dispose(); _prepared = null; SendCommand.NotifyCanExecuteChanged();
        var next = await _preparation.PrepareAsync(draft, environment, token);
        if (_disposed || operation != _operation || draft.Revision != _revision) { next.Dispose(); return; }
        _prepared = next; PreviewText = next.Preview; Status = "预览有效120秒。发送不会自动重试或跟随重定向。"; SendCommand.NotifyCanExecuteChanged();
    }
    private async Task SendAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_prepared is null) throw new RequestFailure("RQ006", "请先预览。");
        _attempt = _execution.Start(_prepared, _draftId, _revision, SelectedEnvironment); _prepared = null;
        Busy = true; SendCommand.NotifyCanExecuteChanged(); Status = "正在发送；可取消，取消后的远端结果可能未知。";
        try
        {
            await _attempt.Completion;
            if (_disposed) return;
            Status = _attempt.Snapshot.Message;
            await Results.RefreshAsync(); await Results.OpenAnalyzedRecordAsync(_session, _attempt.Id);
            await LoadHistoryAsync(token);
        }
        finally { if (!_disposed) { Busy = false; SendCommand.NotifyCanExecuteChanged(); } }
    }
    private async Task ReloadAsync(CancellationToken token)
    {
        var loaded = await _libraryStore.LoadAsync(token); if (_disposed) return;
        SetLibrary(loaded); Status = "模板与环境已加载，没有发送请求。";
        await LoadHistoryAsync(token);
    }
    private async Task LoadHistoryAsync(CancellationToken token)
    {
        if (_history is null) return;
        var rows = await _history.ReadAsync(token); if (_disposed) return;
        History.Clear(); foreach (var row in rows) History.Add(row);
    }
    private void SetLibrary(RequestLibrary value)
    {
        // ItemsSource清空会通过双向绑定立即清除选择，必须先固定身份，再按新库恢复修订。
        var environmentId = SelectedEnvironment?.Id; var templateId = SelectedTemplate?.Id;
        _library = value; Templates.Clear(); foreach (var template in value.Templates) Templates.Add(template);
        Environments.Clear(); foreach (var environment in value.Environments) Environments.Add(environment);
        // 外部修订变更必须使旧预览失效；不保留已不属于当前库的环境引用。
        SelectedEnvironment = value.Environments.FirstOrDefault(e => e.Id == environmentId);
        SelectedTemplate = value.Templates.FirstOrDefault(t => t.Id == templateId);
    }
    private async Task SaveTemplateAsync(CancellationToken token)
    {
        var template = new RequestTemplate(Guid.NewGuid(), 1, TemplateName, RequestTemplateSanitizer.Sanitize(Snapshot()));
        var saved = await _libraryStore.CommitAsync(_library with { Templates = [.. _library.Templates, template] }, _library.Revision, token);
        if (!_disposed) { SetLibrary(saved); Status = "模板已另存；敏感头和变量需要下次重新填写。"; }
    }
    private async Task SaveEnvironmentAsync(CancellationToken token)
    {
        var variables = ParseVariables(EnvironmentText);
        if (variables.Any(v => v.Sensitive)) throw new RequestFailure("RQ005", "敏感变量请填在本次变量区域，不写入环境库。");
        var environment = new RequestEnvironment(Guid.NewGuid(), 1, EnvironmentName, variables);
        var saved = await _libraryStore.CommitAsync(_library with { Environments = [.. _library.Environments, environment] }, _library.Revision, token);
        if (!_disposed) { SetLibrary(saved); SelectedEnvironment = environment; Status = "环境已保存，原预览失效。"; }
    }
    public void Load(RequestDraft draft)
    {
        _comparisonOperation++; _comparisonSource = null; _comparisonBody = []; _comparisonRequest = []; _sourceReservation?.Dispose(); _sourceReservation = null;
        ComparisonText = "尚未选择对比来源。";
        _draftId = Guid.NewGuid(); _origin = draft.Origin; _bodyAsset = draft.BodyAssetId; _defaults = draft.Defaults ?? [];
        Method = draft.Method; Address = draft.Address; BodyKind = draft.BodyKind; BodyText = draft.BodyKind == RequestBodyKind.Form
            ? string.Join("\n", (draft.Form ?? []).Select(f => (f.Enabled ? "" : "# ") + f.Name + "=" + f.Value)) : draft.Body;
        HeadersText = string.Join("\n", draft.Headers.Select(h => (h.Enabled ? "" : "# ") + h.Name + ": " + h.Value));
        if (draft.QueryEditorText is { } query) QueryText = query;
        VariablesText = string.Join("\n", (draft.Variables ?? []).Select(v => (v.Sensitive ? "!" : "") + v.Name + "=" + v.Value));
        MediaType = draft.MediaType ?? "application/octet-stream"; Invalidate(); Status = "草稿已载入；原始/有效双快照和应用规则仍不可用。";
    }
    private async Task ChangeTemplateAsync(bool delete, CancellationToken token)
    {
        var selected = SelectedTemplate ?? throw new RequestFailure("RQ012", "请先选中模板。");
        var replacement = selected with { Revision = selected.Revision + 1, Name = TemplateName, Draft = RequestTemplateSanitizer.Sanitize(Snapshot()) };
        var templates = _library.Templates.Where(t => !delete || t.Id != selected.Id).Select(t => t.Id == selected.Id ? replacement : t).ToArray();
        var saved = await _libraryStore.CommitAsync(_library with { Templates = templates }, _library.Revision, token);
        if (!_disposed) { SetLibrary(saved); SelectedTemplate = saved.Templates.FirstOrDefault(t => t.Id == selected.Id); Status = delete ? "模板已删除。" : "模板修订已更新。"; }
    }
    private async Task ChangeEnvironmentAsync(bool delete, CancellationToken token)
    {
        var selected = SelectedEnvironment ?? throw new RequestFailure("RQ012", "请先选中环境。");
        var replacement = selected with { Revision = selected.Revision + 1, Name = EnvironmentName, Variables = ParseVariables(EnvironmentText) };
        var environments = _library.Environments.Where(e => !delete || e.Id != selected.Id).Select(e => e.Id == selected.Id ? replacement : e).ToArray();
        var saved = await _libraryStore.CommitAsync(_library with { Environments = environments }, _library.Revision, token);
        if (!_disposed) { SetLibrary(saved); Status = delete ? "环境已删除，旧预览失效。" : "环境修订已更新，旧预览失效。"; }
    }
    public async Task ExtractAsync(ICaptureSession source, Guid recordId)
    {
        await RunAsync(async token =>
        {
            var reservation = _preparation.ContentBudget.TryReserve(RequestLimits.ReservationBytes) ?? throw new RequestFailure("RQ008", "来源读取额度不足。");
            try
            {
                using var lease = source.AcquireReadLease();
                var revision = _revision; var (next, original) = await RequestSnapshotFactory.ExtractWithSnapshotAsync(source, recordId, _files, token);
                var body = await source.ReadBodyAsync(original.ResponseBody, RequestLimits.BodyBytes, token);
                var requestBody = await source.ReadBodyAsync(original.RequestBody, RequestLimits.BodyBytes, token);
                if (_disposed || revision != _revision) return;
                Load(next); _comparisonSource = original; _comparisonBody = body; _comparisonRequest = requestBody; _sourceReservation = reservation; reservation = null;
            }
            finally { reservation?.Dispose(); }
        });
    }
    public async Task ImportFileAsync(string path) => await RunAsync(async token =>
    {
        var revision = _revision; var asset = await _files.ImportAsync(path, token);
        if (_disposed || revision != _revision) return;
        _bodyAsset = asset; BodyKind = RequestBodyKind.RawFile; Invalidate(); Status = "原始文件已冻结；预览后原文件变化不会改变本次发送字节。";
    });
    private async Task CompareAsync(CancellationToken token)
    {
        if (_comparisonSource is null || _attempt is null) throw new RequestFailure("RQ014", "请先从记录提取，再发送并对比。");
        var attempt = _attempt; var operation = ++_comparisonOperation;
        var comparison = await _comparison.CompareAsync(_comparisonSource, _comparisonBody, _comparisonRequest, _session, attempt.Id, token);
        if (_disposed || operation != _comparisonOperation || !ReferenceEquals(attempt, _attempt)) return;
        ComparisonText = comparison.Message + "\n" + string.Join("\n", comparison.Differences.Select(d => $"{d.Location}\n旧：{d.Before}\n新：{d.After}"));
    }
    public void Refresh()
    {
        if (_disposed) return;
        if (Busy && _attempt is { } attempt) Status = attempt.Snapshot.Message + $" 已接收 {attempt.Snapshot.ResponseBytes:N0} B";
        SendCommand.NotifyCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _operation++; _closing.Cancel(); _attempt?.Cancel();
        _prepared?.Dispose(); _prepared = null; _comparisonBody = []; _comparisonRequest = []; _sourceReservation?.Dispose(); _sourceReservation = null;
        VariablesText = ""; HeadersText = ""; BodyText = ""; _defaults = []; Results.Dispose();
        // 在途操作仍可能观察此令牌，先取消而不提前Dispose其源；它随页面回收，不抛弃网络任务。
    }
}
