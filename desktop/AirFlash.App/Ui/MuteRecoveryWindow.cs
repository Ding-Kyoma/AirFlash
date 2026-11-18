using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Ui;

// Modeless: reviewing a stale record never blocks Stop or owns a lifecycle lock.
public sealed class MuteRecoveryWindow : Window
{
    private readonly AppViewModel _app;
    private readonly StackPanel _entries = new();
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _refresh;
    private readonly Button _archive;
    private bool _busy;
    private bool _closed;

    public MuteRecoveryWindow(AppViewModel app)
    {
        _app = app; Title = L.Get("Review local audio"); Width = 580; Height = 470; MinWidth = 420; MinHeight = 320;
        WindowThemeService.Track(this); AppIconService.Track(this);
        var content = new DockPanel { Margin = new Thickness(20) };
        var introduction = new TextBlock
        {
            Text = L.Get("AirFlash saved the previous mute setting before changing local audio. Another app or a later user action may have changed it. Choose how to handle each saved output."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14)
        };
        DockPanel.SetDock(introduction, Dock.Top); content.Children.Add(introduction);
        var footer = new StackPanel(); DockPanel.SetDock(footer, Dock.Bottom); content.Children.Add(footer);
        footer.Children.Add(_error);
        var actions = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(actions);
        _refresh = new() { Name = "RefreshRecoveryButton", Content = L.Get("Refresh"), Margin = new Thickness(0, 0, 8, 0) };
        _refresh.Click += async (_, _) => await PerformAsync(_app.RefreshMuteRecoveryAsync); actions.Children.Add(_refresh);
        _archive = new() { Name = "ArchiveRecoveryButton", Content = L.Get("Preserve record and continue"), Margin = new Thickness(0, 0, 8, 0) };
        _archive.Click += async (_, _) => { var revision = _app.MuteRecovery.Revision; await PerformAsync(() => _app.ArchiveMuteRecoveryAsync(revision)); };
        actions.Children.Add(_archive);
        var later = new Button { Name = "RecoveryLaterButton", Content = L.Get("Later") };
        later.Click += (_, _) => Close(); actions.Children.Add(later);
        content.Children.Add(new ScrollViewer { Content = _entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = content;
        Loaded += async (_, _) => await PerformAsync(async () => { await _app.Endpoints.GetAsync(); });
        _app.PropertyChanged += AppChanged;
        Closed += (_, _) => { _closed = true; _app.PropertyChanged -= AppChanged; };
        BuildEntries();
    }

    private void AppChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (!_closed && args.PropertyName == nameof(AppViewModel.MuteRecovery)) BuildEntries();
    }
    private void BuildEntries()
    {
        _entries.Children.Clear();
        var snapshot = _app.MuteRecovery;
        _entries.Children.Add(new TextBlock { Text = _app.MuteRecoveryMessage, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        if (snapshot.Detail is { Length: > 0 } detail)
            _entries.Children.Add(new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var entry in snapshot.Entries.Where(entry => !entry.Live || entry.KnownRestored || entry.State == MuteJournalState.Restoring))
        {
            var card = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            var endpoint = _app.Endpoints.Items?.FirstOrDefault(endpoint => endpoint.Id == entry.EndpointId);
            card.Children.Add(new TextBlock { Text = endpoint?.Name ?? L.Get("Saved audio output"), ToolTip = entry.EndpointId, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (entry.KnownRestored)
            {
                // This is a known completed endpoint write while this process is
                // alive. The observed bit may since have changed; retry only disk.
                card.Children.Add(new TextBlock { Text = L.Get("The saved mute setting was restored. The recovery record still needs to be saved."), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
                var retry = new Button { Name = "RetryRecoverySaveButton", Content = L.Get("Retry saving recovery record"), IsEnabled = !_busy, HorizontalAlignment = HorizontalAlignment.Left };
                retry.Click += async (_, _) => await PerformAsync(_app.RefreshMuteRecoveryAsync);
                card.Children.Add(retry); _entries.Children.Add(card);
                continue;
            }
            card.Children.Add(new TextBlock
            {
                Text = L.Format("Saved mute: {0}; current mute: {1}", L.Get(entry.PreviousMute ? "Muted" : "Unmuted"),
                    entry.CurrentMute is { } current ? L.Get(current ? "Muted" : "Unmuted") : L.Get("Unavailable")),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8)
            });
            var choices = new WrapPanel();
            AddChoice(choices, "RestoreSavedMuteButton", "Restore saved mute setting", MuteRecoveryChoice.RestoreSaved);
            AddChoice(choices, "KeepCurrentMuteButton", "Keep current mute setting", MuteRecoveryChoice.KeepCurrent);
            card.Children.Add(choices); _entries.Children.Add(card);
            void AddChoice(Panel panel, string name, string label, MuteRecoveryChoice choice)
            {
                var button = new Button { Name = name, Content = L.Get(label), IsEnabled = !_busy && entry.CurrentMute.HasValue,
                    Margin = new Thickness(0, 0, 8, 0) };
                button.Click += async (_, _) => await PerformAsync(() => _app.ResolveMuteRecoveryAsync(snapshot.Revision, entry, choice));
                panel.Children.Add(button);
            }
        }
        _archive.Visibility = snapshot.Status == MuteRecoveryStatus.Unrecognized ? Visibility.Visible : Visibility.Collapsed;
        _archive.IsEnabled = !_busy; _refresh.IsEnabled = !_busy;
        if (snapshot.Status == MuteRecoveryStatus.Unrecognized)
            _entries.Children.Add(new TextBlock { Text = L.Get("Continuing preserves an exact backup of the unreadable record and keeps the current mute settings. Previously saved mute settings will not be restored."), TextWrapping = TextWrapping.Wrap });
        if (snapshot.CanMute)
            _entries.Children.Add(new TextBlock { Text = L.Get("Local audio recovery is complete."), TextWrapping = TextWrapping.Wrap });
    }
    private async Task PerformAsync(Func<Task> action)
    {
        if (_busy || _closed || _app.IsClosing) return;
        _busy = true; _error.Text = ""; BuildEntries();
        try { await action(); }
        catch (Exception error) { if (!_closed) _error.Text = error.Message; }
        finally { _busy = false; if (!_closed) BuildEntries(); }
    }
}
