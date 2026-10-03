using NetWorkTrafficPlugin.Application.Requests;
using NetWorkTrafficPlugin.Infrastructure.Requests;
using NetWorkTrafficPlugin.Tests.Fixtures;
using Xunit;

namespace NetWorkTrafficPlugin.Tests.Requests;

public sealed class RequestExampleTests
{
    [Theory]
    [InlineData(0, "a.example.test")] [InlineData(1, "b.example.test")]
    public async Task 文档样例经真实库校验和离线预览解析(int environment, string host)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NetWorkTrafficPlugin.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory); var temporary = TemporaryArtifacts.Create();
        try
        {
            File.Copy(Path.Combine(directory.FullName, "docs/examples/m4/requests.json"), Path.Combine(temporary, "requests.json"));
            var library = await new FileRequestLibraryStore(temporary).LoadAsync(default);
            using var preview = await new RequestPreparation(new()).PrepareAsync(Assert.Single(library.Templates).Draft, library.Environments[environment], default);
            Assert.Equal(host, preview.Wire.Address.Host); Assert.Contains("tag=%E4%B8%AD%E6%96%87%2B%25&tag=second", preview.Preview);
            Assert.Contains("9007199254740993", preview.BodyPreview); Assert.False(preview.Sensitive);
        }
        finally { TemporaryArtifacts.Delete(temporary); }
    }
}
