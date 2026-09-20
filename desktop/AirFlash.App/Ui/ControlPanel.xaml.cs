using AirFlash.Core;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Ui;

public partial class ControlPanel : Window
{
    private readonly AppViewModel _viewModel;
    private bool _allowClose;
    private bool _placing;
    private DispatcherOperation? _placement;
    private DateTime _lastDismissed;
    public bool MenuOpen { get; set; }
    public TrayService? Tray { get; set; }
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public ControlPanel(AppViewModel viewModel)
    {
        InitializeComponent(); _viewModel = viewModel; DataContext = viewModel; WindowThemeService.Track(this);
        viewModel.SettingsChanged += UpdateLayoutSettings;
        viewModel.Receivers.CollectionChanged += OnReceiversChanged;
        SizeChanged += (_, _) => { if (!_placing) RequestPlacement(); };
        DpiChanged += (_, _) => RequestPlacement();
        UpdateLayoutSettings();
    }
    private void UpdateLayoutSettings()
    {
        var width = _viewModel.Settings.ShowWiderVolume ? 520 : 440;
        if (Width == width) return;
        Width = width;
        RequestPlacement();
    }
    public void ToggleFromTray()
    {
        if (IsVisible) Hide();
        else if (DateTime.UtcNow - _lastDismissed > TimeSpan.FromMilliseconds(240)) ShowPanel();
    }
    public void ShowPanel()
    {
        Show(); RequestPlacement(); Activate();
    }
    public void RequestPlacement()
    {
        if (!IsVisible || _placement is { Status: DispatcherOperationStatus.Pending }) return;
        _placement = Dispatcher.BeginInvoke(() => { _placement = null; Place(); }, DispatcherPriority.Loaded);
    }
    public void Place()
    {
        if (!IsVisible) return;
        _placing = true;
        try
        {
            UiPerformance.Count("panel.place");
            var area = Tray?.WorkArea() ?? NativeWindowPlacement.CursorWorkArea();
            var maxDip = area.Rect.Height * .7 / area.Scale;
            var constraintsChanged = MaxHeight != maxDip;
            DeviceScroll.MaxHeight = Math.Max(80, maxDip - 280);
            MaxHeight = maxDip;
            if (constraintsChanged || ActualHeight <= 0) UpdateLayout();
            NativeWindowPlacement.Place(this, area, Width, ActualHeight);
        }
        finally { _placing = false; }
    }
    private void OnDeactivated(object sender, EventArgs args)
    {
        Dispatcher.BeginInvoke(() => { if (!IsActive && !MenuOpen) { _lastDismissed = DateTime.UtcNow; Hide(); } }, DispatcherPriority.Background);
    }
    private void OnKeyDown(object sender, KeyEventArgs args) { if (args.Key == Key.Escape) { Hide(); args.Handled = true; } }
    private void OnClosing(object? sender, CancelEventArgs args) { if (!_allowClose) { args.Cancel = true; Hide(); } }
    private void OnSliderPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        if (sender is not Slider slider || IsInsideThumb(args.OriginalSource as DependencyObject)) return;
        if (slider.Template.FindName("PART_Track", slider) is not Track track || track.ActualWidth <= 0) return;

        var point = args.GetPosition(track);
        var value = VolumeSliderMath.ValueFromPosition(point.X, track.ActualWidth, track.Thumb.ActualWidth, slider.Minimum, slider.Maximum);
        slider.SetCurrentValue(Slider.ValueProperty, value);
        slider.Focus();
        args.Handled = true;
    }

    private static bool IsInsideThumb(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Thumb) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }
    private void OnPopupOpened(object? sender, EventArgs args) => MenuOpen = true;
    private void OnPopupClosed(object? sender, EventArgs args) { MenuOpen = false; if (!IsActive) Hide(); }
    private void OnSettings(object sender, RoutedEventArgs args) => SettingsRequested?.Invoke();
    private void OnQuit(object sender, RoutedEventArgs args) => QuitRequested?.Invoke();
    private void OnReceiversChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs args)
        => RequestPlacement();
    public void ShutdownPanel()
    {
        _viewModel.SettingsChanged -= UpdateLayoutSettings;
        _viewModel.Receivers.CollectionChanged -= OnReceiversChanged;
        _placement?.Abort();
        _allowClose = true; Close();
    }
}
