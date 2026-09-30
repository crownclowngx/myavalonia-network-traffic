using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Capture;
using NetWorkTrafficPlugin.Application.Content;
using NetWorkTrafficPlugin.Application.Dsl;

namespace NetWorkTrafficPlugin.Infrastructure.Analysis;

public sealed class CaptureAnalysisSource(ICaptureSession session, ContentMemoryBudget memory) : IAnalysisSource
{
    public Guid InstanceId { get; } = Guid.NewGuid();
    public bool IsArchive => session.IsArchive;
    public async Task<AnalysisSnapshot> FreezeAsync(CancellationToken token)
    {
        if (session.Snapshot().Records.Length > DslLimits.Records) throw new AnalysisLimitException("保留记录超过分析上限。");
        var lease = await session.FreezeAsync(token);
        try
        {
            if (lease.Records.Length > DslLimits.Records) throw new AnalysisLimitException("冻结时记录超过分析上限。");
            // CaptureLease 已拥有冻结的记录数组，头数组在捕获层只替换、不原地修改。
            // 此处只借用租约，避免在分析额度预留前再复制一份全部头信息；分析器不写这些对象。
            var records = lease.Records;
            // 旧存档的 CreatedUtc 是写入完成时间，不能冒充冻结时间。只读窗口稳定定位到最后请求。
            var anchor = session.IsArchive ? records.Length == 0 ? DateTimeOffset.UnixEpoch : records.Max(r => r.StartedUtc).AddTicks(1)
                : lease.FrozenUtc ?? DateTimeOffset.UtcNow;
            return new Snapshot(lease, records, anchor, session, memory);
        }
        catch { lease.Dispose(); throw; }
    }
    private sealed class Snapshot(CaptureLease lease, TrafficRecord[] records, DateTimeOffset anchor, ICaptureSession session, ContentMemoryBudget memory)
        : AnalysisSnapshot(lease.Snapshot, records, anchor)
    {
        public override IFieldSource OpenFields(TrafficRecord record, EvaluationBudget budget) => new RequestFieldReader(record, session, memory, budget, Source(record));
        public override void Dispose() => lease.Dispose();
    }
}
