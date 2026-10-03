using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Infrastructure.Analysis;

/// <summary>
/// 一个冻结记录的只读提取器。正文只在需要时读取一次，租约覆盖字节和解析对象；
/// 它不使用查看器的宽容解析，防止损坏/部分数据被当成确定的业务身份。
/// </summary>
public sealed class RequestFieldReader(TrafficRecord record, ICaptureSession session, ContentMemoryBudget memory,
    EvaluationBudget work, string source = "capture") : IFieldSource, IDisposable
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private IDisposable? _reservation;
    private JsonDocument? _json;
    private byte[]? _bytes;
    private FieldValue? _bodyFailure;
    private bool _read;
    public async ValueTask<FieldValue> ReadAsync(AccessExpr access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); work.Check(); FieldValue value;
        if (access.Name == "source") return FieldValue.Text(source);
        if (access.Name == "statusCode") return record.StatusCode is { } status ? FieldValue.Number(status) :
            FieldValue.Problem(!session.IsArchive && record.ResponseBody.Transport == TransportState.Receiving ? ValueState.NotReady : ValueState.Unavailable, DslType.Number, "DATA_NO_STATUS");
        if (record.MetadataPartial) value = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_METADATA_PARTIAL");
        else try
        {
            value = access.Name switch
            {
                "host" => FieldValue.Text(new Uri(record.OriginalUri).IdnHost.ToLowerInvariant()),
                "method" => FieldValue.Text(record.Method.ToUpperInvariant()),
                "path" => FieldValue.Text(new Uri(record.OriginalUri).AbsolutePath, true),
                "pathSegment" => PathSegment(access.Argument!),
                "query" => Fields(Query(record.OriginalUri), access.Argument!),
                "headers" => Fields(record.RequestHeaders.Select(h => (h.Name, FieldValue.Text(h.Value, true))), access.Argument!, StringComparison.OrdinalIgnoreCase),
                "cookies" => Fields(Cookies(), access.Argument!),
                "json" or "form" => await BodyAsync(access, token),
                _ => FieldValue.Problem(ValueState.Invalid, reason: "DATA_ACCESS")
            };
        }
        catch (Exception ex) when (ex is UriFormatException or DecoderFallbackException or FormatException or JsonException)
        { value = FieldValue.Problem(ValueState.Invalid, reason: "DATA_ENCODING_OR_FORMAT"); }
        return value with { Sensitive = access.Name is not ("host" or "method"), Origins = [new(access.Name, access.Argument ?? "", value.State)] };
    }
    private FieldValue PathSegment(string argument)
    {
        var path = new Uri(record.OriginalUri).AbsolutePath;
        var segments = path == "/" ? [] : path.TrimStartOneSlash().Split('/');
        var index = int.Parse(argument, System.Globalization.CultureInfo.InvariantCulture);
        return index >= segments.Length ? FieldValue.Problem(ValueState.Missing) : FieldValue.Text(Decode(segments[index], false), true);
    }
    private static IEnumerable<(string Name, FieldValue Value)> Query(string uri)
    {
        var index = uri.IndexOf('?'); return index < 0 ? [] : Pairs(uri[(index + 1)..].Split('#')[0], false);
    }
    private static IEnumerable<(string Name, FieldValue Value)> Pairs(string text, bool form)
    {
        if (text.Length == 0) yield break;
        foreach (var part in text.Split('&'))
        {
            var index = part.IndexOf('='); var name = Decode(index < 0 ? part : part[..index], form);
            yield return (name, index < 0 ? FieldValue.Problem(ValueState.Null) : FieldValue.Text(Decode(part[(index + 1)..], form), true));
        }
    }
    private IEnumerable<(string Name, FieldValue Value)> Cookies()
    {
        foreach (var header in record.RequestHeaders.Where(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            foreach (var raw in header.Value.Split(';'))
            {
                var part = raw.Trim(' ', '\t'); var index = part.IndexOf('=');
                if (index < 1) throw new FormatException("Cookie项无名称或等号。");
                yield return (part[..index].Trim(), FieldValue.Text(part[(index + 1)..], true));
            }
    }
    private static FieldValue Fields(IEnumerable<(string Name, FieldValue Value)> fields, string key, StringComparison comparison = StringComparison.Ordinal)
    {
        var matches = new List<FieldValue>();
        foreach (var field in fields)
            if (field.Name.Equals(key, comparison))
            {
                if (matches.Count == DslLimits.CollectionItems) return FieldValue.Problem(ValueState.BudgetExceeded, DslType.Strings, "DATA_COLLECTION_LIMIT");
                matches.Add(field.Value);
            }
        return matches.Count == 0 ? FieldValue.Problem(ValueState.Missing, DslType.Strings) : FieldValue.Items(matches.AsReadOnly());
    }
    // 分析与改写共享严格转义规则；保留旧入口，避免现有调用者因内部提取而破坏兼容。
    public static string Decode(string text, bool form) => RequestFieldText.Decode(text, form);
    private async ValueTask<FieldValue> BodyAsync(AccessExpr access, CancellationToken token)
    {
        var body = record.RequestBody; var mime = body.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (body.Transport != TransportState.Receiving && body.ObservedBytes == 0 && body.CommittedBytes == 0) return FieldValue.Problem(ValueState.Missing);
        if (mime is null) return FieldValue.Problem(ValueState.Unavailable, reason: "DATA_CONTENT_TYPE");
        var isJson = mime == "application/json" || mime.EndsWith("+json", StringComparison.Ordinal);
        if (access.Name == "json" && !isJson) return FieldValue.Problem(ValueState.Missing);
        if (access.Name == "form" && mime != "application/x-www-form-urlencoded") return FieldValue.Problem(mime.StartsWith("multipart/", StringComparison.Ordinal) ? ValueState.Unavailable : ValueState.Missing, reason: "DATA_FORMAT");
        if (body.Transport == TransportState.Receiving) return FieldValue.Problem(session.IsArchive ? ValueState.Unavailable : ValueState.NotReady, reason: session.IsArchive ? "DATA_ARCHIVED_INCOMPLETE" : "DATA_RECEIVING");
        if (body.Capture != CaptureState.Complete) return FieldValue.Problem(ValueState.Unavailable, reason: "DATA_BODY_INCOMPLETE");
        if (!_read)
        {
            _read = true;
            if (body.CommittedBytes > DslLimits.BodyBytes) _bodyFailure = FieldValue.Problem(ValueState.BudgetExceeded, reason: "DATA_BODY_LIMIT");
            else if (body.Representation == BodyRepresentation.Unknown) _bodyFailure = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_REPRESENTATION");
            else
            {
                // 保守预留覆盖源/目标字节、UTF-16、DOM及有限解析辅助结构；额度不足不排队等代理。
                _reservation = memory.TryReserve(8L * DslLimits.BodyBytes);
                if (_reservation is null) _bodyFailure = FieldValue.Problem(ValueState.BudgetExceeded, reason: "DATA_MEMORY_LIMIT");
                else try
                {
                    var input = await session.ReadBodyAsync(body, DslLimits.BodyBytes, token);
                    work.Read(input.Length, 0);
                    if (input.LongLength != body.CommittedBytes) _bodyFailure = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_SHORT_READ");
                    else
                    {
                        _bytes = await DecodeBodyAsync(input, body, work, token);
                        var charset = body.ContentType?.Split(';').Select(x => x.Trim()).FirstOrDefault(x => x.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"');
                        if (charset is not null && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase)) _bodyFailure = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_CHARSET");
                        else if (isJson) { ValidateJson(_bytes, token); _json = JsonDocument.Parse(_bytes, new JsonDocumentOptions { MaxDepth = DslLimits.JsonDepth }); }
                        else _ = StrictUtf8.GetString(_bytes);
                    }
                }
                catch (NotSupportedException) { _bodyFailure = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_ENCODING"); }
                catch (BodyLimitException) { _bodyFailure = FieldValue.Problem(ValueState.BudgetExceeded, reason: "DATA_BODY_LIMIT"); }
                catch (Exception ex) when (ex is JsonException or InvalidDataException or DecoderFallbackException) { _bodyFailure = FieldValue.Problem(ValueState.Invalid, reason: "DATA_BODY_FORMAT"); }
                catch (IOException) { _bodyFailure = FieldValue.Problem(ValueState.Unavailable, reason: "DATA_BODY_IO"); }
            }
        }
        if (_bodyFailure is not null) return _bodyFailure;
        return access.Name == "json" ? JsonFieldPath.Read(_json!.RootElement, access.Argument!) : Fields(Pairs(StrictUtf8.GetString(_bytes!), true), access.Argument!);
    }
    private sealed class BodyLimitException : Exception;
    private static async Task<byte[]> DecodeBodyAsync(byte[] input, BodyDescriptor body, EvaluationBudget work, CancellationToken token)
    {
        if (body.Representation == BodyRepresentation.DecodedEntity || string.IsNullOrEmpty(body.ContentEncoding) || body.ContentEncoding.Equals("identity", StringComparison.OrdinalIgnoreCase))
        { work.Read(0, input.Length); return input; }
        using var stream = new MemoryStream(input, false);
        using Stream decoder = body.ContentEncoding.Trim().ToLowerInvariant() switch
        { "gzip" => new GZipStream(stream, CompressionMode.Decompress), "br" => new BrotliStream(stream, CompressionMode.Decompress), "deflate" => new DeflateStream(stream, CompressionMode.Decompress), _ => throw new NotSupportedException() };
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await decoder.ReadAsync(buffer, token)) != 0)
        {
            // 已经解出的字节即使使当前正文超限，也消耗了本轮资源。必须先计入整轮预算，
            // 否则一连串膨胀/损坏正文会因没有“成功结果”而绕过累计32MiB限制。
            work.Read(0, count);
            if (output.Length + count > DslLimits.BodyBytes) throw new BodyLimitException();
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        return output.ToArray();
    }
    private static void ValidateJson(byte[] bytes, CancellationToken token)
    {
        _ = StrictUtf8.GetString(bytes);
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = DslLimits.JsonDepth + 1 });
        var objects = new Stack<HashSet<string>>(); int tokens = 0;
        while (reader.Read())
        {
            if (++tokens > DslLimits.JsonTokens) throw new BodyLimitException(); if (tokens % 128 == 0) token.ThrowIfCancellationRequested();
            if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject && reader.CurrentDepth >= DslLimits.JsonDepth) throw new BodyLimitException();
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw new JsonException("重复属性。");
        }
    }
    public void Dispose() { _json?.Dispose(); _reservation?.Dispose(); _reservation = null; _bytes = null; }
}

internal static class PathText
{
    public static string TrimStartOneSlash(this string text) => text.StartsWith('/') ? text[1..] : text;
}
