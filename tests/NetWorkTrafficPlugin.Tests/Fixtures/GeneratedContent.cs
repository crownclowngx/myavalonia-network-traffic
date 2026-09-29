using System.Net;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

/// <summary>固定块生成上传正文；长度声明与 chunked 使用同一字节序列，内存与上传大小无关。</summary>
public sealed class GeneratedContent(long length, bool knownLength, TaskCompletionSource? first = null, Task? release = null) : HttpContent
{
    protected override bool TryComputeLength(out long computed) { computed = length; return knownLength; }
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => WriteAsync(stream, default);
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => WriteAsync(stream, token);
    private async Task WriteAsync(Stream stream, CancellationToken token)
    {
        for (long offset = 0; offset < length; offset += ControlledOrigin.Block.Length)
        {
            await stream.WriteAsync(ControlledOrigin.Block.AsMemory(0, (int)Math.Min(ControlledOrigin.Block.Length, length - offset)), token);
            if (offset == 0 && first is not null)
            {
                await stream.FlushAsync(token); first.TrySetResult();
                if (release is not null) await release.WaitAsync(token);
            }
        }
    }
}
