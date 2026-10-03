using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;

namespace NetWorkTrafficPlugin.Application.Requests;

/// <summary>对比的读入和计算共用两项名额与内容预算，包含文件读取等待；每页多开不会放大容器额度。</summary>
public sealed class RequestComparisonService(ContentMemoryBudget memory)
{
    private readonly SemaphoreSlim _slots = new(2, 2);
    public async Task<RequestComparisonResult> CompareAsync(TrafficRecord before, byte[] beforeBody, byte[] beforeRequest, ICaptureSession source, Guid afterId, CancellationToken token)
    {
        if (!_slots.Wait(0)) throw new RequestFailure("RQ009", "已有两项对比，请稍后重试。");
        try
        {
            using var reservation = memory.TryReserve(RequestLimits.ReservationBytes) ?? throw new RequestFailure("RQ008", "对比共享内容额度不足。");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            using var closing = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            var after = await source.ReadAsync(afterId, closing.Token).ConfigureAwait(false) ?? throw new RequestFailure("RQ014", "结果记录已不可用。");
            var bytes = await source.ReadBodyAsync(after.ResponseBody, RequestLimits.BodyBytes, closing.Token).ConfigureAwait(false);
            var requestBytes = await source.ReadBodyAsync(after.RequestBody, RequestLimits.BodyBytes, closing.Token).ConfigureAwait(false);
            return await Task.Run(() => RequestComparison.Compare(before, beforeBody, after, bytes, closing.Token, beforeRequest, requestBytes), closing.Token).ConfigureAwait(false);
        }
        finally { _slots.Release(); }
    }
}
