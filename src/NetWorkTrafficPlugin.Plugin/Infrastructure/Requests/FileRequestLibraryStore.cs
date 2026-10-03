using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Capture;

namespace NetWorkTrafficPlugin.Infrastructure.Requests;

/// <summary>一个JSON是模板和环境的提交单元；跨进程独占锁与修订比较阻止两个页面静默覆盖。</summary>
public sealed class FileRequestLibraryStore(string directory, Action<string>? checkpoint = null, FileRequestBodyStore? bodies = null) : IRequestLibraryStore
{
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private const int MaximumBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 40 };
    public async Task<RequestLibrary> LoadAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try { return await ReadAsync(token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }
    private async Task<RequestLibrary> ReadAsync(CancellationToken token)
    {
        var path = Path.Combine(_directory, "requests.json"); CaptureDiskBudget.EnsureRegular(path);
        if (!File.Exists(path)) return RequestLibrary.Empty;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 8192, true);
        if (stream.Length > MaximumBytes) throw new RequestFailure("RQ012", "请求库超过8MiB，未改写原文件。");
        var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        try
        {
            var value = JsonSerializer.Deserialize<RequestLibrary>(bytes, Json) ?? throw new RequestFailure("RQ012", "请求库为空。");
            Validate(value); return value;
        }
        catch (JsonException) { throw new RequestFailure("RQ012", "请求库格式损坏，未覆盖原文件。"); }
    }
    public async Task<RequestLibrary> CommitAsync(RequestLibrary candidate, long expectedRevision, CancellationToken token)
    {
        Validate(candidate); await _gate.WaitAsync(token).ConfigureAwait(false); string? temporary = null;
        var newlyPinned = new List<Guid>(); var committedIndex = false;
        try
        {
            Directory.CreateDirectory(_directory); CaptureDiskBudget.EnsureRegular(_directory);
            var lockPath = Path.Combine(_directory, "requests.lock"); CaptureDiskBudget.EnsureRegular(lockPath);
            using var fileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            var current = await ReadAsync(token).ConfigureAwait(false);
            if (current.Revision != expectedRevision || candidate.Revision != expectedRevision)
                throw new RequestFailure("RQ013", "模板或环境已被另一页更新，请重新加载后保存。");
            var committed = candidate with { Revision = checked(expectedRevision + 1) };
            foreach (var id in candidate.Templates.Where(t => t.Draft.BodyAssetId is not null).Select(t => t.Draft.BodyAssetId!.Value).Distinct())
            {
                if (bodies is null) throw new RequestFailure("RQ012", "未提供模板资产存储。");
                using var lease = await bodies.AcquireAsync(id, token).ConfigureAwait(false);
                if (!await bodies.SetPersistentAsync(id, true, token).ConfigureAwait(false)) newlyPinned.Add(id);
            }
            temporary = Path.Combine(_directory, ".requests-" + Guid.NewGuid().ToString("N") + ".tmp");
            // 直接序列化到有界文件流，不先生成可能几十MiB的字符串；失败只删除本次临时文件。
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
            {
                await using var limited = new LimitedWriteStream(file, MaximumBytes);
                await JsonSerializer.SerializeAsync(limited, committed, Json, token).ConfigureAwait(false);
                await file.FlushAsync(token).ConfigureAwait(false); file.Flush(true);
            }
            checkpoint?.Invoke("written"); token.ThrowIfCancellationRequested();
            var target = Path.Combine(_directory, "requests.json"); CaptureDiskBudget.EnsureRegular(target);
            if (File.Exists(target)) File.Replace(temporary, target, null); else File.Move(temporary, target);
            temporary = null; committedIndex = true;
            await ReclaimAsync(committed).ConfigureAwait(false); return committed;
        }
        finally
        {
            try
            {
                if (temporary is not null) File.Delete(temporary);
                if (!committedIndex && bodies is not null)
                    foreach (var id in newlyPinned) await bodies.SetPersistentAsync(id, false, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
    }
    private async Task ReclaimAsync(RequestLibrary library)
    {
        if (bodies is null) return;
        try
        {
            await bodies.ReconcileTemplatesAsync(library.Templates.Where(t => t.Draft.BodyAssetId is not null)
                .Select(t => t.Draft.BodyAssetId!.Value).ToHashSet(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { /* 索引已可信提交。回收失败保留计费资产，下次持提交锁重试；不能误报保存失败诱使调用方重复操作。 */ }
    }
    private static void Validate(RequestLibrary library)
    {
        if (library.SchemaVersion != 1 || library.Revision < 0 || library.Templates is null || library.Environments is null ||
            library.Templates.Length > 200 || library.Environments.Length > 50)
            throw new RequestFailure("RQ012", "请求库版本、数量或修订无效；保留原文件。");
        if (library.Templates.Any(t => t is null || t.Name is null || t.Id == Guid.Empty) ||
            library.Environments.Any(e => e is null || e.Name is null || e.Variables is null || e.Id == Guid.Empty) ||
            library.Templates.Select(t => t.Id).Distinct().Count() != library.Templates.Length || library.Environments.Select(e => e.Id).Distinct().Count() != library.Environments.Length)
            throw new RequestFailure("RQ012", "请求库身份重复。");
        long cost = 0;
        foreach (var template in library.Templates)
        {
            if (template.Name.Length is 0 or > 128 || template.Revision < 1 || template.Draft is null)
                throw new RequestFailure("RQ012", "模板名称或修订无效。");
            var draft = template.Draft;
            if (draft.Address is null || draft.Method is null || draft.Body is null || draft.Headers is null ||
                draft.Headers.Any(h => h is null || h.Name is null || h.Value is null) ||
                (draft.Form?.Any(f => f is null || f.Name is null || f.Value is null) ?? false) ||
                !Enum.IsDefined(draft.BodyKind) || (draft.BodyKind == RequestBodyKind.RawFile) != (draft.BodyAssetId is not null) ||
                draft.Address.Length > RequestLimits.UriCharacters || draft.Headers.Length > 256 || draft.Body.Length > RequestLimits.BodyBytes ||
                (draft.Form?.Length ?? 0) > 256 || draft.ApplyRules || draft.Origin is not null ||
                draft.QueryEditorText is { } query && (query.Length > RequestLimits.UriCharacters || !RequestQueryEditor.Matches(draft.Address, query)))
                throw new RequestFailure("RQ012", "模板输入超限或包含尚不支持的运行状态。");
            cost += Encoding.UTF8.GetByteCount(draft.Body) + Encoding.UTF8.GetByteCount(draft.Address);
            foreach (var header in draft.Headers)
            {
                RequestValidation.Header(header.Name, header.Value);
                if ((header.Sensitive || RequestValidation.IsSensitive(header.Name)) && !RequestTemplateSanitizer.IsSensitivePlaceholder(header.Value))
                    throw new RequestFailure("RQ012", "模板不能保存敏感头的具体值。");
                cost += Encoding.UTF8.GetByteCount(header.Name) + Encoding.UTF8.GetByteCount(header.Value);
            }
            foreach (var field in draft.Form ?? []) cost += Encoding.UTF8.GetByteCount(field.Name) + Encoding.UTF8.GetByteCount(field.Value);
            CheckVariables(draft.Variables ?? []); CheckVariables(draft.Defaults ?? []);
            if (cost > MaximumBytes) throw new RequestFailure("RQ012", "模板内容总量超过8MiB。");
        }
        foreach (var environment in library.Environments)
        {
            if (environment.Name.Length is 0 or > 128 || environment.Revision < 1) throw new RequestFailure("RQ012", "环境名称或修订无效。");
            CheckVariables(environment.Variables);
        }
    }
    private static void CheckVariables(RequestVariable[] variables)
    {
        if (variables.Any(v => v is null || v.Name is null || v.Value is null)) throw new RequestFailure("RQ012", "变量条目不能为空。");
        _ = new RequestVariableResolver(variables);
        if (variables.Any(v => v.Sensitive)) throw new RequestFailure("RQ012", "运行期敏感变量不能写入模板或环境库。");
    }
    private sealed class LimitedWriteStream(Stream inner, long maximum) : Stream
    {
        private long _written;
        private void Count(int count) { if (count > maximum - _written) throw new RequestFailure("RQ012", "请求库序列化超过8MiB。"); _written += count; }
        public override void Write(byte[] buffer, int offset, int count) { Count(count); inner.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Count(buffer.Length); inner.Write(buffer); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Count(buffer.Length); await inner.WriteAsync(buffer, token).ConfigureAwait(false); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken token) => inner.FlushAsync(token);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => _written; public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
