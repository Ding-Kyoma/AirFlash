using AirFlash.Core;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly HwndSource _source;
    private readonly IntPtr _icon;
    private readonly ControlPanel _panel;
    private readonly AppViewModel _viewModel;
    private readonly Action _settings, _quit;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Guid _guid = new("f3f63f27-a6df-4a27-a942-b448e17dcd12");
    private const uint CallbackMessage = 0x8001;
    private string? _lastTitle;
    public TrayService(ControlPanel panel, AppViewModel viewModel, Action settings, Action quit)
    {
        _panel = panel; _viewModel = viewModel; _settings = settings; _quit = quit;
        _source = new(new HwndSourceParameters("AirFlash.Tray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _source.AddHook(Hook);
        ExtractIconEx(Environment.ProcessPath!, 0, out var large, out _icon, 1);
        if (large != IntPtr.Zero) DestroyIcon(large);
        Add();
        viewModel.PropertyChanged += OnPropertyChanged;
    }
    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AppViewModel.StatusTitle) || _lastTitle == _viewModel.StatusTitle) return;
        _lastTitle = _viewModel.StatusTitle;
        UiPerformance.Count("tray.update");
        var data = Data(); Shell_NotifyIcon(1, ref data);
    }
    private NotifyData Data() => new() { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _source.Handle, Id = 1, Flags = 1 | 2 | 4 | 0x20 | 0x80, Callback = CallbackMessage, Icon = _icon, Tip = $"AirFlash · {_viewModel.StatusTitle}", Guid = _guid, Info = "", InfoTitle = "" };
    private void Add() { var data = Data(); Shell_NotifyIcon(0, ref data); data.Version = 4; Shell_NotifyIcon(4, ref data); }
    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _taskbarCreated) Add();
        if ((uint)message == CallbackMessage)
        {
            var notification = lParam.ToInt64() & 0xffff;
            if (notification is 0x400 or 0x401) _panel.ToggleFromTray();
            if (notification == 0x7b) ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void ShowMenu()
    {
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("TextMenu") };
        void AddItem(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        AddItem(L.Get("Open panel"), _panel.ShowPanel); AddItem(L.Get("Settings…"), _settings);
        menu.Items.Add(new MenuItem { Header = L.Get("Stop streaming"), Command = _viewModel.StopCommand }); menu.Items.Add(new Separator()); AddItem(L.Get("Quit"), _quit);
        _panel.MenuOpen = true; menu.Closed += (_, _) => _panel.MenuOpen = false;
        SetForegroundWindow(_source.Handle); menu.IsOpen = true;
    }
    internal IntPtr WindowHandle => _source.Handle;
    public MonitorArea WorkArea()
    {
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = _source.Handle, Id = 1, Guid = _guid };
        return Shell_NotifyIconGetRect(ref identifier, out var rectangle) == 0 ? NativeWindowPlacement.FromRect(rectangle) : NativeWindowPlacement.CursorWorkArea();
    }
    public void Dispose() { _viewModel.PropertyChanged -= OnPropertyChanged; var data = Data(); Shell_NotifyIcon(2, ref data); _source.Dispose(); if (_icon != IntPtr.Zero) DestroyIcon(_icon); }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IconIdentifier { public uint Size; public IntPtr Window; public uint Id; public Guid Guid; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref NotifyData data);
    [DllImport("shell32.dll")] private static extern int Shell_NotifyIconGetRect(ref IconIdentifier identifier, out NativeWindowPlacement.Rectangle rectangle);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
