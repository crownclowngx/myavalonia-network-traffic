using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestLibraryTests
{
    [Fact]
    public async Task 模板保存未解析输入且敏感变量不落盘()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRequestLibraryStore(root);
            var draft = RequestPreparationTests.Draft("${baseUrl}/api?token=${secret}") with
            { Headers = [new("Cookie", "actual-cookie")], Variables = [new("secret", "SENSITIVE_SENTINEL", true), new("ordinary", "value")] };
            var template = new RequestTemplate(Guid.NewGuid(), 1, "接口", RequestTemplateSanitizer.Sanitize(draft));
            var saved = await store.CommitAsync(RequestLibrary.Empty with { Templates = [template] }, 0, default);
            Assert.Equal(1, saved.Revision); var text = await File.ReadAllTextAsync(Path.Combine(root, "requests.json"));
            Assert.DoesNotContain("SENSITIVE_SENTINEL", text); Assert.DoesNotContain("actual-cookie", text);
            var loaded = await new FileRequestLibraryStore(root).LoadAsync(default);
            Assert.Equal("${baseUrl}/api?token=${secret}", Assert.Single(loaded.Templates).Draft.Address);
            Assert.Null(loaded.Templates[0].Draft.Origin);
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Fact]
    public async Task 两页修订冲突与临时写入失败保留旧库()
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var store = new FileRequestLibraryStore(root); var saved = await store.CommitAsync(RequestLibrary.Empty, 0, default);
            await Assert.ThrowsAsync<RequestFailure>(() => new FileRequestLibraryStore(root).CommitAsync(RequestLibrary.Empty, 0, default));
            var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "requests.json"));
            var failing = new FileRequestLibraryStore(root, _ => throw new IOException("synthetic"));
            await Assert.ThrowsAsync<IOException>(() => failing.CommitAsync(saved, 1, default));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(Path.Combine(root, "requests.json")));
            Assert.Empty(Directory.EnumerateFiles(root, "*.tmp"));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
    [Theory]
    [InlineData("broken")] [InlineData("{\"SchemaVersion\":999,\"Revision\":0,\"Templates\":[],\"Environments\":[]}")]
    public async Task 损坏与未来库不会被空保存覆盖(string content)
    {
        var root = TemporaryArtifacts.Create();
        try
        {
            var path = Path.Combine(root, "requests.json"); await File.WriteAllTextAsync(path, content);
            var store = new FileRequestLibraryStore(root);
            await Assert.ThrowsAsync<RequestFailure>(() => store.LoadAsync(default));
            await Assert.ThrowsAsync<RequestFailure>(() => store.CommitAsync(RequestLibrary.Empty, 0, default));
            Assert.Equal(content, await File.ReadAllTextAsync(path));
        }
        finally { TemporaryArtifacts.Delete(root); }
    }
}
