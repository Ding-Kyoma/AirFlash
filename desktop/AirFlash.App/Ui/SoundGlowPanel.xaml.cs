using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Ui;

public partial class SoundGlowPanel : UserControl, IDisposable
{
    public static readonly DependencyProperty AppProperty = DependencyProperty.Register(
        nameof(App), typeof(AppViewModel), typeof(SoundGlowPanel),
        new PropertyMetadata(null, (sender, args) => ((SoundGlowPanel)sender).SetApp(args.NewValue as AppViewModel)));
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private AppViewModel? _app;
    private string? _lastPalette;
    private bool _busy, _disposed;

    public AppViewModel? App { get => (AppViewModel?)GetValue(AppProperty); set => SetValue(AppProperty, value); }

    public SoundGlowPanel()
    {
        InitializeComponent();
        _timer.Tick += OnTimerTick;
        IsVisibleChanged += OnVisibleChanged;
    }

    private void SetApp(AppViewModel? app)
    {
        if (_disposed || ReferenceEquals(_app, app)) return;
        if (_app is not null) { _app.Session.SoundGlowFeedback -= OnFeedback; _app.Session.Changed -= OnSessionChanged; }
        _app = app;
        if (_app is not null) { _app.Session.SoundGlowFeedback += OnFeedback; _app.Session.Changed += OnSessionChanged; }
        UpdateState();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        IsVisibleChanged -= OnVisibleChanged;
        if (_app is not null) { _app.Session.SoundGlowFeedback -= OnFeedback; _app.Session.Changed -= OnSessionChanged; _app = null; }
    }

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (!IsVisible) { Cycle.IsChecked = false; _timer.Stop(); }
        else UpdateState();
    }

    private static string PaletteLabel(string? palette) => L.Get(palette switch { "metadata_only" => "Song information only", "red" => "Red", "blue" => "Blue", "green" => "Green", _ => "Not sent" });

    private async void OnPaletteClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string palette }) return;
        Cycle.IsChecked = false;
        await SendAsync(palette);
    }

    private async void OnCycleChecked(object sender, RoutedEventArgs args)
    {
        if (_disposed || _app is null) return;
        await SendAsync("red");
        if (Cycle.IsChecked == true && !_disposed) _timer.Start();
    }

    private void OnCycleUnchecked(object sender, RoutedEventArgs args) => _timer.Stop();

    private async void OnTimerTick(object? sender, EventArgs args)
    {
        var choices = new[] { "red", "blue", "green" }.Where(p => p != _lastPalette).ToArray();
        await SendAsync(choices[Random.Shared.Next(choices.Length)]);
    }

    private async void OnResetClick(object sender, RoutedEventArgs args)
    {
        if (_busy || _disposed || _app is null) return;
        Cycle.IsChecked = false; _busy = true; UpdateState();
        try
        {
            var receiver = _app.Session.Snapshot.Receiver;
            await _app.Session.StopAsync();
            _lastPalette = null; Swatch.Background = Brushes.Gray;
            if (!_disposed && receiver is not null) await _app.Session.StartAsync(receiver, _app.Settings.Clone());
            Append(L.Get("Reconnected with normal song information. HomePod may retain the previous cover."));
        }
        catch (Exception error) { Append(error.Message); }
        finally { _busy = false; if (!_disposed) UpdateState(); }
    }

    private void OnOpenLogsClick(object sender, RoutedEventArgs args)
    {
        try { AppPaths.OpenLogs(); }
        catch (Exception error) { Append(error.Message); }
    }

    private async Task SendAsync(string palette)
    {
        if (_busy || _disposed || _app is null) return;
        _busy = true; UpdateState();
        try
        {
            await _app.Session.SendSoundGlowTestAsync(palette);
            if (_disposed) return;
            _lastPalette = palette;
            Swatch.Background = palette switch { "red" => new SolidColorBrush(Color.FromRgb(240, 24, 24)), "blue" => new SolidColorBrush(Color.FromRgb(24, 64, 240)), "green" => new SolidColorBrush(Color.FromRgb(24, 220, 64)), _ => Brushes.Gray };
            Append(L.Format("Requested: {0}; waiting for receiver response.", PaletteLabel(palette)));
        }
        catch (Exception error) { Cycle.IsChecked = false; Append(error.Message); }
        finally { _busy = false; if (!_disposed) UpdateState(); }
    }

    private void OnFeedback(JsonElement item) => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (item.Text("event") == "sound_glow_queued") { Append(L.Get("Queued by the engine; receiver acceptance is not yet confirmed.")); return; }
        if (item.Text("event") == "sound_glow_error" || item.Boolean("accepted") == false) Cycle.IsChecked = false;
        Append(item.Text("event") == "sound_glow_result"
            ? $"#{item.Integer("sequence")} {item.Text("host")} · {PaletteLabel(item.Text("palette"))} · {item.Text("part")}: {(item.Boolean("accepted") == true ? L.Get("200 accepted (observe the light)") : L.Format("Failed: {0}", item.Text("message")))}"
            : item.Text("message"));
    });

    private void OnSessionChanged(SessionSnapshot snapshot) => Dispatcher.BeginInvoke(() =>
    {
        if (_disposed) return;
        if (snapshot.State != PlaybackState.Streaming) Cycle.IsChecked = false;
        if (snapshot.State is PlaybackState.Idle or PlaybackState.Connecting or PlaybackState.Error or PlaybackState.Preempted) { _lastPalette = null; Swatch.Background = Brushes.Gray; }
        UpdateState();
    });

    private void UpdateState()
    {
        if (_disposed) return;
        if (_app is null) { StateText.Text = ""; Actions.IsEnabled = false; Cycle.IsEnabled = false; return; }
        var supported = _app.Session.SoundGlowSupported;
        var streaming = _app.Session.Snapshot.State == PlaybackState.Streaming;
        StateText.Text = streaming && !supported ? L.Get("This audio engine does not support the Sound Glow experiment.")
            : L.Format("Playback: {0} · Requested palette: {1}", _app.StatusTitle, PaletteLabel(_lastPalette));
        Actions.IsEnabled = streaming && supported && !_busy; Cycle.IsEnabled = streaming && supported && !_busy;
    }

    private void Append(string message)
    {
        if (_disposed) return;
        if (SoundGlowLog.Text.Length > 16000) SoundGlowLog.Text = SoundGlowLog.Text[^8000..];
        SoundGlowLog.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); SoundGlowLog.ScrollToEnd();
    }
}
