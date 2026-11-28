using System.IO;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace AirFlash.App.Metadata;

internal static class PlayerArtworkReader
{
    private const int MaxSourceBytes = 16 * 1024 * 1024;

    // Deliberately synchronous: awaiting a WinRT stream operation resumes the
    // continuation on an STA thread, and SMTC session calls made from that thread
    // fail with RPC_E_WRONG_THREAD (an empty-message COMException). Blocking keeps
    // the read and the caller's follow-up session query on the same MTA thread.
    // The streaming caller runs this on a thread-pool thread; never call it on the
    // UI thread with a real media-session reference.
    public static byte[]? Read(IRandomAccessStreamReference thumbnail, CancellationToken cancellation)
    {
        using var source = thumbnail.OpenReadAsync().AsTask(cancellation).GetAwaiter().GetResult();
        var length = source.Size;
        if (length is 0 or > MaxSourceBytes) return null;
        using var stream = source.AsStreamForRead();
        var data = new byte[(int)length];
        stream.ReadExactly(data.AsSpan());
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
