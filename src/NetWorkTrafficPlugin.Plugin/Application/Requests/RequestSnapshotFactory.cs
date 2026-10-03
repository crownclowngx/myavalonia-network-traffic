using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>只从冻结的事实提取。schema1不是原始/有效双快照；出现缺口就解释原因，不能把一份头拼到另一份URL上。</summary>
public static class RequestSnapshotFactory
{
    public static async Task<RequestDraft> ExtractAsync(ICaptureSession session, Guid recordId, CancellationToken token)
        => (await ExtractWithSnapshotAsync(session, recordId, null, token).ConfigureAwait(false)).Draft;

    public static async Task<(RequestDraft Draft, TrafficRecord Record)> ExtractWithSnapshotAsync(ICaptureSession session, Guid recordId,
        IRequestFileImporter? files, CancellationToken token)
    {
        using var snapshot = await session.FreezeAsync(token).ConfigureAwait(false);
        var record = snapshot.Records.SingleOrDefault(r => r.RecordId == recordId) ?? throw new RequestFailure("RQ011", "来源记录已不在当前保存范围。");
        if (record.MetadataPartial || record.OriginalUri != record.EffectiveUri)
            throw new RequestFailure("RQ011", "旧记录缺少完整双快照或元信息，不支持精确提取；请手工构造新请求。");
        var descriptor = record.RequestBody;
        var provenEmpty = descriptor.ExpectedBytes == 0 && descriptor.ObservedBytes == 0 && descriptor.CommittedBytes == 0;
        if (descriptor.Transport != TransportState.Completed || descriptor.Capture != CaptureState.Complete && !provenEmpty ||
            descriptor.ObservedBytes != descriptor.CommittedBytes || descriptor.ExpectedBytes is { } expected && expected != descriptor.CommittedBytes ||
            descriptor.Representation != BodyRepresentation.EncodedEntity || descriptor.ContentEncoding is not (null or "" or "identity"))
            throw new RequestFailure("RQ011", "请求正文不完整或编码无法确定，不能把前缀当成完整请求发送。");
        if (descriptor.CommittedBytes > RequestLimits.FileBytes)
            throw new RequestFailure("RQ011", "来源正文超过64MiB，不能创建可发送草稿。");
        var hopHeaders = record.RequestHeaders.Where(h => h.Name.Equals("Connection", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(',').Select(v => v.Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var headers = record.RequestHeaders.Where(h => !RequestValidation.IsProtected(h.Name) && !hopHeaders.Contains(h.Name))
            .Select((h, i) => new RequestField(h.Name, RequestValidation.IsSensitive(h.Name) ? "${secret_" + i + "}" : Literal(h.Value),
                Sensitive: RequestValidation.IsSensitive(h.Name))).ToArray();
        var type = descriptor.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        var binary = descriptor.CommittedBytes > RequestLimits.BodyBytes || type is not null && !type.StartsWith("text/", StringComparison.Ordinal) &&
            type != "application/json" && !type.EndsWith("+json", StringComparison.Ordinal) && type != "application/x-www-form-urlencoded";
        if (binary && descriptor.CommittedBytes > 0)
        {
            if (files is null || session is not ICaptureBodyStreamReader reader)
                throw new RequestFailure("RQ011", "来源没有提供完整正文流读取能力。");
            // 在冻结租约存续期间复制为自己的不可变资产，关闭/删除原会话不会改变已提取的正文。
            await using var input = await reader.OpenBodyAsync(descriptor, token).ConfigureAwait(false);
            var asset = await files.ImportAsync(input, descriptor.CommittedBytes, token).ConfigureAwait(false);
            return (new(Guid.NewGuid(), 1, record.Method, Literal(record.OriginalUri), headers, RequestBodyKind.RawFile, "", BodyAssetId: asset,
                MediaType: descriptor.ContentType ?? "application/octet-stream", Origin: new(record.SessionId, record.RecordId, record.Revision, RequestSnapshotKind.LegacyObserved)), record);
        }
        var bytes = await session.ReadBodyAsync(descriptor, RequestLimits.BodyBytes, token).ConfigureAwait(false);
        if (bytes.Length != descriptor.CommittedBytes) throw new RequestFailure("RQ011", "来源正文文件缺失或截断。");
        if (descriptor.ContentType is { } contentType) RequestValidation.Utf8MediaType(contentType);
        var kind = bytes.Length == 0 ? RequestBodyKind.None : type == "application/json" || type?.EndsWith("+json", StringComparison.Ordinal) == true
            ? RequestBodyKind.Json : type == "application/x-www-form-urlencoded" ? RequestBodyKind.Form : RequestBodyKind.Text;
        if (bytes.Length > 0 && type is not null && !type.StartsWith("text/", StringComparison.Ordinal) && kind == RequestBodyKind.Text)
            throw new RequestFailure("RQ011", "该二进制正文需要完整原始文件，不能按文本猜测解码。");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { throw new RequestFailure("RQ011", "正文不是严格UTF-8，不能无损转为编辑文本。"); }
        // 仅移除协议派生字段。来自旧快照的身份值变为必填占位符，不自动沿用凭据发到新环境。
        var form = kind == RequestBodyKind.Form ? text.Split('&').Select(item =>
        {
            var parts = item.Split('=', 2);
            if (!RequestValidation.ValidEscapes(item)) throw new RequestFailure("RQ011", "表单转义损坏。");
            return new RequestField(Literal(DecodeForm(parts[0])), Literal(DecodeForm(parts.Length > 1 ? parts[1] : "")));
        }).ToArray() : null;
        return (new(Guid.NewGuid(), 1, record.Method, Literal(record.OriginalUri), headers, kind, Literal(text), form,
            MediaType: kind == RequestBodyKind.Text ? descriptor.ContentType : null,
            Origin: new(record.SessionId, record.RecordId, record.Revision, RequestSnapshotKind.LegacyObserved)), record);
    }
    // 捕获内容是数据，不是模板代码。保留原文中的${...}，不能因变量同名而在重发时意外替换。
    private static string Literal(string value) => value.Replace("${", "$${", StringComparison.Ordinal);
    private static string DecodeForm(string value)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '%') { bytes.WriteByte(Convert.ToByte(value.Substring(i + 1, 2), 16)); i += 2; }
            else if (value[i] == '+') bytes.WriteByte(32);
            else
            {
                var count = char.IsHighSurrogate(value[i]) && i + 1 < value.Length ? 2 : 1;
                bytes.Write(Encoding.UTF8.GetBytes(value.Substring(i, count))); i += count - 1;
            }
        }
        try { return new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException) { throw new RequestFailure("RQ011", "来源表单包含非UTF-8转义，不能无损提取。"); }
    }
}
