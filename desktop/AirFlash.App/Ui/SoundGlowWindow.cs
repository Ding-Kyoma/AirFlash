using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Ui;

// Session-only: closing this window stops cycling, but does not clear receiver artwork.
public sealed class SoundGlowWindow : Window
{
    private readonly AppViewModel _app;
    private readonly TextBox _log = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 160 };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Border _swatch = new() { Height = 60, CornerRadius = new CornerRadius(6), Background = Brushes.Gray, Margin = new Thickness(0, 12, 0, 12) };
    private readonly CheckBox _cycle = new() { Margin = new Thickness(0, 8, 0, 8) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly WrapPanel _actions = new();
    private string? _lastPalette;
    private bool _busy, _closed;

    public SoundGlowWindow(AppViewModel app)
    {
        _app = app;
        Title = "AirFlash · " + L.Get("Sound Glow experiment");
        Width = 610; Height = 660; MinWidth = 440; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "CanvasBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        SetResourceReference(FontFamilyProperty, "BodyFont");
        var root = new StackPanel { Margin = new Thickness(20) };
        var scroll = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        scroll.SetResourceReference(BackgroundProperty, "CanvasBrush");
        var surface = new Border { Child = scroll };
        surface.SetResourceReference(Border.BackgroundProperty, "CanvasBrush");
        Content = surface;
        root.Children.Add(new TextBlock { Text = L.Get("Sound Glow experiment"), FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(Hint("Enable Sound Glow in the Home app, then stream audio. This experiment sends synthetic song information and test covers.", new Thickness(0, 12, 0, 12)));
        root.Children.Add(_state); root.Children.Add(_swatch); root.Children.Add(_actions);
        AddButton("Song information only", "metadata_only"); AddButton("Red", "red"); AddButton("Blue", "blue"); AddButton("Green", "green");
        _cycle.Content = Hint("Random color every 15 seconds", new Thickness());
        root.Children.Add(_cycle);
        var reset = new Button { Content = Hint("Restore normal playback (stop and reconnect)", new Thickness()), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 4, 0, 8), Name = "SoundGlowReset" };
        reset.Click += async (_, _) =>
        {
            if (_busy || _closed) return;
            _cycle.IsChecked = false; _busy = true; UpdateState();
            try
            {
                var receiver = _app.Session.Snapshot.Receiver;
                await _app.Session.StopAsync();
                _lastPalette = null; _swatch.Background = Brushes.Gray;
                if (!_closed && receiver is not null) await _app.Session.StartAsync(receiver, _app.Settings.Clone());
                Append(L.Get("Reconnected with normal song information. HomePod may retain the previous cover."));
            }
            catch (Exception error) { Append(error.Message); }
            finally { _busy = false; UpdateState(); }
        };
        root.Children.Add(reset);
        root.Children.Add(Hint("Try song information, then red and blue. Song information alone does not clear an old cover; reconnect first.", new Thickness(0, 4, 0, 12)));
        root.Children.Add(_log);
        var logs = new Button { Content = L.Get("Open diagnostic logs"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0) };
        logs.Click += (_, _) => AppPaths.OpenLogs(); root.Children.Add(logs);
        _timer.Tick += async (_, _) =>
        {
            var choices = new[] { "red", "blue", "green" }.Where(p => p != _lastPalette).ToArray();
            await SendAsync(choices[Random.Shared.Next(choices.Length)]);
        };
        _cycle.Checked += async (_, _) => { await SendAsync("red"); if (_cycle.IsChecked == true && !_closed) _timer.Start(); };
        _cycle.Unchecked += (_, _) => _timer.Stop();
        _app.Session.SoundGlowFeedback += OnFeedback; _app.Session.Changed += OnSessionChanged;
        Closed += (_, _) => { _closed = true; _timer.Stop(); _app.Session.SoundGlowFeedback -= OnFeedback; _app.Session.Changed -= OnSessionChanged; };
        WindowThemeService.Track(this); AppIconService.Track(this);
        UpdateState();
        Append(L.Get("No test covers are sent by default. Accepted requests do not prove visible lighting. Closing stops cycling; the last cover may remain."));
    }
    private static TextBlock Hint(string text, Thickness margin) => new() { Text = L.Get(text), TextWrapping = TextWrapping.Wrap, Margin = margin };
    private static string PaletteLabel(string? palette) => L.Get(palette switch { "metadata_only" => "Song information only", "red" => "Red", "blue" => "Blue", "green" => "Green", _ => "Not sent" });
    private void AddButton(string label, string palette)
    {
        var button = new Button { Content = L.Get(label), Tag = palette, Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += async (_, _) => { _cycle.IsChecked = false; await SendAsync(palette); };
        _actions.Children.Add(button);
    }
    private async Task SendAsync(string palette)
    {
        if (_busy || _closed) return;
        _busy = true; UpdateState();
        try
        {
            await _app.Session.SendSoundGlowTestAsync(palette);
            if (_closed) return;
            _lastPalette = palette;
            _swatch.Background = palette switch { "red" => new SolidColorBrush(Color.FromRgb(240, 24, 24)), "blue" => new SolidColorBrush(Color.FromRgb(24, 64, 240)), "green" => new SolidColorBrush(Color.FromRgb(24, 220, 64)), _ => Brushes.Gray };
            Append(L.Format("Requested: {0}; waiting for receiver response.", PaletteLabel(palette)));
        }
        catch (Exception error) { _cycle.IsChecked = false; Append(error.Message); }
        finally { _busy = false; UpdateState(); }
    }
    private void OnFeedback(JsonElement item) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed) return;
        if (item.Text("event") == "sound_glow_queued") { Append(L.Get("Queued by the engine; receiver acceptance is not yet confirmed.")); return; }
        if (item.Text("event") == "sound_glow_error" || item.Boolean("accepted") == false) _cycle.IsChecked = false;
        Append(item.Text("event") == "sound_glow_result"
            ? $"#{item.Integer("sequence")} {item.Text("host")} · {PaletteLabel(item.Text("palette"))} · {item.Text("part")}: {(item.Boolean("accepted") == true ? L.Get("200 accepted (observe the light)") : L.Format("Failed: {0}", item.Text("message")))}"
            : item.Text("message"));
    });
    private void OnSessionChanged(SessionSnapshot snapshot) => Dispatcher.BeginInvoke(() =>
    {
        if (_closed) return;
        if (snapshot.State != PlaybackState.Streaming) _cycle.IsChecked = false;
        if (snapshot.State is PlaybackState.Idle or PlaybackState.Connecting or PlaybackState.Error or PlaybackState.Preempted) { _lastPalette = null; _swatch.Background = Brushes.Gray; }
        UpdateState();
    });
    private void UpdateState()
    {
        if (_closed) return;
        var supported = _app.Session.SoundGlowSupported;
        var streaming = _app.Session.Snapshot.State == PlaybackState.Streaming;
        _state.Text = streaming && !supported ? L.Get("This audio engine does not support the Sound Glow experiment.")
            : L.Format("Playback: {0} · Requested palette: {1}", _app.StatusTitle, PaletteLabel(_lastPalette));
        _actions.IsEnabled = streaming && supported && !_busy; _cycle.IsEnabled = streaming && supported && !_busy;
    }
    private void Append(string message)
    {
        if (_closed) return;
        if (_log.Text.Length > 16000) _log.Text = _log.Text[^8000..];
        _log.AppendText($"{DateTime.Now:HH:mm:ss} {message}{Environment.NewLine}"); _log.ScrollToEnd();
    }
}
