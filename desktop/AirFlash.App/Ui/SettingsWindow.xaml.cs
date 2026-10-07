using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
namespace AirFlash.App.Ui;

public partial class SettingsWindow : Window
{
    public SettingsViewModel ViewModel { get; }
    private readonly HashSet<ValidationError> _errors = [];
    private readonly Dictionary<int, FrameworkElement> _pages = [];
    public SettingsWindow(AppViewModel app)
    {
        InitializeComponent(); ViewModel = new(app); DataContext = ViewModel;
        WindowThemeService.Track(this);
        AppIconService.Track(this);
        ViewModel.CloseRequested += _ => Close();
        ViewModel.AddReceiverRequested += () => { var dialog = new ManualReceiverDialog { Owner = this }; if (dialog.ShowDialog() == true) ViewModel.AddManual(dialog.DeviceName, dialog.Host, dialog.Port); };
        ViewModel.PairRequested += async receiver =>
        {
            try { await app.Session.PairAsync(receiver, app.Settings.Clone(), (member, token) => PinDialog.RequestAsync(this, member, token)); }
            catch (Exception error) { app.ShowError(error); }
        };
        ViewModel.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(SettingsViewModel.SelectedPage)) ShowPage(); };
        IsVisibleChanged += (_, _) => app.SetMonitorVisible(IsVisible && ViewModel.SelectedPage == SettingsViewModel.MonitorPage);
        Closing += (_, args) => { if (ViewModel.IsApplying) args.Cancel = true; };
        Closed += (_, _) => ViewModel.Dispose();
        ShowPage();
    }
    private void ShowPage()
    {
        var selected = ViewModel.SelectedPage;
        if (!_pages.TryGetValue(selected, out var page))
        {
            page = (FrameworkElement)((DataTemplate)FindResource($"Page{selected}")).LoadContent();
            if (selected != SettingsViewModel.MonitorPage) page.SetBinding(IsEnabledProperty, new Binding(nameof(SettingsViewModel.CanEdit)));
            _pages.Add(selected, page); PageContainer.Children.Add(page);
        }
        foreach (var (index, view) in _pages) view.Visibility = index == selected ? Visibility.Visible : Visibility.Collapsed;
        ViewModel.App.SetMonitorVisible(IsVisible && selected == SettingsViewModel.MonitorPage);
        PageScroll.ScrollToTop();
    }
    private void OnSourceInitialized(object? sender, EventArgs args)
    {
        NativeWindowPlacement.CenterSettings(this);
    }
    private void OnEqualizerMouseWheel(object sender, MouseWheelEventArgs args)
    {
        // The horizontal band viewer consumes wheel input even with vertical scrolling disabled.
        // Let the page apply WPF's normal wheel behavior, preserving the user's wheel settings.
        args.Handled = true;
        PageScroll.RaiseEvent(new MouseWheelEventArgs(args.MouseDevice, args.Timestamp, args.Delta)
        {
            RoutedEvent = Mouse.MouseWheelEvent
        });
    }
    private void OnCoffeeClick(object sender, RoutedEventArgs args)
    {
        new CoffeeWindow { Owner = this }.ShowDialog();
    }
    private void OnValidationError(object sender, ValidationErrorEventArgs args)
    {
        if (args.Action == ValidationErrorEventAction.Added) _errors.Add(args.Error); else _errors.Remove(args.Error);
        ViewModel.SetValidationErrors(_errors.Count);
    }
}
