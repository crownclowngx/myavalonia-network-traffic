using NetWorkTrafficPlugin.Application.Rewrite;
using Titanium.Web.Proxy.EventArguments;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>
/// 仅供 V6-02 验证第三方公开 API，不能注册进生产容器。
/// 固定版本不能在严格 Continue 前产生本地中间响应，取消后还可能在清理阶段等待客户端正文；
/// 保留真实读取任务直至退出，探针不使用 WaitAsync 丢弃任务来伪造按时释放。
/// </summary>
public sealed class TitaniumBodyCapabilityReader(SessionEventArgs session, bool terminateOnCancellation = false) : IRewriteBodyReader
{
    public async ValueTask<byte[]> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var request = session.HttpClient.Request;
        if (request.HttpVersion != System.Net.HttpVersion.Version11)
            throw new RewriteFailure("RW010", "正文改写仅支持 HTTP/1.1。");
        if (request.ContentLength > RewriteLimits.BodyBytes)
            throw new RewriteFailure("RW009", "请求正文超过256 KiB。", 413);
        session.MaxBufferedBodyBytes = RewriteLimits.BodyBytes;
        using var cancellation = terminateOnCancellation ? token.Register(session.TerminateSession) : default;
        var body = await session.GetRequestBody(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        return body;
    }
}
