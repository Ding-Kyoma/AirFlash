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
        if (!WindowsCapabilities.Current.WindowColors) return;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        try
        {
            ApplyAttributes(WindowsCapabilities.Current, palette, (attribute, value) =>
                DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int)), AppPaths.Log);
        }
        catch (DllNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); }
        catch (EntryPointNotFoundException error) { AppPaths.Log($"Optional DWM unavailable: {error.Message}"); }
    }

    internal static void ApplyAttributes(WindowsCapabilities capabilities, ThemePalette palette, Func<int, int, int> set, Action<string> log)
    {
        if (!capabilities.WindowColors) return;
        foreach (var (attribute, value) in new[] { (DwmwaUseImmersiveDarkMode, palette.IsDark ? 1 : 0),
            (DwmwaCaptionColor, ToColorRef(palette.Caption)), (DwmwaTextColor, ToColorRef(palette.CaptionText)) })
        {
            var result = set(attribute, value);
            if (result < 0) log($"Optional DWM attribute={attribute} failed hresult=0x{result:X8}");
        }
    }

    private static int ToColorRef(Color color) => color.R | color.G << 8 | color.B << 16;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);
}
