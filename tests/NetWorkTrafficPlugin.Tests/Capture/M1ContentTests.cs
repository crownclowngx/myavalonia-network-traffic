using System.Buffers.Binary;
using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Proxy;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Capture;

public sealed class M1ContentTests
{
    [Fact]
    public void 查询保留重复空值加号及错误转义()
    {
        var fields = HttpFieldParser.Query("https://example.test/p?a=1&a=2&empty=&flag&plus=+&cn=%E4%B8%AD&broken=%Q1");
        Assert.Equal(new[] { "a", "a", "empty", "flag", "plus", "cn", "broken" }, fields.Select(f => f.Name));
        Assert.Equal("", fields[2].Value); Assert.Null(fields[3].Value); Assert.Equal("+", fields[4].Value);
        Assert.Equal("中", fields[5].Value); Assert.Equal("%Q1", fields[6].Value); Assert.NotNull(fields[6].Diagnostic);
    }
    [Fact]
    public void 多值头和SetCookie日期不会被错误拆分且复制默认脱敏()
    {
        HeaderField[] headers = [new("Cookie", "a=1; a=2"), new("Set-Cookie", "x=1; Expires=Wed, 21 Oct 2030 07:28:00 GMT"), new("Set-Cookie", "y=2"), new("Authorization", "synthetic-secret")];
        var cookies = HttpFieldParser.Cookies(headers);
        Assert.Equal(4, cookies.Length); Assert.Contains("Wed, 21", cookies[2].Value);
        Assert.DoesNotContain("synthetic-secret", HttpFieldParser.Display(headers));
        Assert.Contains("synthetic-secret", HttpFieldParser.Display(headers, true));
        Assert.DoesNotContain("/", HttpFieldParser.SafeFileName("../../a.bin"));
        Assert.Equal("body.bin", HttpFieldParser.SafeFileName("CON"));
    }
    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("\r\n")]
    public void SSE所有字节切分位置保留UTF8和事件字段(string newline)
    {
        var bytes = Encoding.UTF8.GetBytes("\uFEFF: heartbeat" + newline + "id: item-1" + newline + "event: update" + newline + "retry: 123" + newline + "data: 中文😀" + newline + "data: second" + newline + newline);
        for (var split = 0; split <= bytes.Length; split++)
        {
            var parser = new StreamingContentParser(); parser.Append(bytes.AsSpan(0, split)); parser.Append(bytes.AsSpan(split)); parser.Complete(true);
            var item = Assert.Single(parser.Snapshot().Events);
            Assert.Equal("中文😀\nsecond", item.Data); Assert.Equal("update", item.Type); Assert.Equal("item-1", item.Id); Assert.Equal(123, item.Retry);
            Assert.False(parser.Snapshot().Incomplete);
        }
    }
    [Fact]
    public void SSE半事件不派发空data合法且超长事件不会假装完整()
    {
        var parser = new StreamingContentParser(maxBytes: 20);
        parser.Append(Encoding.UTF8.GetBytes("data:" + new string('x', 40) + "\n\ndata:\n\ndata: tail"));
        parser.Complete(true); var snapshot = parser.Snapshot();
        Assert.Equal("", Assert.Single(snapshot.Events).Data); Assert.True(snapshot.Incomplete); Assert.Equal(1, snapshot.Invalid);
    }
    [Fact]
    public void 事件环有数量和字节双上限且丢字节停止连续解析()
    {
        var parser = new StreamingContentParser(capacity: 2, retainedBytes: 400);
        for (var i = 0; i < 10; i++) parser.Append(Encoding.UTF8.GetBytes($"data: {i}\n\n"));
        Assert.Equal(10, parser.Snapshot().Total); Assert.Equal(8, parser.Snapshot().Evicted);
        parser.MarkGap(); parser.Append("data: unseen\n\n"u8);
        Assert.Equal(10, parser.Snapshot().Total); Assert.True(parser.Snapshot().Gap);
    }
    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    public void NDJSON坏行保留且末行根据终态区别处理(bool normal, int expected)
    {
        var parser = new StreamingContentParser(true);
        parser.Append(Encoding.UTF8.GetBytes("\n{\"a\":1}\r\nbad\n{\"中文\":2}")); parser.Complete(normal);
        Assert.Equal(expected, parser.Snapshot().Events.Length); Assert.Equal(1, parser.Snapshot().Invalid);
        Assert.Equal(!normal, parser.Snapshot().Incomplete);
    }
    [Fact]
    public void 文本编码与JSON预算不改变保存状态()
    {
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("中文")).ToArray();
        Assert.Equal("中文", ContentPreviewReader.Decode(bytes, "text/plain").Text);
        var body = Complete("application/json", 7);
        var preview = ContentPreviewReader.Parse(body, "{\"a\":1}"u8.ToArray());
        Assert.Equal("json", preview.Kind); Assert.Contains("\"a\"", preview.JsonTree);
        var partial = ContentPreviewReader.Parse(body with { Capture = CaptureState.Partial }, "{\"a\":1}"u8.ToArray());
        Assert.Null(partial.JsonTree); Assert.True(partial.Truncated);
        var deep = Encoding.UTF8.GetBytes(new string('[', 70) + "1" + new string(']', 70));
        Assert.Null(ContentPreviewReader.Parse(Complete("application/json", deep.Length), deep).JsonTree);
        var chinese = Encoding.UTF8.GetBytes(new string('中', 30000));
        var limited = ContentPreviewReader.Parse(Complete("text/plain", chinese.Length), chinese);
        Assert.True(limited.Truncated); Assert.True(Encoding.UTF8.GetByteCount(limited.Text) <= 65536);
        Assert.DoesNotContain('\uFFFD', limited.Text);
    }
    [Theory]
    [InlineData(512, 512, true)]
    [InlineData(10000, 1, false)]
    [InlineData(4000, 4000, false)]
    [InlineData(-1, 12, false)]
    public void PNG在像素分配前拒绝超预算尺寸(int width, int height, bool valid)
    {
        var bytes = new byte[24]; new byte[] { 137,80,78,71,13,10,26,10 }.CopyTo(bytes, 0); "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        if (valid) Assert.Equal((width, height), ImageHeader.Read(bytes)); else Assert.Throws<InvalidDataException>(() => ImageHeader.Read(bytes));
    }
    [Fact]
    public void 压缩膨胀和未知编码降级且已解码实体不再解压()
    {
        using var output = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(output, System.IO.Compression.CompressionLevel.SmallestSize, true)) gzip.Write(new byte[2 << 20]);
        Assert.Throws<InvalidDataException>(() => ContentPreviewReader.Decompress(output.ToArray(), "gzip"));
        var body = Complete("text/plain", 3) with { ContentEncoding = "gzip", Representation = BodyRepresentation.DecodedEntity };
        Assert.Equal("abc", ContentPreviewReader.Parse(body, "abc"u8.ToArray()).Text);
        Assert.NotNull(ContentPreviewReader.Parse(body with { Representation = BodyRepresentation.EncodedEntity, ContentEncoding = "unknown" }, "abc"u8.ToArray()).Diagnostic);
    }
    private static BodyDescriptor Complete(string type, int bytes) => new(Guid.NewGuid(), BodyDirection.Response, BodyRepresentation.EncodedEntity,
        bytes, bytes, bytes, TransportState.Completed, CaptureState.Complete, null, null, type, null);
}
