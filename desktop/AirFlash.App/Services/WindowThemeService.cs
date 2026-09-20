using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
namespace AirFlash.App.Services;

internal static class WindowThemeService
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    public static void Track(Window window)
    {
        EventHandler sourceInitialized = (_, _) => Apply(window, ThemeService.CurrentPalette);
        Action<ThemePalette> themeChanged = palette => Apply(window, palette);
        EventHandler closed = (_, _) =>
        {
            window.SourceInitialized -= sourceInitialized;
            ThemeService.ThemeChanged -= themeChanged;
        };

        window.SourceInitialized += sourceInitialized;
        ThemeService.ThemeChanged += themeChanged;
        window.Closed += closed;
        Apply(window, ThemeService.CurrentPalette);
    }

    public static void Apply(Window window, ThemePalette palette)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            var dark = palette.IsDark ? 1 : 0;
            var caption = ToColorRef(palette.Caption);
            var text = ToColorRef(palette.CaptionText);
            SetAttribute(handle, DwmwaUseImmersiveDarkMode, ref dark);
            SetAttribute(handle, DwmwaCaptionColor, ref caption);
            SetAttribute(handle, DwmwaTextColor, ref text);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private static void SetAttribute(IntPtr handle, int attribute, ref int value) => _ = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
