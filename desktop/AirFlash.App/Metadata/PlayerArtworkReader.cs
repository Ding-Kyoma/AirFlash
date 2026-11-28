using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace AirFlash.App.Metadata;

internal static class PlayerArtworkReader
{
    private const int MaxSourceBytes = 16 * 1024 * 1024;

    public static async Task<byte[]?> ReadAsync(IRandomAccessStreamReference thumbnail, CancellationToken cancellation)
    {
        using var source = await thumbnail.OpenReadAsync().AsTask(cancellation).ConfigureAwait(false);
        var length = source.Size;
        if (length is 0 or > MaxSourceBytes) return null;
        using var stream = source.AsStreamForRead();
        var data = new byte[(int)length];
        await stream.ReadExactlyAsync(data, cancellation).ConfigureAwait(false);
        cancellation.ThrowIfCancellationRequested();
        return Encode(data);
    }

    internal static byte[]? Encode(byte[] source)
    {
        using var input = new MemoryStream(source, writable: false);
        var info = BitmapFrame.Create(input, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        int width = info.PixelWidth, height = info.PixelHeight;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000) return null;
        foreach (int edge in new[] { 256, 160 })
        {
            input.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (width >= height) image.DecodePixelWidth = Math.Min(edge, width);
            else image.DecodePixelHeight = Math.Min(edge, height);
            image.StreamSource = input;
            image.EndInit();
            var encoder = new JpegBitmapEncoder { QualityLevel = 80 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using var output = new MemoryStream();
            encoder.Save(output);
            if (output.Length <= AirFlash.Core.NowPlaying.MaxArtworkBytes) return output.ToArray();
        }
        return null;
    }
}
