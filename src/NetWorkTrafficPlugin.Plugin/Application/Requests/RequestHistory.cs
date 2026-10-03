using NetWorkTrafficPlugin.Application.Capture;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>历史只保存脱敏执行事实，不含Header、正文、敏感变量、可消费令牌或自动恢复资格。</summary>
public sealed record RequestHistoryEntry(Guid AttemptId, DateTimeOffset StartedUtc, string Method, string Address,
    TrafficSource Source, RequestOrigin? Origin, Guid? EnvironmentId, long EnvironmentRevision, RequestExecutionSnapshot Result)
{
    public override string ToString() => $"{StartedUtc.LocalDateTime:MM-dd HH:mm:ss} {Source} {Method} {Address} · {Result.State} · {Result.StatusCode?.ToString() ?? "无状态码"}";
}
public interface IRequestHistoryStore
{
    Task WriteAsync(RequestHistoryEntry entry, CancellationToken token);
    Task<IReadOnlyList<RequestHistoryEntry>> ReadAsync(CancellationToken token);
}
