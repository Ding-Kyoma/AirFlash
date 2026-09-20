namespace AirFlash.Core;

public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
}
public static class PopupPlacement
{
    public static PixelRect Place(PixelRect work, double dpiScale, double widthDip, double heightDip)
    {
        var margin = Math.Max(1, (int)Math.Round(12 * dpiScale));
        var width = Math.Min((int)Math.Round(widthDip * dpiScale), Math.Max(1, work.Width - 2 * margin));
        var height = Math.Min((int)Math.Round(heightDip * dpiScale), Math.Max(1, (int)(work.Height * 0.7)));
        return new(work.Right - margin - width, work.Bottom - margin - height, work.Right - margin, work.Bottom - margin);
    }
}
