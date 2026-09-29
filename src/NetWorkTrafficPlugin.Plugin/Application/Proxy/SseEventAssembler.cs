using System.Text;

namespace NetWorkTrafficPlugin.Application.Proxy;

/// <summary>仅组装有界 SSE data 事件，不自动重连。Decoder 保留跨块字符，行解析保留跨块 CRLF。</summary>
public sealed class SseEventAssembler(int maxCharacters = 65536)
{
    private readonly Decoder _decoder = new UTF8Encoding(false, true).GetDecoder();
    private readonly StringBuilder _line = new(), _data = new();
    private bool _skipLf;
    public bool Truncated { get; private set; }
    public bool HasIncompleteEvent => _line.Length != 0 || _data.Length != 0;
    public void Append(ReadOnlySpan<byte> bytes, Action<string> emit)
    {
        if (maxCharacters < 1) throw new ArgumentOutOfRangeException(nameof(maxCharacters));
        Span<char> chars = stackalloc char[1024];
        do
        {
            _decoder.Convert(bytes, chars, false, out var used, out var written, out _);
            foreach (var c in chars[..written])
            {
                if (_skipLf) { _skipLf = false; if (c == '\n') continue; }
                if (c is '\r' or '\n') { ProcessLine(emit); _skipLf = c == '\r'; }
                else if (_line.Length + _data.Length < maxCharacters) _line.Append(c);
                else Truncated = true;
            }
            bytes = bytes[used..];
        } while (!bytes.IsEmpty);
    }
    private void ProcessLine(Action<string> emit)
    {
        if (_line.Length == 0)
        {
            if (_data.Length != 0 && !Truncated) emit(_data.ToString(0, _data.Length - 1));
            _data.Clear();
            Truncated = false;
        }
        else if (!Truncated && _line.ToString() is var line && (line == "data" || line.StartsWith("data:", StringComparison.Ordinal)))
        {
            var value = line.Length > 4 ? line[5..] : "";
            if (value.StartsWith(' ')) value = value[1..];
            if (_data.Length + value.Length + 1 <= maxCharacters) _data.Append(value).Append('\n');
            else Truncated = true;
        }
        _line.Clear();
    }
}
