using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Application.Content;

public sealed record ContentPreview(string Kind, string Text, string? Diagnostic, bool Truncated,
    StreamPreview? Stream = null, byte[]? ImageBytes = null, string? JsonTree = null);

/// <summary>
/// 只解析调用者提供的有限字节。原始文件不在这里修改；解码/JSON 失败只降低查看能力，
/// 不影响已经完成的转发与记录完整性。格式分派使用普通分支，不建设查看器框架。
/// </summary>
public static class ContentPreviewReader
{
    public const int MaximumDecodedBytes = 1 << 20;
    public static ContentPreview Parse(BodyDescriptor body, byte[] input, bool prefixTruncated = false)
    {
        var mime = body.ContentType?.Split(';')[0].Trim().ToLowerInvariant() ?? "application/octet-stream";
        var incomplete = body.Capture != CaptureState.Complete || prefixTruncated || input.LongLength < body.ObservedBytes;
        var bytes = input;
        try
        {
            if (body.Representation == BodyRepresentation.Unknown) return new("binary", Hex(input), "无法确认字节表示，保留有限原始预览。", true);
            if (body.Representation == BodyRepresentation.EncodedEntity && body.ContentEncoding is { Length: > 0 } encoding && encoding != "identity")
                bytes = Decompress(input, encoding);
            if (mime is "image/png" or "image/jpeg")
            {
                if (incomplete) return new("image", "图片记录尚不完整。", null, true);
                var size = ImageHeader.Read(bytes);
                return new("image", $"{size.Width} × {size.Height}", null, false, ImageBytes: bytes);
            }
            if (mime == "text/event-stream" || mime is "application/x-ndjson" or "application/ndjson")
            {
                var parser = new StreamingContentParser(mime != "text/event-stream"); parser.Append(bytes);
                if (body.Transport != TransportState.Receiving) parser.Complete(!incomplete && body.Transport == TransportState.Completed);
                return new("stream", "", incomplete ? "仅解析已记录范围；实时未落盘的尾部不可恢复。" : null, incomplete, parser.Snapshot());
            }
            if (!(mime.StartsWith("text/", StringComparison.Ordinal) || mime.Contains("json", StringComparison.Ordinal) || mime.Contains("xml", StringComparison.Ordinal) || mime.Contains("x-www-form-urlencoded", StringComparison.Ordinal)))
                return new("binary", Hex(bytes), "二进制仅提供有限十六进制预览，不自动播放或下载。", incomplete || bytes.Length > 4096);
            var (text, diagnostic) = Decode(bytes, body.ContentType);
            if (mime.Contains("json", StringComparison.Ordinal) && !incomplete)
            {
                try
                {
                    using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 64 });
                    var nodes = 0;
                    CountNodes(document.RootElement, ref nodes);
                    using var output = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })) document.WriteTo(writer);
                    if (output.Length > MaximumDecodedBytes) throw new InvalidDataException("JSON 格式化输出超过 1 MiB。");
                    return new("json", text, diagnostic, false, JsonTree: Encoding.UTF8.GetString(output.ToArray()));
                }
                catch (Exception ex) when (ex is JsonException or InvalidDataException)
                { return new("text", Limit(text), "JSON 无效或超出深度/节点/输出预算，显示有限原文。", Encoding.UTF8.GetByteCount(text) > 65536); }
            }
            // 预览预算按 UTF-8 字节计算；中文不足 64K 字符也可能已经截断，标记必须与实际输出一致。
            var truncated = incomplete || Encoding.UTF8.GetByteCount(text) > 65536;
            return new("text", Limit(text), truncated ? "正文或预览仅为连续前缀。" : diagnostic, truncated);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException)
        { return new("binary", Hex(input), $"内容解码失败或超限（{ex.GetType().Name}），原记录不变。", true); }
    }
    private static void CountNodes(JsonElement value, ref int count)
    {
        if (++count > 10000) throw new InvalidDataException("JSON 节点超过 10000。");
        if (value.ValueKind == JsonValueKind.Object) foreach (var property in value.EnumerateObject()) CountNodes(property.Value, ref count);
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) CountNodes(item, ref count);
    }
    public static byte[] Decompress(byte[] bytes, string encoding)
    {
        using var source = new MemoryStream(bytes, false);
        using Stream decoder = encoding.Trim().ToLowerInvariant() switch
        {
            "gzip" => new GZipStream(source, CompressionMode.Decompress),
            "br" => new BrotliStream(source, CompressionMode.Decompress),
            _ => throw new InvalidDataException("暂不支持该内容编码。")
        };
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = decoder.Read(buffer)) != 0)
        {
            var length = output.Length + read;
            if (length > MaximumDecodedBytes || length > 65536 && length > Math.Max(1, bytes.Length) * 100L)
                throw new InvalidDataException("解压输出或膨胀比超过预算。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
    public static (string Text, string? Diagnostic) Decode(byte[] bytes, string? contentType)
    {
        var charset = contentType?.Split(';').Select(p => p.Trim()).FirstOrDefault(p => p.StartsWith("charset=", StringComparison.OrdinalIgnoreCase))?[8..].Trim('"', '\'').ToLowerInvariant();
        var bom = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? "utf-8" :
            bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE }) ? "utf-16" : bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF }) ? "utf-16be" : null;
        var selected = charset ?? bom ?? "utf-8";
        var encoding = selected switch
        {
            "utf-8" or "utf8" => Encoding.UTF8, "utf-16" or "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode, "us-ascii" or "ascii" => Encoding.ASCII,
            _ => Encoding.UTF8
        };
        var text = encoding.GetString(bytes).TrimStart('\uFEFF');
        var diagnostic = charset is null && bom is null ? "未声明编码，使用 UTF-8。" :
            bom is not null && charset is not null && charset != bom ? "charset 与 BOM 不同，按声明编码显示。" :
            selected is not ("utf-8" or "utf8" or "utf-16" or "utf-16le" or "utf-16be" or "us-ascii" or "ascii") ? "不支持声明编码，使用 UTF-8 预览。" : null;
        if (text.Contains('\uFFFD')) diagnostic = "内容包含非法或未完成字符，显示替换字符。";
        return (text, diagnostic);
    }
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 4096)));
    private static string Limit(string text)
    {
        if (Encoding.UTF8.GetByteCount(text) <= 65536) return text;
        var low = 0; var high = Math.Min(text.Length, 65536);
        while (low < high) { var middle = (low + high + 1) / 2; if (Encoding.UTF8.GetByteCount(text.AsSpan(0, middle)) <= 65536) low = middle; else high = middle - 1; }
        if (low > 0 && char.IsHighSurrogate(text[low - 1])) low--;
        return text[..low];
    }
}

