using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;

namespace NetWorkTrafficPlugin.Application.Requests;

public interface IRequestBodyStore
{
    Task<RequestBodyLease> AcquireAsync(Guid assetId, CancellationToken token);
}

/// <summary>Ready 的资源由预览拥有；成功接纳后转移给 Attempt，页面失效不能提前清除正在发送的正文。</summary>
public sealed class PreparedRequest : IDisposable
{
    private readonly object _sync = new();
    private readonly IDisposable _reservation;
    private readonly TimeProvider _clock;
    private readonly long _created;
    private readonly ITimer _expiry;
    private int _state;
    public Guid Token { get; } = Guid.NewGuid();
    public Guid DraftId { get; }
    public long DraftRevision { get; }
    public Guid? EnvironmentId { get; }
    public long EnvironmentRevision { get; }
    public RequestOrigin? Origin { get; }
    public RequestWireInput Wire { get; }
    public bool Sensitive { get; }
    public string BodyPreview { get; }
    public string SafeAddress => Sensitive ? Wire.Address.GetLeftPart(UriPartial.Authority) + "/[已隐藏]" : Wire.Address.GetLeftPart(UriPartial.Path);
    public string Preview => $"{Wire.Method} {(Sensitive ? SafeAddress : Wire.Address.AbsoluteUri)}\n来源：{Origin?.Kind.ToString() ?? "手工"}；环境修订：{EnvironmentRevision}；当前规则：不应用\n正文：{Wire.Body?.Length ?? 0} 字节；协议：HTTP/1.1\nHost: {Wire.Address.Authority}\nConnection: close\n" +
        (Wire.Body is null ? "" : $"Content-Type: {Wire.MediaType}\nContent-Length: {Wire.Body.Length}\n") +
        string.Join("\n", Wire.Headers.Select(h => h.Name + ": " + (Sensitive || RequestValidation.IsSensitive(h.Name) ? "[敏感值]" : h.Value))) + "\n\n" + BodyPreview;
    public bool IsReady { get { lock (_sync) { if (_state == 0 && _clock.GetElapsedTime(_created).TotalSeconds >= RequestLimits.PreviewSeconds) Release(); return _state == 0; } } }
    internal PreparedRequest(RequestDraft draft, RequestEnvironment? environment, RequestWireInput wire, bool sensitive, IDisposable reservation, TimeProvider clock, string bodyPreview = "")
    {
        DraftId = draft.Id; DraftRevision = draft.Revision; EnvironmentId = environment?.Id; EnvironmentRevision = environment?.Revision ?? 0;
        Origin = draft.Origin; Wire = wire; Sensitive = sensitive; _reservation = reservation; _clock = clock; _created = clock.GetTimestamp();
        BodyPreview = sensitive ? "[敏感正文不展示、不保存摘要]" : bodyPreview;
        _expiry = clock.CreateTimer(_ => Dispose(), null, TimeSpan.FromSeconds(RequestLimits.PreviewSeconds), Timeout.InfiniteTimeSpan);
    }
    internal void Consume(Guid draftId, long revision, RequestEnvironment? environment)
    {
        lock (_sync)
        {
            if (!IsReady || draftId != DraftId || revision != DraftRevision || environment?.Id != EnvironmentId || (environment?.Revision ?? 0) != EnvironmentRevision)
                throw new RequestFailure("RQ006", "预览已消费、过期或输入发生变化，请重新预览。");
            _state = 1;
        }
    }
    public void Dispose() { lock (_sync) if (_state == 0) Release(); }
    internal void Complete() { lock (_sync) if (_state != 2) Release(); }
    private void Release() { _state = 2; _expiry.Dispose(); try { Wire.Body?.Dispose(); } finally { _reservation.Dispose(); } }
    public override string ToString() => "请求预览（敏感输入不输出）";
}

