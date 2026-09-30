using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace NetWorkTrafficPlugin.Features.Capture;

public enum WorkspaceFileKind { Save, Open, Recover, Export }

/// <summary>平台边界只负责选择路径；不会保存记录或决定业务范围，取消返回 null。</summary>
public interface IWorkspaceFilePicker
{
    Task<string?> ChooseAsync(WorkspaceFileKind kind, string suggestedPath, CancellationToken cancellationToken);
}

/// <summary>
/// 公开 Avalonia StorageProvider 适配。保存档案选择的是现有父目录，最终目标仍是新子目录；
/// 文件保存框关闭覆盖提示不能放宽存储层的“禁止覆盖”，最后由原归档服务再次校验。
/// </summary>
public sealed class WorkspaceFilePicker(Func<TopLevel?> topLevel) : IWorkspaceFilePicker
{
    public async Task<string?> ChooseAsync(WorkspaceFileKind kind, string suggestedPath, CancellationToken cancellationToken)
    {
        var storage = topLevel()?.StorageProvider ?? throw new NotSupportedException("当前宿主没有路径选择器，请手工输入路径。");
        cancellationToken.ThrowIfCancellationRequested();
        string? path;
        if (kind == WorkspaceFileKind.Export)
        {
            if (!storage.CanSave) throw new NotSupportedException("当前宿主不支持文件选择，请手工输入新文件路径。");
            var file = await storage.SaveFilePickerAsync(new() { Title = "导出已保存正文到新文件", SuggestedFileName = Path.GetFileName(suggestedPath), ShowOverwritePrompt = false });
            path = file?.TryGetLocalPath(); file?.Dispose();
        }
        else
        {
            if (!storage.CanPickFolder) throw new NotSupportedException("当前宿主不支持目录选择，请手工输入路径。");
            var folders = await storage.OpenFolderPickerAsync(new() { Title = kind == WorkspaceFileKind.Save ? "选择存档父目录（保存到新子目录）" : "选择已有记录目录", AllowMultiple = false });
            path = folders.FirstOrDefault()?.TryGetLocalPath();
            foreach (var folder in folders) folder.Dispose();
            if (path is not null && kind == WorkspaceFileKind.Save) path = Path.Combine(path, Path.GetFileName(suggestedPath));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }
}
