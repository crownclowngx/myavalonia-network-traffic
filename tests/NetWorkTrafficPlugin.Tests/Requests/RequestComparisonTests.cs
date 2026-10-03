using System.Text;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Application.Requests;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestComparisonTests
{
    internal static TrafficRecord Record(byte[] request, byte[] response, string type = "application/json")
    {
        BodyDescriptor Body(byte[] bytes, BodyDirection direction) => new(Guid.NewGuid(), direction, BodyRepresentation.EncodedEntity, bytes.Length, bytes.Length, bytes.Length,
            TransportState.Completed, CaptureState.Complete, null, null, type, null);
        return new(Guid.NewGuid(), Guid.NewGuid(), 1, 1, TrafficSource.Captured, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1, "POST",
            "https://example.test/api", "https://example.test/api", "1.1", "1.1", 200, [], [], Body(request, BodyDirection.Request), Body(response, BodyDirection.Response), false, null);
    }
    [Fact]
    public void 同时比较请求响应且JSON属性无序数组有序数字不损精度()
    {
        byte[] a = "{\"n\":9007199254740993,\"a\":[1,2]}"u8.ToArray(), b = "{\"a\":[2,1],\"n\":9007199254740994}"u8.ToArray();
        var left = Record(a, a); var right = Record(b, b);
        var result = RequestComparison.Compare(left, a, right, b, default, a, b);
        Assert.True(result.Complete); Assert.Equal(6, result.Differences.Count);
        Assert.Contains(result.Differences, d => d.Location == "请求正文/$/n" && d.Before == "9007199254740993");
        var reordered = "{\"a\":[1,2],\"n\":9007199254740993}"u8.ToArray();
        Assert.Empty(RequestComparison.Compare(left, a, Record(a, reordered), reordered, default, a, a).Differences);
    }
    [Theory]
    [InlineData("partial")] [InlineData("metadata")] [InlineData("sensitive")] [InlineData("encoding")] [InlineData("invalid-json")]
    public void 缺口敏感值编码不同和损坏JSON不宣称全文相同(string kind)
    {
        var bytes = Encoding.UTF8.GetBytes(kind == "invalid-json" ? "{" : "{}"); var record = Record(bytes, bytes);
        record = kind switch
        {
            "partial" => record with { ResponseBody = record.ResponseBody with { Capture = CaptureState.Partial } },
            "metadata" => record with { MetadataPartial = true },
            "sensitive" => record with { RequestHeaders = [new("Authorization", "secret")] },
            "encoding" => record with { ResponseBody = record.ResponseBody with { ContentEncoding = "gzip" } },
            _ => record
        };
        var result = RequestComparison.Compare(record, bytes, Record(bytes, bytes), bytes, default, bytes, bytes);
        Assert.False(result.Complete); Assert.DoesNotContain(result.Differences, d => d.Before.Contains("secret", StringComparison.Ordinal));
    }
    [Fact]
    public void 大量差异与长行保持输出有界并响应取消()
    {
        var a = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat(new string('a', 1500), 170)));
        var b = Encoding.UTF8.GetBytes(string.Join('\n', Enumerable.Repeat(new string('b', 1500), 170)));
        var result = RequestComparison.Compare(Record([], a, "text/plain"), a, Record([], b, "text/plain"), b, default, [], []);
        Assert.False(result.Complete); Assert.All(result.Differences, d => Assert.True(d.Before.Length <= 1025 && d.After.Length <= 1025));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => RequestComparison.Compare(Record([], a), a, Record([], b), b, cancelled.Token));
    }
}
