using System.Text;
using System.Text.Json;

namespace NetWorkTrafficPlugin.Application.Content;

public sealed record StreamEvent(long Sequence, string Type, string Data, string? Id, int? Retry, string? Error = null);
public sealed record StreamPreview(StreamEvent[] Events, long Total, long Evicted, long Invalid,
    bool Incomplete, bool Gap, string? Fragment);

/// <summary>
/// 每个正文一个有界解析器：增量 UTF-8 保留半字符，换行状态跨块保存。
/// 这里只观察 SSE/NDJSON，不执行重连。事件环同时限制数量与 UTF-8 字节，
/// 超长输入只扫描到下一个边界，不继续增长字符串，也不把截短事件伪装成合法事件。
/// </summary>
public sealed class StreamingContentParser(bool ndjson = false, int maxBytes = 65536, int capacity = 256, int retainedBytes = 262144)
{
    private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
    private readonly StringBuilder _line = new(), _data = new();
    private readonly Queue<StreamEvent> _events = [];
    private bool _skipLf, _first = true, _oversized, _hadData, _gap, _ended, _lineHasCharacters;
    private string _type = "message";
    private string? _id;
    private int? _retry;
    private int _eventBytes, _retainedBytes;
    private long _total, _evicted, _invalid;
    public void Append(ReadOnlySpan<byte> bytes)
    {
        if (_gap || _ended) return;
        Span<char> chars = stackalloc char[1024];
        do
        {
            _decoder.Convert(bytes, chars, false, out var used, out var written, out _);
            foreach (var c in chars[..written]) Character(c);
            bytes = bytes[used..];
        } while (!bytes.IsEmpty);
    }
    private void Character(char c)
    {
        if (_first) { _first = false; if (c == '\uFEFF') return; }
        if (c == '\uFFFD') _invalid++;
        if (_skipLf) { _skipLf = false; if (c == '\n') return; }
        if (c is '\r' or '\n') { Line(); _skipLf = c == '\r'; }
        else
        {
            _lineHasCharacters = true;
            if (_oversized) return;
            _line.Append(c);
            // 最多检查一个有限行；按 UTF-16 硬边界先拦截，行结束再检查精确 UTF-8 字节。
            if (_line.Length + _data.Length > maxBytes) { _oversized = true; _line.Clear(); _data.Clear(); }
        }
    }
    private void Line()
    {
        var line = _line.ToString(); var empty = !_lineHasCharacters; _lineHasCharacters = false; _line.Clear();
        if (ndjson)
        {
            if (_oversized) { _invalid++; _oversized = false; return; }
            if (line.Length != 0) EmitJson(line);
            return;
        }
        if (empty)
        {
            if (_oversized) _invalid++;
            else if (_hadData) Emit(_type, _data.Length > 0 ? _data.ToString(0, _data.Length - 1) : "", null);
            _data.Clear(); _type = "message"; _hadData = false; _eventBytes = 0; _oversized = false;
            return;
        }
        if (_oversized || line[0] == ':') return;
        _eventBytes += Encoding.UTF8.GetByteCount(line);
        if (_eventBytes > maxBytes) { _oversized = true; _data.Clear(); return; }
        var colon = line.IndexOf(':');
        var name = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        switch (name)
        {
            case "data": _data.Append(value).Append('\n'); _hadData = true; break;
            case "event": _type = value.Length == 0 ? "message" : value; break;
            case "id": if (!value.Contains('\0')) _id = value; break;
            case "retry": if (value.Length > 0 && value.All(char.IsAsciiDigit) && int.TryParse(value, out var retry)) _retry = retry; break;
        }
    }
    private void EmitJson(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > maxBytes) { _invalid++; return; }
        try { using var json = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 64 }); Emit("json", line, null); }
        catch (JsonException) { _invalid++; Emit("json", line, "该行不是有效 JSON。"); }
    }
    private void Emit(string type, string data, string? error)
    {
        var item = new StreamEvent(++_total, type, data, _id, _retry, error);
        var size = Cost(item);
        _events.Enqueue(item); _retainedBytes += size;
        while (_events.Count > capacity || _retainedBytes > retainedBytes)
        {
            _retainedBytes -= Cost(_events.Dequeue()); _evicted++;
        }
    }
    private static int Cost(StreamEvent item) => Encoding.UTF8.GetByteCount(item.Data) + Encoding.UTF8.GetByteCount(item.Type) + Encoding.UTF8.GetByteCount(item.Id ?? "") + 128;
    public void MarkGap() => _gap = true;
    public void Complete(bool normal)
    {
        if (_ended) return;
        Span<char> remaining = stackalloc char[4];
        _decoder.Convert([], remaining, true, out _, out var written, out _);
        foreach (var c in remaining[..written]) Character(c);
        if (ndjson && normal && _line.Length > 0) Line();
        _ended = true;
    }
    public StreamPreview Snapshot() => new(_events.ToArray(), _total, _evicted, _invalid,
        _hadData || _line.Length > 0 || _oversized, _gap,
        _line.Length > 0 ? _line.ToString() : _hadData ? _data.ToString() : null);
}
