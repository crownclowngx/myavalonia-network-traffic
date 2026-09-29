using Avalonia.Media.Imaging;
using NetWorkTrafficPlugin.Application.Content;

namespace NetWorkTrafficPlugin.Infrastructure.Content;

public sealed class ImagePreview(Bitmap bitmap, IDisposable reservation) : IDisposable
{
    public Bitmap Bitmap { get; } = bitmap;
    private int _disposed;
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) { Bitmap.Dispose(); reservation.Dispose(); } }
}

/// <summary>解码器共享并发名额。等待/取消均不创建额外解码器，旧位图由页面租约释放。</summary>
public sealed class BoundedImageDecoder(ContentMemoryBudget budget)
{
    private readonly SemaphoreSlim _decoder = new(1, 1);
    private int _pending;
    public async Task<ImagePreview> DecodeAsync(byte[] bytes, CancellationToken token)
    {
        var size = ImageHeader.Read(bytes);
        if (Interlocked.Increment(ref _pending) > 3) { Interlocked.Decrement(ref _pending); throw new InvalidOperationException("图片解码队列已满。"); }
        var entered = false;
        try
        {
            await _decoder.WaitAsync(token).ConfigureAwait(false); entered = true;
            var reservation = budget.TryReserve((long)size.Width * size.Height * 4 + (1 << 20)) ?? throw new InvalidOperationException("图片内存预算不足。");
            try
            {
                var bitmap = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(bytes, false);
                    return size.Width >= size.Height ? Bitmap.DecodeToWidth(stream, Math.Min(512, size.Width)) : Bitmap.DecodeToHeight(stream, Math.Min(512, size.Height));
                }, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) { bitmap.Dispose(); token.ThrowIfCancellationRequested(); }
                return new(bitmap, reservation);
            }
            catch { reservation.Dispose(); throw; }
        }
        finally { if (entered) _decoder.Release(); Interlocked.Decrement(ref _pending); }
    }
}
