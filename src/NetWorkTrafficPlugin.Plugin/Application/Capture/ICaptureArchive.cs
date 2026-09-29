namespace NetWorkTrafficPlugin.Application.Capture;

public sealed record ArchiveSaveResult(string Directory, Guid SnapshotId, int Records, long BodyBytes);

/// <summary>存档只复制既有事实；接口没有 HTTP 客户端或代理启动能力，因此重开不能变成请求重放。</summary>
public interface ICaptureArchive
{
    Task<ArchiveSaveResult> SaveAsync(ICaptureSession session, string targetDirectory, CancellationToken token);
    Task<ICaptureSession> OpenAsync(string directory, CancellationToken token);
    Task<ICaptureSession> RecoverAsync(string directory, CancellationToken token);
    Task ExportBodyAsync(BodyDescriptor body, string targetFile, CancellationToken token);
}