/// <summary>在任何位图像素分配前读取格式头；只允许有限 PNG/JPEG，头部未知时拒绝解码。</summary>
public static class ImageHeader
{
    public static (int Width, int Height) Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > 4 << 20) throw new InvalidDataException("图片编码数据超过 4 MiB。");
        int width = 0, height = 0;
        if (bytes.Length >= 24 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[16..]);
            height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes[20..]);
        }
        else if (bytes.Length > 4 && bytes[0] == 0xff && bytes[1] == 0xd8)
        {
            for (var index = 2; index + 3 < bytes.Length;)
            {
                if (bytes[index++] != 0xff) throw new InvalidDataException("JPEG 标记无效。");
                while (index < bytes.Length && bytes[index] == 0xff) index++;
                if (index >= bytes.Length) break;
                var marker = bytes[index++];
                if (marker is 0xd9 or 0xda) break;
                if (marker == 0x01 || marker is >= 0xd0 and <= 0xd7) continue;
                if (index + 2 > bytes.Length) break;
                var length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[index..]);
                if (length < 2 || index + length > bytes.Length) throw new InvalidDataException("JPEG 头被截断。");
                if (marker is 0xc0 or 0xc1 or 0xc2 && length >= 8)
                {
                    height = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 3)..]);
                    width = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes[(index + 5)..]); break;
                }
                index += length;
            }
        }
        if (width <= 0 || height <= 0 || width > 8192 || height > 8192 || (long)width * height > 4000000)
            throw new InvalidDataException("图片尺寸未知或超过预览预算。");
        return (width, height);
    }
}