/// <summary>准备完全离线；没有传输端口，因而预览、变量替换和规则不可用诊断不能意外发网。</summary>
public sealed class RequestPreparation(ContentMemoryBudget memory, IRequestBodyStore? bodies = null, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _previews = new(RequestLimits.Active, RequestLimits.Active);
    public ContentMemoryBudget ContentBudget => memory;
    public async Task<PreparedRequest> PrepareAsync(RequestDraft draft, RequestEnvironment? environment, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (draft.ApplyRules) throw new RequestFailure("RQ007", "M3 真实改写与双快照尚未接通，不能应用当前规则。");
        if (!_previews.Wait(0)) throw new RequestFailure("RQ009", "已有四项准备或预览，请先关闭旧预览。");
        var allocation = memory.TryReserve(RequestLimits.ReservationBytes);
        if (allocation is null) { _previews.Release(); throw new RequestFailure("RQ008", "共享内容额度不足，请关闭旧预览后再试。"); }
        var reservation = new CaptureReadLease(() => { allocation.Dispose(); _previews.Release(); });
        RequestBodyLease? body = null;
        try
        {
            if (!Enum.IsDefined(draft.BodyKind)) throw new RequestFailure("RQ004", "不支持的正文模式。");
            RequestValidation.Method(draft.Method, draft.BodyKind != RequestBodyKind.None);
            var resolver = new RequestVariableResolver(draft.Defaults ?? [], environment?.Variables ?? [], draft.Variables ?? []);
            var address = RequestValidation.Address(resolver.Address(draft.Address));
            if (draft.Headers.Length > RequestLimits.HeaderLines) throw new RequestFailure("RQ008", "Header 超过 256 行。");
            var headers = new List<HeaderField>(); var headerBytes = 0; var sensitive = false;
            foreach (var field in draft.Headers.Where(h => h.Enabled))
            {
                var value = resolver.Resolve(field.Value); RequestValidation.Header(field.Name, value);
                headerBytes += Encoding.UTF8.GetByteCount(field.Name) + Encoding.UTF8.GetByteCount(value) + 4;
                if (headerBytes > RequestLimits.HeaderBytes) throw new RequestFailure("RQ008", "Header 超过 64 KiB。");
                sensitive |= field.Sensitive || RequestValidation.IsSensitive(field.Name); headers.Add(new(field.Name, value));
            }
            string? media = null; var preview = "无正文";
            if (draft.BodyKind == RequestBodyKind.RawFile)
            {
                if (draft.BodyAssetId is not { } asset || bodies is null) throw new RequestFailure("RQ004", "尚未选择完整的原始文件。");
                body = await bodies.AcquireAsync(asset, token).ConfigureAwait(false);
                media = RequestValidation.MediaType(draft.MediaType ?? "application/octet-stream");
                preview = "完整原始文件 SHA-256：" + body.Digest;
            }
            else if (draft.BodyKind != RequestBodyKind.None)
            {
                var text = draft.BodyKind switch
                {
                    RequestBodyKind.Json => resolver.Json(draft.Body),
                    RequestBodyKind.Form => Form(draft.Form ?? [], resolver),
                    _ => resolver.Resolve(draft.Body)
                };
                var bytes = RequestValidation.Utf8(text);
                try { body = new MemoryRequestBody(bytes); } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
                media = draft.BodyKind switch
                {
                    RequestBodyKind.Json => "application/json; charset=utf-8",
                    RequestBodyKind.Form => "application/x-www-form-urlencoded; charset=utf-8",
                    _ => RequestValidation.Utf8MediaType(draft.MediaType ?? "text/plain; charset=utf-8")
                };
                preview = (text.Length <= 4096 ? text : text[..4096] + "\n[预览仅显示前4096字符]") + "\nSHA-256：" + body.Digest;
            }
            token.ThrowIfCancellationRequested();
            return new(draft, environment, new(draft.Method, address, headers, body, media), sensitive || resolver.UsedSensitive || (draft.Form?.Any(f => f.Sensitive) ?? false), reservation, _clock, preview);
        }
        catch { body?.Dispose(); reservation.Dispose(); throw; }
    }
    private static string Form(RequestField[] fields, RequestVariableResolver resolver)
    {
        if (fields.Length > RequestLimits.Items) throw new RequestFailure("RQ008", "表单超过 256 项。");
        static string Encode(string value) => Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
        var output = new StringBuilder();
        foreach (var field in fields.Where(f => f.Enabled))
        {
            var item = Encode(resolver.Resolve(field.Name)) + "=" + Encode(resolver.Resolve(field.Value));
            if (output.Length + item.Length + 1 > RequestLimits.BodyBytes) throw new RequestFailure("RQ008", "表单超过正文预算。");
            if (output.Length != 0) output.Append('&'); output.Append(item);
        }
        return output.ToString();
    }
}
