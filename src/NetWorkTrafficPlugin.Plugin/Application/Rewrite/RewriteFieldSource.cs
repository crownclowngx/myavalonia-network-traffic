using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Application.Rewrite;

/// <summary>容器共用正文槽与既有内容额度；额度不足立即反馈，不持网络请求无限等待。</summary>
public sealed class RewriteResources(ContentMemoryBudget memory)
{
    private int _active;
    public int Active => Volatile.Read(ref _active);
    public IDisposable Acquire()
    {
        if (Interlocked.Increment(ref _active) > RewriteLimits.BodySlots)
        { Interlocked.Decrement(ref _active); throw new RewriteFailure("RW009", "正文改写并发已满。", 503); }
        var reservation = memory.TryReserve(RewriteLimits.BodyReservationBytes);
        if (reservation is null) { Interlocked.Decrement(ref _active); throw new RewriteFailure("RW009", "共享内容额度不足。", 503); }
        return new Lease(this, reservation);
    }
    private sealed class Lease(RewriteResources owner, IDisposable reservation) : IDisposable
    {
        private IDisposable? _reservation = reservation;
        public void Dispose()
        {
            var released = Interlocked.Exchange(ref _reservation, null);
            if (released is null) return;
            released.Dispose(); Interlocked.Decrement(ref owner._active);
        }
    }
}

