using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Features.Capture;

public sealed partial class CaptureWorkspace
{
    [ObservableProperty] private bool _fileDialogVisible, _pickingFile;
    [ObservableProperty] private string _fileDialogTitle = "", _fileDialogHint = "", _fileDialogPath = "";
    private WorkspaceFileKind _fileKind;
    private ICaptureSession? _fileSource;
    private Guid? _fileRecord;
    private bool _fileDirection;
    private long _fileRevision, _openRevision;
    public long FileDialogRevision => _fileRevision;
    public IAsyncRelayCommand ExecuteFileCommand => _executeFileCommand ??= new AsyncRelayCommand(ExecuteFileAsync);
    private IAsyncRelayCommand? _executeFileCommand;
    public void BeginFileOperation(WorkspaceFileKind kind)
    {
        if (_disposed) return;
        _fileRevision++; _fileKind = kind; _fileSource = Session; _fileRecord = Selected?.RecordId; _fileDirection = RequestSelected;
        FileDialogTitle = kind switch { WorkspaceFileKind.Save => "保存会话快照", WorkspaceFileKind.Open => "打开只读存档", WorkspaceFileKind.Recover => "恢复工作日志", _ => "导出已保存正文" };
        FileDialogHint = kind switch
        {
            WorkspaceFileKind.Save => "保存当前会话仍保留的全部记录，包含筛选隐藏项和真实内容。浏览选择父目录，最终路径必须为新目录。",
            WorkspaceFileKind.Open => "选择已有存档目录。代理可继续运行，回看不会重发请求。",
            WorkspaceFileKind.Recover => "选择本插件工作目录，仅恢复已经持久化的记录，不恢复网络任务。",
            _ => $"导出当前{(RequestSelected ? "请求" : "响应")}已保存的实体字节，不补下载、不覆盖已有文件；可能包含真实敏感内容。"
        };
        FileDialogPath = kind == WorkspaceFileKind.Export ? ExportPath : ArchivePath;
        FileDialogVisible = true;
    }
    public void CloseFileDialog() { _fileRevision++; FileDialogVisible = false; }
    private bool FileContextCurrent(long revision) => !_disposed && revision == _fileRevision &&
        ReferenceEquals(_fileSource, Session) && (_fileKind != WorkspaceFileKind.Export ||
        (_fileRecord == Selected?.RecordId && _fileDirection == RequestSelected));
    public async Task BrowseFileAsync(IWorkspaceFilePicker picker)
    {
        if (PickingFile || !FileDialogVisible || _disposed) return;
        var revision = _fileRevision; var feedback = Feedback.Begin(); PickingFile = true;
        try
        {
            var path = await picker.ChooseAsync(_fileKind, FileDialogPath, _closing.Token);
            if (FileContextCurrent(revision) && path is not null) FileDialogPath = path;
            else if (!_disposed && revision == _fileRevision && path is not null) { CloseFileDialog(); Feedback.Complete(feedback, "当前记录或会话已改变，请重新发起操作。", true); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or InvalidOperationException)
        { if (FileContextCurrent(revision)) Feedback.Complete(feedback, ex.Message, true); }
        finally { if (!_disposed) PickingFile = false; }
    }
    private async Task ExecuteFileAsync()
    {
        if (!FileContextCurrent(_fileRevision))
        {
            CloseFileDialog();
            if (!_disposed) Feedback.Complete(Feedback.Begin(), "当前记录或会话已改变，请重新发起操作。", true);
            return;
        }
        var kind = _fileKind;
        if (kind == WorkspaceFileKind.Export) ExportPath = FileDialogPath; else ArchivePath = FileDialogPath;
        CloseFileDialog();
        switch (kind)
        {
            case WorkspaceFileKind.Save: await SaveCommand.ExecuteAsync(null); break;
            case WorkspaceFileKind.Open: await OpenCommand.ExecuteAsync(null); break;
            case WorkspaceFileKind.Recover: await RecoverCommand.ExecuteAsync(null); break;
            case WorkspaceFileKind.Export: await ExportCommand.ExecuteAsync(null); break;
        }
    }
}
