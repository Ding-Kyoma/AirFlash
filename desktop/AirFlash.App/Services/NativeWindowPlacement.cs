using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AirFlash.Core;
namespace AirFlash.App.Services;

public sealed record MonitorArea(PixelRect Rect, double Scale);
public static class NativeWindowPlacement
{
    public static MonitorArea CursorWorkArea() { GetCursorPos(out var point); return Area(MonitorFromPoint(point, 2)); }
    public static MonitorArea FromRect(Rectangle rectangle) => Area(MonitorFromRect(ref rectangle, 2));
    private static MonitorArea Area(IntPtr monitor)
    {
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() }; GetMonitorInfo(monitor, ref info);
        var status = GetDpiForMonitor(monitor, 0, out var x, out _);
        return new(new(info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom), status == 0 && x > 0 ? x / 96d : 1);
    }
    public static void Place(Window window, MonitorArea area, double width, double height)
    {
        var position = PopupPlacement.Place(area.Rect, area.Scale, width, height);
        SetWindowPos(new WindowInteropHelper(window).Handle, new(-1), position.Left, position.Top, position.Width, position.Height, 0x0010);
    }
    public static void CenterSettings(Window window)
    {
        var area = CursorWorkArea(); var margin = 24 * area.Scale;
        window.Width = Math.Min(880, (area.Rect.Width - margin) / area.Scale);
        window.Height = Math.Min(640, (area.Rect.Height - margin) / area.Scale);
        window.MinWidth = Math.Min(640, window.Width); window.MinHeight = Math.Min(440, window.Height);
        var w = (int)(window.Width * area.Scale); var h = (int)(window.Height * area.Scale);
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, area.Rect.Left + (area.Rect.Width - w) / 2, area.Rect.Top + (area.Rect.Height - h) / 2, w, h, 0x0014);
    }
    [StructLayout(LayoutKind.Sequential)] public struct Rectangle { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rectangle Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromRect(ref Rectangle rectangle, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
