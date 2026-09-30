using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NetWorkTrafficPlugin.Application.Analysis;
using NetWorkTrafficPlugin.Application.Dsl;
using NetWorkTrafficPlugin.Features.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Analysis;
using NetWorkTrafficPlugin.Infrastructure.Rules;
using NetWorkTrafficPlugin.Tests.Analysis;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Integration;

/// <summary>
/// V5专项负载只使用合成身份。小型预算试验与桌面持续试验分别测量，
/// 不让测试进程生成正文的内存污染被测桌面进程的私有内存曲线。
/// </summary>
[Trait("Category", "M2ResourceProbe")]
public sealed class M2ResourceProbeTests
{
    [Fact] public async Task P01TenThousandRecordsAndThirtyTwoFields()
    {
        var f = new M2Fixture();
        for (var i = 0; i < 10000; i++) f.Add($"https://example.test/?user={i % 5000}");
        var fields = "field f0=first(original.query[\"user\"]);" +
            string.Concat(Enumerable.Range(1, 31).Select(i => $"field f{i}=f{i - 1};"));
        var text = M2Fixture.Program(fields, group: "tuple(f31,original.host,source,original.method)", columns: "f31");
        var program = M2Fixture.Compile(text);
        var resources = new AnalysisResources(); var engine = new AnalysisQueryEngine(resources);
        var source = f.Source(); var measurements = new List<double>(); var operations = new List<long>();
        for (var round = -2; round < 10; round++)
        {
            var watch = Stopwatch.StartNew();
            using (var result = await engine.RunAsync(source, program, Guid.NewGuid(), AnalysisQueryEngine.Hash(text), new(), default))
            {
                Assert.Equal(5000, result.Groups.Count);
                Assert.All(result.Groups, g => Assert.Equal(2, g.Count));
                Assert.Equal(10000, result.Coverage.GroupedRecords);
                if (round >= 0) { measurements.Add(watch.Elapsed.TotalMilliseconds); operations.Add(result.Operations); }
            }
            Assert.Equal(0, resources.Memory.Reserved);
        }
        var p95 = measurements.Order().ElementAt(9);
        Assert.True(p95 <= 2000, $"元信息分组p95={p95:N1}ms");
        using var profile = JsonDocument.Parse(File.ReadAllText(Path.Combine(ProbeProcess.Repository, "tools/m2-profile.json")));
        foreach (var field in typeof(DslLimits).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
            Assert.Equal(Convert.ToInt64(field.GetRawConstantValue()), profile.RootElement.GetProperty("limits").GetProperty(field.Name).GetInt64());
        M2Fixture.Evidence("performance", "P01", new { records = f.Records.Count, fields = program.Fields.Count, groups = 5000, components = program.GroupKeys.Count == 1 ? 4 : 0,
            warmups = 2, measurements, operations, p95, peak = resources.Memory.Peak, reservedAfter = resources.Memory.Reserved });
    }
    [Fact] public async Task P02BodyBudgetsAndDecompression()
    {
        var f = new M2Fixture(); var body = "{\"user\":\"u\"}" + new string(' ', DslLimits.BodyBytes - 12);
        Assert.Equal(DslLimits.BodyBytes, Encoding.UTF8.GetByteCount(body));
        for (var i = 0; i < 128; i++) f.Add(body: body, contentType: "application/json");
        var text = M2Fixture.Program("field user=toString(original.json(\"$.user\"));");
        var resources = new AnalysisResources();
        long encoded, decoded;
        using (var result = await GroupingTests.Run(f, text, resources))
        {
            encoded = result.EncodedBytes; decoded = result.DecodedBytes;
            Assert.Equal(32L << 20, encoded); Assert.Equal(encoded, decoded);
            Assert.Equal(128, result.Coverage.GroupedRecords);
        }
        f.Add(body: body, contentType: "application/json");
        await Assert.ThrowsAsync<AnalysisLimitException>(() => GroupingTests.Run(f, text, resources));
        var r = f.Records[0];
        using var compressed = new MemoryStream();
        using (var zip = new GZipStream(compressed, CompressionMode.Compress, true)) zip.Write(Encoding.UTF8.GetBytes(body + " "));
        f.Bodies[r.RequestBody.BodyId] = compressed.ToArray();
        r = r with { RequestBody = r.RequestBody with { ContentEncoding = "gzip", CommittedBytes = compressed.Length, ObservedBytes = compressed.Length } };
        Assert.Equal(ValueState.BudgetExceeded, (await f.Value("original.json(\"$.user\")", r)).State);
        // 一个读取器先占住内容额度，再验证另一读取器立即降级而非等待；释放后全部归还。
        using (var held = f.Memory.TryReserve((32L << 20) - 1)!)
            Assert.Equal(ValueState.BudgetExceeded, (await f.Value("original.json(\"$.user\")", r)).State);
        Assert.Equal(0, resources.Memory.Reserved); Assert.Equal(0, f.Memory.Reserved); Assert.Equal(0, f.Leases);
        M2Fixture.Evidence("performance", "P02", new { encoded, decoded, bodyBytes = Encoding.UTF8.GetByteCount(body),
            reads = f.BodyReads, expansionState = "BudgetExceeded", contentPeak = f.Memory.Peak, analysisPeak = resources.Memory.Peak,
            reservedAfter = resources.Memory.Reserved, contentAfter = f.Memory.Reserved, leases = f.Leases });
    }
    [Fact] public async Task P03DesktopCaptureAndAnalysisForTenMinutes()
    {
        var directory = Environment.GetEnvironmentVariable("NETWORK_TRAFFIC_TEST_ARTIFACTS") ?? Path.Combine(ProbeProcess.Repository, "TestResults/M2ResourcePreview");
        Directory.CreateDirectory(directory);
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(ProbeProcess.Repository, "tools/NetWorkTrafficPlugin.UiProbe/bin/Debug/net10.0/NetWorkTrafficPlugin.UiProbe.dll"));
        start.ArgumentList.Add("--m2"); start.Environment["NETWORK_TRAFFIC_TEST_ARTIFACTS"] = directory;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(16)); }
        catch { if (!process.HasExited) process.Kill(true); throw; }
        var log = await stdout + await stderr;
        await File.WriteAllTextAsync(Path.Combine(directory, "m2-desktop.log"), log);
        Assert.True(process.ExitCode == 0, log);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "m2-desktop.json")));
        // 独立子进程避免覆盖同一测试进程的P01/P02字典；原始证据保留并由总门禁再次交叉核对。
        var result = json.RootElement.GetProperty("scenarios").GetProperty("sustained");
        Assert.True(result.GetProperty("seconds").GetDouble() >= 600);
        Assert.True(result.GetProperty("successes").GetInt32() >= 50);
        M2Fixture.Evidence("performance", "P03", result.Clone());
    }
    [Fact] public async Task P04CancellationAndPageLifetime()
    {
        var directory = TemporaryArtifacts.Create(); var f = new M2Fixture(); f.Add();
        var resources = new AnalysisResources(); var measurements = new List<double>();
        try
        {
            for (var i = 0; i < 20; i++)
            {
                using var page = new AnalysisWorkspace(new(resources), new FileRuleLibraryStore(directory));
                page.Editor.Text = M2Fixture.Program(); page.SetSource(f.Source()); await page.ApplyAsync();
                Assert.NotNull(page.Result);
                // 可取消的真实等待端口保证取消发生在租约占用期间，不能把已经完成的Task当取消试验。
                var blocked = new CancellableSource(f.Source());
                page.SetSource(blocked);
                var pending = i % 2 == 0 ? page.ApplyAsync() : page.PreviewAsync();
                await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                var watch = Stopwatch.StartNew(); page.CancelCommand.Execute(null);
                await pending.WaitAsync(TimeSpan.FromSeconds(1)); page.Dispose();
                measurements.Add(watch.Elapsed.TotalMilliseconds);
                Assert.Equal(0, f.Leases); Assert.Equal(0, resources.Active); Assert.Equal(0, resources.Memory.Reserved);
            }
            using (resources.Enter()) using (resources.Enter()) Assert.Throws<AnalysisLimitException>(() => resources.Enter());
            M2Fixture.Evidence("performance", "P04", new { rounds = measurements.Count, measurements, active = resources.Active,
                leases = f.Leases, reservedAfter = resources.Memory.Reserved });
        }
        finally { TemporaryArtifacts.Delete(directory); }
    }
    private sealed class CancellableSource(IAnalysisSource inner) : IAnalysisSource
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Guid InstanceId { get; } = Guid.NewGuid();
        public bool IsArchive => false;
        public async Task<AnalysisSnapshot> FreezeAsync(CancellationToken token)
        {
            using var snapshot = await inner.FreezeAsync(token);
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            throw new InvalidOperationException("取消等待不应正常返回。");
        }
    }
}
