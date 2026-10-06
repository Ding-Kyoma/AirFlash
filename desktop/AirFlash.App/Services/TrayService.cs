using AirFlash.Core;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly HwndSource _source;
    private AppIconService.NativeIcon _icon;
    private int _iconPixels;
    private bool _refreshQueued;
    private readonly TrayRegistration _registration;
    private readonly DispatcherTimer _retry;
    private readonly ControlPanel _panel;
    private readonly AppViewModel _viewModel;
    private readonly Action _settings, _quit;
    private readonly uint _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly Guid _guid;
    private static readonly Guid ProductionGuid = new("f3f63f27-a6df-4a27-a942-b448e17dcd12");
    private const uint CallbackMessage = 0x8001;
    private string? _lastTitle;
    private bool _disposed;
    internal ContextMenu? ActiveMenu { get; private set; }
    public TrayService(ControlPanel panel, AppViewModel viewModel, Action settings, Action quit)
        : this(panel, viewModel, settings, quit, ProductionGuid) { }
    internal TrayService(ControlPanel panel, AppViewModel viewModel, Action settings, Action quit, Guid guid, bool windowIdentityForVerification = false)
    {
        _panel = panel; _viewModel = viewModel; _settings = settings; _quit = quit; _guid = guid;
        _source = new(new HwndSourceParameters("AirFlash.Tray") { WindowStyle = unchecked((int)0x80000000), Width = 0, Height = 0 });
        _iconPixels = AppIconService.Pixels(AppIconService.TaskbarDpi(), true);
        try { _icon = LoadTrayIcon(_iconPixels); }
        catch { _source.Dispose(); throw; }
        _registration = new(Notify, Log, initialIdentity: windowIdentityForVerification ? TrayIdentity.WindowIconId : null);
        _retry = new(DispatcherPriority.Background, _source.Dispatcher) { Interval = TimeSpan.FromSeconds(2) };
        _retry.Tick += Retry;
        _source.AddHook(Hook);
        UpdateRetry(_registration.TryRegister());
        viewModel.PropertyChanged += OnPropertyChanged;
    }
    private static void Log(string message) => AppPaths.Log($"Tray path=\"{Environment.ProcessPath}\" {message}");
    private static AppIconService.NativeIcon LoadTrayIcon(int pixels)
    {
        try
        {
            var embedded = AppIconService.Create(pixels);
            Log($"operation=LoadEmbeddedIcon pixels={pixels} success=True");
            return embedded;
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidDataException)
        {
            Log($"operation=LoadEmbeddedIcon pixels={pixels} success=False error={error.Message}");
        }
        var count = ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1);
        Log($"operation=ExtractIconEx count={count} small={small != IntPtr.Zero} large={large != IntPtr.Zero}");
        if (small != IntPtr.Zero)
        {
            if (large != IntPtr.Zero && large != small) ReleaseIcon(large);
            return new(small);
        }
        if (large != IntPtr.Zero) { Log("operation=LoadIcon fallback=large success=True"); return new(large); }
        var shared = LoadIcon(IntPtr.Zero, new(32512));
        // Copy the shared system handle so NativeIcon owns exactly one private handle.
        var systemIcon = shared == IntPtr.Zero ? IntPtr.Zero : CopyIcon(shared);
        Log($"operation=LoadIcon fallback=system success={systemIcon != IntPtr.Zero}");
        if (systemIcon == IntPtr.Zero) throw new InvalidOperationException("Windows could not load a tray icon.");
        return new(systemIcon);
    }
    private static void ReleaseIcon(IntPtr icon) => Log($"operation=DestroyIcon success={DestroyIcon(icon)}");
    private void Retry(object? sender, EventArgs args) => UpdateRetry(_registration.TryRegister());
    private void UpdateRetry(bool registered, bool refresh = true)
    {
        if (registered || _disposed) { _retry.Stop(); if (!_disposed && refresh) QueueRefresh(); }
        else _retry.Start();
    }
    private void OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(AppViewModel.StatusTitle) || _lastTitle == _viewModel.StatusTitle) return;
        _lastTitle = _viewModel.StatusTitle;
        UiPerformance.Count("tray.update");
        UpdateRetry(_registration.Update());
    }
    private Guid IdentityGuid => _registration.Identity == TrayIdentity.Guid ? _guid : Guid.Empty;
    private bool Notify(TrayOperation operation, TrayIdentity identity) => Notify(operation, identity, _icon.DangerousGetHandle());
    private bool Notify(TrayOperation operation, TrayIdentity identity, IntPtr icon)
    {
        var data = new NotifyData { Size = (uint)Marshal.SizeOf<NotifyData>(), Window = _source.Handle, Id = 1,
            Flags = 1 | 2 | 4 | 0x80 | (identity == TrayIdentity.Guid ? 0x20u : 0), Callback = CallbackMessage,
            Icon = icon, Tip = $"AirFlash · {_viewModel.StatusTitle}", Guid = identity == TrayIdentity.Guid ? _guid : Guid.Empty,
            Info = "", InfoTitle = "", Version = operation == TrayOperation.SetVersion ? 4u : 0 };
        return Shell_NotifyIcon((uint)operation, ref data);
    }
    private uint IconDpi() => TryGetRectangle(out var rectangle)
        ? (uint)Math.Round(NativeWindowPlacement.FromRect(rectangle).Scale * 96) : AppIconService.TaskbarDpi();
    private void QueueRefresh()
    {
        if (_disposed || _refreshQueued) return;
        _refreshQueued = true;
        _source.Dispatcher.BeginInvoke(() =>
        {
            _refreshQueued = false;
            if (!_disposed) RefreshIcon();
        }, DispatcherPriority.ContextIdle);
    }
    private void RefreshIcon()
    {
        if (!IsRegistered) { UpdateRetry(_registration.TryRegister()); return; }
        var pixels = AppIconService.Pixels(IconDpi(), true);
        if (pixels == _iconPixels) return;
        AppIconService.NativeIcon? next = null;
        try
        {
            next = AppIconService.Create(pixels);
            var success = Notify(TrayOperation.Modify, _registration.Identity, next.DangerousGetHandle());
            _registration.ReportResult("RefreshIcon", success, _registration.Identity, $"pixels={pixels}");
            if (!success) { UpdateRetry(_registration.Update(), refresh: false); return; }
            var previous = _icon; _icon = next; next = null; _iconPixels = pixels;
            previous.Dispose();
        }
        catch (System.ComponentModel.Win32Exception error) { Log($"operation=RefreshIcon success=False error={error.Message}"); }
        finally { next?.Dispose(); }
    }
    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;
        if (_taskbarCreated != 0 && (uint)message == _taskbarCreated) UpdateRetry(_registration.TaskbarCreated());
        if (message is 0x001a or 0x007e or 0x02e0) QueueRefresh();
        if ((uint)message == CallbackMessage)
        {
            QueueRefresh();
            var action = TrayRegistration.DecodeCallback(wParam.ToInt64(), lParam.ToInt64(), _registration.UsesVersion4);
            if (action == TrayAction.TogglePanel) _panel.ToggleFromTray();
            if (action == TrayAction.OpenMenu) ShowMenu();
            handled = true;
        }
        return IntPtr.Zero;
    }
    private void ShowMenu()
    {
        if (ActiveMenu is { } previousMenu) previousMenu.IsOpen = false;
        var menu = new ContextMenu { Placement = PlacementMode.MousePoint, Style = (System.Windows.Style)System.Windows.Application.Current.FindResource("TextMenu") };
        void AddItem(string label, Action action) { var item = new MenuItem { Header = label }; item.Click += (_, _) => action(); menu.Items.Add(item); }
        AddItem(L.Get("Open panel"), _panel.ShowPanel); AddItem(L.Get("Settings…"), _settings);
        menu.Items.Add(new MenuItem { Header = L.Get("Stop streaming"), Command = _viewModel.StopCommand }); menu.Items.Add(new Separator()); AddItem(L.Get("Quit"), _quit);
        ActiveMenu = menu;
        _panel.MenuOpen = true; menu.Closed += (_, _) => { _panel.MenuOpen = false; if (ActiveMenu == menu) ActiveMenu = null; };
        _registration.ReportResult("SetForegroundWindow", SetForegroundWindow(_source.Handle), _registration.Identity); menu.IsOpen = true;
    }
    internal IntPtr WindowHandle => _source.Handle;
    internal IntPtr IconHandle => _icon.DangerousGetHandle();
    internal int IconPixels => _iconPixels;
    internal bool IsRegisteredForVerification => IsRegistered;
    internal void RemoveForVerification() => _registration.RemoveForVerification();
    internal bool IsRegistered => _registration.IsRegistered;
    internal bool UsesVersion4 => _registration.UsesVersion4;
    internal TrayIdentity Identity => _registration.Identity;
    internal bool TryGetRectangle(out NativeWindowPlacement.Rectangle rectangle)
    {
        rectangle = default;
        if (_disposed || !IsRegistered) return false;
        var identifier = new IconIdentifier { Size = (uint)Marshal.SizeOf<IconIdentifier>(), Window = _source.Handle, Id = 1, Guid = IdentityGuid };
        var result = Shell_NotifyIconGetRect(ref identifier, out rectangle);
        _registration.ReportResult("GetRect", result == 0, _registration.Identity, $"hresult=0x{result:x8}");
        return result == 0;
    }
    public MonitorArea WorkArea()
    {
        return TryGetRectangle(out var rectangle) ? NativeWindowPlacement.FromRect(rectangle) : NativeWindowPlacement.CursorWorkArea();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ActiveMenu is { } menu) menu.IsOpen = false;
        _retry.Stop(); _retry.Tick -= Retry;
        _viewModel.PropertyChanged -= OnPropertyChanged;
        _registration.Dispose(); _source.Dispose();
        _icon.Dispose();
    }
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
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);
    [DllImport("user32.dll")] private static extern IntPtr CopyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
}
