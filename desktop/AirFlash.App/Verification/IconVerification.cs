using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirFlash.App.Services;

namespace AirFlash.App.Verification;

internal static class IconVerification
{
    internal static object Run(Window window, TrayService tray, List<string> checks, string directory)
    {
        var logo = Application.Current.FindResource("AppLogo") as DrawingImage
            ?? throw new InvalidOperationException("The panel logo must be a vector DrawingImage.");
        Check(logo.Drawing.Bounds == new Rect(0, 0, 64, 64), "vector logo preserves the SVG viewBox", checks);
        foreach (var size in new[] { 16, 20, 24, 28, 32, 40, 48, 56, 60, 64, 72, 80, 96, 128, 192, 256, 18 })
        {
            using var icon = AppIconService.Create(size);
            Check(Size(icon.DangerousGetHandle()) == size, $"native ICO decodes at {size}px", checks);
        }
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = AppIconService.WindowDpi(handle);
        Check(Size(SendMessage(handle, 0x007f, IntPtr.Zero, IntPtr.Zero)) == AppIconService.Pixels(dpi, true), "window small icon matches actual DPI", checks);
        Check(Size(SendMessage(handle, 0x007f, new(1), IntPtr.Zero)) == AppIconService.Pixels(dpi, false), "window large/taskbar icon matches actual DPI", checks);
        Check(Size(tray.IconHandle) == tray.IconPixels, "tray native handle matches its selected size", checks);

        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xFB)), null, new Rect(0, 0, 420, 340));
            var row = 0;
            foreach (var scale in new[] { 1d, 1.25, 1.5, 1.75, 2d })
            {
                var testDpi = (uint)(96 * scale);
                var small = AppIconService.Pixels(testDpi, true); var large = AppIconService.Pixels(testDpi, false);
                using var smallIcon = AppIconService.Create(small); using var largeIcon = AppIconService.Create(large);
                Check(Size(smallIcon.DangerousGetHandle()) == small && Size(largeIcon.DangerousGetHandle()) == large,
                    $"native icons at {scale * 100}%: {small}/{large}px", checks);
                var label = new FormattedText($"{scale * 100}%  ·  {small}/{large}px", System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, new Typeface("Segoe UI"), 14, Brushes.Black, 1);
                var y = 12 + row++ * 64;
                drawing.DrawText(label, new Point(12, y + 12));
                drawing.DrawImage(logo, new Rect(190, y, 26 * scale, 26 * scale));
                drawing.DrawImage(Bitmap(smallIcon.DangerousGetHandle()), new Rect(270, y, small, small));
                drawing.DrawImage(Bitmap(largeIcon.DangerousGetHandle()), new Rect(340, y, large, large));
            }
        }
        var image = new RenderTargetBitmap(420, 340, 96, 96, PixelFormats.Pbgra32); image.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using (var output = File.Create(Path.Combine(directory, "icons-dpi.png"))) encoder.Save(output);
        var area = tray.WorkArea();
        return new { window_dpi = dpi, tray_pixels = tray.IconPixels, monitor_scale = area.Scale, monitor_work_area = area.Rect };
    }

    private static BitmapSource Bitmap(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Native icon handle is missing.");
        return Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
    }
    private static int Size(IntPtr handle)
    {
        var bitmap = Bitmap(handle);
        if (bitmap.PixelWidth != bitmap.PixelHeight) throw new InvalidOperationException("Native icon must be square.");
        return bitmap.PixelWidth;
    }
    private static void Check(bool success, string message, List<string> checks)
    {
        if (!success) throw new InvalidOperationException(message);
        checks.Add(message);
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
