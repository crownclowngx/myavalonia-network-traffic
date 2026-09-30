using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Proxy;

namespace NetWorkTrafficPlugin.Features.Capture;

/// <summary>
/// 筛选草稿的纯值对象。校验成功才生成查询，失败保留页面最后有效结果。
/// UI 负责互斥选择；这里仍校验完整输入，防止直接调用绕过状态码和时间边界。
/// </summary>
public sealed record CaptureFilterInput(string Keyword, string Methods, int StatusClass, string StatusCode,
    int TransportIndex, string From, string To, long AfterSequence)
{
    public CaptureQuery Build()
    {
        DateTimeOffset? Time(string text) => string.IsNullOrWhiteSpace(text) ? null :
            DateTimeOffset.TryParse(text, out var value) ? value : throw new ArgumentException("时间格式无效，请输入日期时间或留空。");
        int? code = string.IsNullOrWhiteSpace(StatusCode) ? null :
            int.TryParse(StatusCode, out var number) && number is >= 100 and <= 599 ? number :
            throw new ArgumentException("精确状态码应为 100～599。");
        if (TransportIndex is < 0 or > 2) throw new ArgumentException("传输状态无效。");
        var methods = Methods.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.ToUpperInvariant()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var query = new CaptureQuery(Keyword, methods, code is null && StatusClass > 0 ? StatusClass : null,
            TransportIndex switch { 1 => TransportState.Receiving, 2 => TransportState.Failed, _ => null },
            Time(From), Time(To), AfterSequence, StatusCode: code);
        query.Validate();
        return query;
    }
}

public sealed record FilterChip(string Key, string Label);
