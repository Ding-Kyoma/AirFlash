using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32.SafeHandles;

namespace AirFlash.App.Services;

// The ICO is embedded separately from the executable icon so development hosts
// and single-file releases load the same frames, without relying on ProcessPath.
internal static class AppIconService
{
    private sealed record Frame(int Size, byte[] Data);
    private static readonly Lazy<Frame[]> Frames = new(ReadFrames);

    internal static NativeIcon Create(int pixels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixels);
        var frames = Frames.Value;
        var frame = frames.FirstOrDefault(frame => frame.Size >= pixels) ?? frames[^1];
        var handle = CreateIconFromResourceEx(frame.Data, (uint)frame.Data.Length, true, 0x00030000, pixels, pixels, 0);
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot load the AirFlash icon.");
        return new NativeIcon(handle);
    }

    private static Frame[] ReadFrames()
    {
        using var stream = typeof(AppIconService).Assembly.GetManifestResourceStream("AirFlash.Icon.ico")
            ?? throw new InvalidDataException("The embedded AirFlash icon is missing.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length < 6 || BitConverter.ToUInt16(bytes, 0) != 0 || BitConverter.ToUInt16(bytes, 2) != 1)
            throw new InvalidDataException("Invalid AirFlash ICO header.");
        var count = BitConverter.ToUInt16(bytes, 4);
        if (count == 0 || bytes.Length < 6 + count * 16) throw new InvalidDataException("Invalid AirFlash ICO directory.");
        var frames = new Frame[count];
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var height = bytes[entry + 1] == 0 ? 256 : bytes[entry + 1];
            var length = BitConverter.ToUInt32(bytes, entry + 8);
            var offset = BitConverter.ToUInt32(bytes, entry + 12);
            if (width != height || length == 0 || offset < 6 + count * 16 || (ulong)offset + length > (ulong)bytes.Length)
                throw new InvalidDataException("Invalid AirFlash ICO frame.");
            frames[i] = new(width, bytes.AsSpan((int)offset, (int)length).ToArray());
        }
        return frames.OrderBy(frame => frame.Size).ToArray();
    }

    internal static int Pixels(uint dpi, bool small) => Math.Max(1, GetSystemMetricsForDpi(small ? 49 : 11, dpi));
    internal static uint WindowDpi(IntPtr window) => window != IntPtr.Zero ? GetDpiForWindow(window) : GetDpiForSystem();
    internal static uint TaskbarDpi() => WindowDpi(FindWindow("Shell_TrayWnd", null));

    internal static void Track(Window window)
    {
        NativeIcon? small = null, large = null;
        var currentDpi = 0u;
        void Refresh()
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            var dpi = WindowDpi(handle);
            if (dpi == currentDpi) return;
            NativeIcon? nextSmall = null, nextLarge = null;
            try
            {
                nextSmall = Create(Pixels(dpi, true)); nextLarge = Create(Pixels(dpi, false));
                SendMessage(handle, 0x0080, IntPtr.Zero, nextSmall.DangerousGetHandle());
                SendMessage(handle, 0x0080, new(1), nextLarge.DangerousGetHandle());
                small?.Dispose(); large?.Dispose();
                small = nextSmall; large = nextLarge; nextSmall = null; nextLarge = null;
                currentDpi = dpi;
            }
            catch (Win32Exception error) { AppPaths.Log($"Window icon refresh failed: {error.Message}"); }
            finally { nextSmall?.Dispose(); nextLarge?.Dispose(); }
        }
        EventHandler initialized = (_, _) => Refresh();
        DpiChangedEventHandler dpiChanged = (_, _) => Refresh();
        EventHandler? closed = null;
        closed = (_, _) =>
        {
            window.SourceInitialized -= initialized; window.DpiChanged -= dpiChanged; window.Closed -= closed;
            small?.Dispose(); large?.Dispose();
        };
        window.SourceInitialized += initialized; window.DpiChanged += dpiChanged; window.Closed += closed;
        Refresh();
    }

    internal sealed class NativeIcon : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal NativeIcon(IntPtr handle) : base(true) => SetHandle(handle);
        protected override bool ReleaseHandle() => DestroyIcon(handle);
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CreateIconFromResourceEx(byte[] data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string? title);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