/// <summary>
/// 单请求原始字段源，不读取历史文件。先按作用域/短路求值，确需正文时才申请一次租约并解析。
/// 租约由执行者持有到库停止引用缓冲为止；预览执行者也必须 Dispose，不能仅取消等待。
/// </summary>
public sealed class RewriteFieldSource(RewriteInput input, IRewriteBodyReader bodyReader, RewriteResources resources) : IFieldSource, IDisposable
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private IDisposable? _lease;
    private JsonDocument? _json;
    private byte[]? _body;
    private System.Diagnostics.Stopwatch? _bodyClock;
    private bool _disposed;
    public bool BodyRead => _body is not null;
    public ReadOnlyMemory<byte>? OriginalBody => _body is null ? null : _body;
    /// <summary>截止覆盖读取和后续解析/序列化，不能在读取结束或下一个字段访问时重新取得两秒。</summary>
    public void CheckDeadline()
    {
        if (_bodyClock?.ElapsedMilliseconds >= RewriteLimits.BodyMilliseconds)
            throw new RewriteFailure("RW009", "正文读取和规划超过两秒截止。", 504);
    }
    public async ValueTask<FieldValue> ReadAsync(AccessExpr access, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed, this);
        FieldValue value;
        try
        {
            value = access.Name switch
            {
                "source" => FieldValue.Text("capture"),
                "host" => FieldValue.Text(RewriteValidation.Host(new Uri(input.Uri).IdnHost)),
                "method" => FieldValue.Text(input.Method.ToUpperInvariant()),
                "path" => FieldValue.Text(new Uri(input.Uri).AbsolutePath),
                "pathSegment" => Segment(access.Argument!),
                "headers" => Select(input.Headers.Select(h => (h.Name, FieldValue.Text(h.Value, true))), access.Argument!, StringComparison.OrdinalIgnoreCase),
                "query" => Select(Pairs(QueryText(), false), access.Argument!),
                "cookies" => Select(Cookies(), access.Argument!),
                "json" => JsonFieldPath.Read((await JsonAsync(token)).RootElement, access.Argument!),
                "form" => Select(Pairs(Utf8.GetString(await BodyAsync(false, token)), true), access.Argument!),
                _ => throw new RewriteFailure("RW003", "发送前不可读取该字段。")
            };
        }
        catch (Exception ex) when (ex is FormatException or DecoderFallbackException or JsonException)
        { value = FieldValue.Problem(ValueState.Invalid, reason: "RW008"); }
        return value with { Sensitive = access.Name is not ("host" or "method" or "source"), Origins = [new(access.Name, access.Argument ?? "", value.State)] };
    }
    private string QueryText() { var i = input.Uri.IndexOf('?'); return i < 0 ? "" : input.Uri[(i + 1)..]; }
    private FieldValue Segment(string index)
    {
        var raw = new Uri(input.Uri).AbsolutePath;
        var parts = raw == "/" ? [] : raw[1..].Split('/');
        var n = int.Parse(index, System.Globalization.CultureInfo.InvariantCulture);
        return n >= parts.Length ? FieldValue.Problem(ValueState.Missing) : FieldValue.Text(RequestFieldText.Decode(parts[n], false));
    }
    private static IEnumerable<(string Name, FieldValue Value)> Pairs(string text, bool form)
    {
        if (text.Length == 0) yield break;
        foreach (var item in text.Split('&'))
        {
            var eq = item.IndexOf('=');
            yield return (RequestFieldText.Decode(eq < 0 ? item : item[..eq], form), eq < 0 ? FieldValue.Problem(ValueState.Null) : FieldValue.Text(RequestFieldText.Decode(item[(eq + 1)..], form), true));
        }
    }
    private IEnumerable<(string Name, FieldValue Value)> Cookies()
    {
        foreach (var header in input.Headers.Where(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase)))
            foreach (var raw in header.Value.Split(';'))
            {
                var part = raw.Trim(); var eq = part.IndexOf('=');
                if (eq < 1) throw new FormatException("Cookie项无名称或等号。");
                yield return (part[..eq].Trim(), FieldValue.Text(part[(eq + 1)..], true));
            }
    }
    private static FieldValue Select(IEnumerable<(string Name, FieldValue Value)> pairs, string key, StringComparison comparison = StringComparison.Ordinal)
    {
        var selected = new List<FieldValue>();
        foreach (var pair in pairs)
            if (pair.Name.Equals(key, comparison))
            {
                if (selected.Count == DslLimits.CollectionItems) return FieldValue.Problem(ValueState.BudgetExceeded, reason: "RW009");
                selected.Add(pair.Value);
            }
        return selected.Count == 0 ? FieldValue.Problem(ValueState.Missing, DslType.Strings) : FieldValue.Items(selected.AsReadOnly());
    }
    public async ValueTask<JsonDocument> JsonAsync(CancellationToken token)
    {
        var bytes = await BodyAsync(true, token);
        if (_json is not null) return _json;
        ValidateJson(bytes, token); _json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = DslLimits.JsonDepth }); CheckDeadline();
        return _json;
    }
    private async ValueTask<byte[]> BodyAsync(bool json, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var contentType = input.Headers.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        var mime = contentType.Split(';')[0].Trim().ToLowerInvariant();
        var charset = contentType.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"');
        if (json ? !(mime == "application/json" || mime.StartsWith("application/", StringComparison.Ordinal) && mime.EndsWith("+json", StringComparison.Ordinal)) : mime != "application/x-www-form-urlencoded")
            throw new RewriteFailure("RW008", "规则需要的正文格式不受支持。");
        if (charset is not null && !charset.Equals("utf-8", StringComparison.OrdinalIgnoreCase) || input.Headers.Any(h => h.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase) && !h.Value.Equals("identity", StringComparison.OrdinalIgnoreCase)))
            throw new RewriteFailure("RW008", "正文改写仅支持 identity 编码和 UTF-8。");
        CheckDeadline(); if (_body is not null) return _body;
        _lease ??= resources.Acquire();
        _bodyClock ??= System.Diagnostics.Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(RewriteLimits.BodyMilliseconds);
        try
        {
            _body = await bodyReader.ReadAsync(deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            if (_body.Length > RewriteLimits.BodyBytes) throw new RewriteFailure("RW009", "正文超过256 KiB。", 413);
            _ = Utf8.GetString(_body); return _body;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new RewriteFailure("RW009", "正文读取超过两秒截止。", 504); }
    }
    private static void ValidateJson(byte[] bytes, CancellationToken token)
    {
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = DslLimits.JsonDepth });
        var objects = new Stack<HashSet<string>>(); var count = 0;
        while (reader.Read())
        {
            if (++count > DslLimits.JsonTokens) throw new RewriteFailure("RW009", "JSON标记数超过预算。", 413);
            if (count % 128 == 0) token.ThrowIfCancellationRequested();
            if (reader.TokenType == JsonTokenType.StartObject) objects.Push(new(StringComparer.Ordinal));
            else if (reader.TokenType == JsonTokenType.EndObject) objects.Pop();
            else if (reader.TokenType == JsonTokenType.PropertyName && !objects.Peek().Add(reader.GetString()!)) throw new JsonException("重复属性。");
            else if (reader.TokenType == JsonTokenType.Number && !DslNumbers.TryParse(Utf8.GetString(reader.ValueSpan), out _)) throw new JsonException("数字不能无损表示。");
        }
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _json?.Dispose(); _json = null; _body = null;
        _lease?.Dispose(); _lease = null;
    }
}
