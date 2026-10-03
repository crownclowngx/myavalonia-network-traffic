using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>主动请求与代理传输分别拥有寿命；这里的上限约束编辑、准备和实际执行，不是发布容量承诺。</summary>
public static class RequestLimits
{
    public const int BodyBytes = 256 * 1024, HeaderBytes = 65536, HeaderLines = 256, UriCharacters = 8192;
    public const int Variables = 64, ValueCharacters = 4096, Items = 256, FileBytes = 64 * 1024 * 1024;
    public const int Active = 4, PreviewSeconds = 120, HeaderSeconds = 30, IdleSeconds = 60, LifetimeSeconds = 1800;
    public const int CompareLines = 4000, CompareDifferences = 500, CompareOperations = 100000;
    public const long ReservationBytes = 4L << 20, StagingBytes = 256L << 20;
}

public sealed class RequestFailure(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public enum RequestBodyKind { None, Text, Json, Form, RawFile }
public enum RequestSnapshotKind { LegacyObserved, Original, Effective }
public sealed record RequestOrigin(Guid SessionId, Guid RecordId, long Revision, RequestSnapshotKind Kind);
public sealed record RequestField(string Name, string Value, bool Enabled = true, bool Sensitive = false)
{ public override string ToString() => Name; }
public sealed record RequestVariable(string Name, string Value, bool Sensitive = false)
{ public override string ToString() => Name; }
public sealed record RequestEnvironment(Guid Id, long Revision, string Name, RequestVariable[] Variables)
{ public override string ToString() => Name; }
public sealed record RequestDraft(Guid Id, long Revision, string Method, string Address, RequestField[] Headers,
    RequestBodyKind BodyKind = RequestBodyKind.None, string Body = "", RequestField[]? Form = null,
    Guid? BodyAssetId = null, string? MediaType = null, RequestVariable[]? Variables = null,
    RequestVariable[]? Defaults = null, RequestOrigin? Origin = null, bool ApplyRules = false)
{ public override string ToString() => "请求草稿（内容省略）"; }

/// <summary>
/// 正文租约只给调用者完整且固定的字节，不授予任意文件读取权。释放必须发生在网络不再引用正文之后；
/// 原始文件选择与模板引用的所有权由具体存储器处理，发送器只消费一次独立读取流。
/// </summary>
public abstract class RequestBodyLease : IDisposable
{
    public abstract long Length { get; }
    public abstract string Digest { get; }
    public abstract ValueTask<Stream> OpenAsync(CancellationToken token);
    public abstract void Dispose();
    public override string ToString() => $"请求正文（{Length} 字节）";
}

public sealed class MemoryRequestBody : RequestBodyLease
{
    private byte[]? _bytes;
    private readonly string _digest;
    private readonly long _length;
    public MemoryRequestBody(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "正文超过 256 KiB 编辑上限。");
        _bytes = bytes.ToArray(); _length = bytes.Length;
        _digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }
    public override long Length => _length;
    public override string Digest => _digest;
    public override ValueTask<Stream> OpenAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(new MemoryStream(_bytes ?? throw new ObjectDisposedException(nameof(MemoryRequestBody)), false));
    }
    public override void Dispose()
    {
        var bytes = Interlocked.Exchange(ref _bytes, null);
        if (bytes is not null) System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes);
    }
}

/// <summary>发送输入只包含已经准备好的值；传输器不能访问变量库、规则协调器或原始文件路径。</summary>
public sealed class RequestWireInput(string method, Uri address, IEnumerable<HeaderField> headers,
    RequestBodyLease? body, string? mediaType)
{
    public string Method { get; } = method;
    public Uri Address { get; } = address;
    public IReadOnlyList<HeaderField> Headers { get; } = Array.AsReadOnly(headers.ToArray());
    public RequestBodyLease? Body { get; } = body;
    public string? MediaType { get; } = mediaType;
    public override string ToString() => "冻结的主动请求（不输出地址、头或正文）";
}

/// <summary>只有显式执行用例持有此端口。返回值拥有响应流，调用方必须先结束读取再释放。</summary>
public interface IRequestTransport
{
    Task<RequestTransportResponse> SendAsync(RequestWireInput request, CancellationToken token, Action<ReadOnlyMemory<byte>>? requestBytes = null);
}

public interface IRequestFileImporter
{
    Task<Guid> ImportAsync(string source, CancellationToken token);
    Task<Guid> ImportAsync(Stream source, long length, CancellationToken token);
}

public sealed class RequestTransportResponse(int status, string protocol, HeaderField[] headers,
    Stream body, IDisposable owner) : IAsyncDisposable
{
    public int StatusCode { get; } = status;
    public string Protocol { get; } = protocol;
    public IReadOnlyList<HeaderField> Headers { get; } = Array.AsReadOnly(headers);
    public Stream Body { get; } = body;
    private IDisposable? _owner = owner;
    public async ValueTask DisposeAsync()
    {
        var value = Interlocked.Exchange(ref _owner, null);
        if (value is null) return;
        try { await Body.DisposeAsync().ConfigureAwait(false); }
        finally { value.Dispose(); }
    }
}
