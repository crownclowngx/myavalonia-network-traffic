using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using NetWorkTrafficPlugin.Application.Proxy;
using NetWorkTrafficPlugin.Infrastructure.Capture;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Capture;

public sealed class M1StorageTests
{
    [Theory]
    [InlineData("snapshot")]
    [InlineData("body")]
    [InlineData("records")]
    [InlineData("manifest")]
    [InlineData("commit")]
    public async Task 各保存阶段取消不发布目标且释放读取租约(string point)
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("cancel-body"u8.ToArray()); await f.StopAsync();
        using var cancel = new CancellationTokenSource();
        await using var store = new FileCaptureArchiveStore(stage => { if (stage == point) cancel.Cancel(); });
        var target = Path.Combine(f.Root, "cancelled");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SaveAsync(f.Session, target, cancel.Token));
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.GetDirectories(f.Root, ".capture-save-*"));
        f.Session.DeleteStoppedSession(); Assert.False(Directory.Exists(f.Session.DirectoryPath));
    }

    [Theory]
    [InlineData("null-body")]
    [InlineData("duplicate-id")]
    [InlineData("bad-direction")]
    [InlineData("false-complete")]
    [InlineData("unfinished-line")]
    [InlineData("null-header")]
    [InlineData("negative-size")]
    public async Task 通过哈希的恶意元信息仍拒绝加载(string mutation)
    {
        await using var f = new M1SessionFixture(); await f.AddAsync("sample"u8.ToArray()); await f.StopAsync();
        await using var archive = new FileCaptureArchiveStore();
        var saved = await archive.SaveAsync(f.Session, Path.Combine(f.Root, "invalid"), default);
        var recordsPath = Path.Combine(saved.Directory, "records.jsonl"); var manifestPath = Path.Combine(saved.Directory, "manifest.json");
        var record = JsonNode.Parse((await File.ReadAllTextAsync(recordsPath)).Trim())!;
        if (mutation == "null-body") record["RequestBody"] = null;
        if (mutation == "bad-direction") record["RequestBody"]!["Direction"] = 1;
        if (mutation == "false-complete") record["ResponseBody"]!["ObservedBytes"] = 100;
        if (mutation == "null-header") record["ResponseHeaders"]![0] = null;
        if (mutation == "negative-size") record["ResponseBody"]!["CommittedBytes"] = -1;
        var text = record.ToJsonString() + "\n";
        if (mutation == "duplicate-id") text += text;
        if (mutation == "unfinished-line") text = text.TrimEnd();
        await File.WriteAllTextAsync(recordsPath, text);
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(manifestPath))!;
        var bytes = await File.ReadAllBytesAsync(recordsPath);
        manifest = manifest with { Files = manifest.Files.Select(file => file.Path == "records.jsonl" ? new ArchiveFile(file.Path, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes))) : file).ToArray() };
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.OpenAsync(saved.Directory, default));
    }
    [Fact]
    public async Task 混合记录保存重开保持重复头身份完整性并可导出()
    {
        await using var fixture = new M1SessionFixture();
        var first = await fixture.AddAsync("中文正文"u8.ToArray());
        await fixture.AddAsync("{\"value\":1}"u8.ToArray(), type: "application/json");
        await fixture.AddAsync([], mode: CaptureMode.MetadataOnly);
        await fixture.StopAsync();
        await using var archive = new FileCaptureArchiveStore();
        var target = Path.Combine(fixture.Root, "saved");
        var saved = await archive.SaveAsync(fixture.Session, target, default);
        var reopened = await archive.OpenAsync(target, default);
        Assert.True(reopened.IsArchive); Assert.False(reopened.Snapshot().IsRunning); Assert.Equal(3, saved.Records);
        var record = (await reopened.ReadAsync(first.Id, default))!;
        Assert.Equal(3, record.ResponseHeaders.Length);
        Assert.Equal(CaptureState.Complete, record.ResponseBody.Capture);
        var body = await reopened.ReadBodyAsync(record.ResponseBody, 65536, default);
        Assert.Equal("中文正文", Encoding.UTF8.GetString(body));
        var exported = Path.Combine(fixture.Root, "export.bin");
        await archive.ExportBodyAsync(record.ResponseBody, exported, default);
        Assert.Equal(body, await File.ReadAllBytesAsync(exported));
        ProbeEvidence.Write("m1-storage", "roundtrip", new { saved.SnapshotId, saved.Records, totalBodyBytes = saved.BodyBytes,
            source = first.Id, actual = record.RecordId, state = record.ResponseBody.Capture.ToString(), bodyBytes = body.Length,
            reopenedRunning = reopened.Snapshot().IsRunning });
    }
    [Fact]
    public async Task 保存冻结水位不包含冻结后的追加且不等待流结束()
    {
        await using var fixture = new M1SessionFixture();
        var entry = await fixture.AddAsync("first"u8.ToArray(), finish: false, type: "text/event-stream");
        await using var archive = new FileCaptureArchiveStore(stage =>
        {
            if (stage == "snapshot") fixture.Recorder.Observe(entry.Response, "-late"u8);
        });
        var saved = await archive.SaveAsync(fixture.Session, Path.Combine(fixture.Root, "snapshot"), default);
        var opened = await archive.OpenAsync(saved.Directory, default);
        var record = (await opened.ReadAsync(entry.Id, default))!;
        Assert.Equal(5, record.ResponseBody.CommittedBytes);
        Assert.Equal(TransportState.Receiving, record.ResponseBody.Transport); Assert.Equal(CaptureState.Partial, record.ResponseBody.Capture);
        Assert.Equal("first", Encoding.UTF8.GetString(await opened.ReadBodyAsync(record.ResponseBody, 65536, default)));
        ProbeEvidence.Write("m1-storage", "snapshot-watermark", new { frozen = 5, copied = record.ResponseBody.CommittedBytes, saved.BodyBytes, state = record.ResponseBody.Transport.ToString() });
    }
    [Theory]
    [InlineData("snapshot")]
    [InlineData("body")]
    [InlineData("records")]
    [InlineData("manifest")]
    [InlineData("commit")]
    public async Task 各保存阶段失败不会发布半成品或残留租约(string point)
    {
        await using var fixture = new M1SessionFixture(); await fixture.AddAsync("body"u8.ToArray()); await fixture.StopAsync();
        var target = Path.Combine(fixture.Root, "failure");
        await using (var archive = new FileCaptureArchiveStore(stage => { if (stage == point) throw new IOException("合成写入故障"); }))
            await Assert.ThrowsAsync<IOException>(() => archive.SaveAsync(fixture.Session, target, default));
        Assert.False(Directory.Exists(target)); Assert.Empty(Directory.GetDirectories(fixture.Root, ".capture-save-*"));
        // 失败后的下一次保存必须成功，证明信号量和快照租约都已归还。
        await using var retry = new FileCaptureArchiveStore(); await retry.SaveAsync(fixture.Session, target, default);
        await Assert.ThrowsAsync<IOException>(() => retry.SaveAsync(fixture.Session, target, default));
        ProbeEvidence.Write("m1-storage", "failure-" + point, new { point, targetPreserved = Directory.Exists(target), temporaryDirectories = Directory.GetDirectories(fixture.Root, ".capture-save-*").Length });
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/escape")]
    [InlineData("//server/share")]
    [InlineData("bodies/../escape")]
    public void 存档路径不能越过所选根目录(string path) => Assert.Throws<InvalidDataException>(() => FileCaptureArchiveStore.SafePath(Path.GetTempPath(), path, false));
    [Fact]
    public async Task 缺失正文降级未来版本和伪造完整性拒绝()
    {
        await using var fixture = new M1SessionFixture(); var entry = await fixture.AddAsync("body"u8.ToArray()); await fixture.StopAsync();
        await using var archive = new FileCaptureArchiveStore(); var saved = await archive.SaveAsync(fixture.Session, Path.Combine(fixture.Root, "saved"), default);
        var manifestPath = Path.Combine(saved.Directory, "manifest.json");
        var manifest = JsonSerializer.Deserialize<ArchiveManifest>(await File.ReadAllTextAsync(manifestPath))!;
        var bodyPath = manifest.Files.Single(f => f.Path.EndsWith("response.bin")).Path;
        File.Delete(Path.Combine(saved.Directory, bodyPath));
        var reopened = await archive.OpenAsync(saved.Directory, default);
        var body = (await reopened.ReadAsync(entry.Id, default))!.ResponseBody;
        Assert.Equal(CaptureState.Failed, body.Capture); Assert.Null(body.FilePath);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest with { SchemaVersion = 99 }));
        await Assert.ThrowsAsync<InvalidDataException>(() => archive.OpenAsync(saved.Directory, default));
    }
    [Fact]
    public async Task 工作日志恢复保持接收中语义且有租约时拒绝删除()
    {
        await using var fixture = new M1SessionFixture(); var entry = await fixture.AddAsync("partial"u8.ToArray(), finish: false);
        using (await fixture.Session.FreezeAsync(default))
            Assert.Throws<InvalidOperationException>(fixture.Session.DeleteStoppedSession);
        await fixture.StopAsync();
        await using var archive = new FileCaptureArchiveStore(); var recovered = await archive.RecoverAsync(fixture.Session.DirectoryPath, default);
        Assert.True(recovered.IsArchive); Assert.False(recovered.Snapshot().IsRunning);
        Assert.NotEqual(CaptureState.Complete, (await recovered.ReadAsync(entry.Id, default))!.ResponseBody.Capture);
        using (await fixture.Session.FreezeAsync(default)) Assert.Throws<InvalidOperationException>(fixture.Session.DeleteStoppedSession);
        fixture.Session.DeleteStoppedSession(); Assert.False(Directory.Exists(fixture.Session.DirectoryPath));
    }
}
