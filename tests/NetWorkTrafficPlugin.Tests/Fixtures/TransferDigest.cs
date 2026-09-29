using System.Security.Cryptography;

namespace NetWorkTrafficPlugin.Tests.Fixtures;

public sealed record TransferDigest(long Bytes, string Hash)
{
    /// <summary>固定 64 KiB 工作区同时累计长度和摘要，断言自身不能引入全量缓冲。</summary>
    public static async Task<TransferDigest> ReadAsync(Stream stream, CancellationToken token = default)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long length = 0;
        while (await stream.ReadAsync(buffer, token) is var count && count > 0)
        { length += count; digest.AppendData(buffer, 0, count); }
        return new(length, Convert.ToHexString(digest.GetHashAndReset()));
    }
    public static string Expected(long length)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < length; offset += ControlledOrigin.Block.Length)
            hash.AppendData(ControlledOrigin.Block, 0, (int)Math.Min(ControlledOrigin.Block.Length, length - offset));
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
