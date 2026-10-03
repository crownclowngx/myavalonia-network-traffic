using System.Text;

namespace NetWorkTrafficPlugin.Application.Dsl;

/// <summary>两种字段源共用严格百分号解码。Query 的加号是字面量，只有表单按协议将其视为空格。</summary>
public static class RequestFieldText
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public static string Decode(string text, bool form)
    {
        using var bytes = new MemoryStream();
        for (var i = 0; i < text.Length;)
        {
            if (text[i] == '%')
            {
                if (i + 2 >= text.Length || !byte.TryParse(text.AsSpan(i + 1, 2), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var b)) throw new FormatException("百分号编码错误。");
                bytes.WriteByte(b); i += 3;
            }
            else
            {
                var start = i; while (i < text.Length && text[i] != '%') i++;
                bytes.Write(StrictUtf8.GetBytes(form ? text[start..i].Replace('+', ' ') : text[start..i]));
            }
        }
        return StrictUtf8.GetString(bytes.GetBuffer(), 0, checked((int)bytes.Length));
    }
}
