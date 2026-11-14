using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class SoundGlowRegression
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        try
        {
            foreach (var language in new[] { "en-US", "zh-CN", "zh-TW", "de-DE", "es-ES" })
            {
                L.Initialize([language]);
                var engine = new MockFactory();
                var receiver = new Receiver("glow-test", "Sound Glow test", "127.0.0.1");
                await using var app = new AppViewModel(new MemoryStore(), new MockDiscovery { Items = [receiver] }, new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher);
                app.Start(); await app.Startup;
                var window = new SoundGlowWindow(app); window.Show(); await Pump();
                try
                {
                    var buttons = Descendants(window).OfType<Button>().Where(b => b.Tag is string).ToArray();
                    Check(buttons.Length == 4 && buttons.All(b => !b.IsEnabled), language + ": experiment disabled before streaming", checks);
                    await app.Session.StartAsync(receiver, app.Settings.Clone());
                    await Until(() => app.Session.Snapshot.State == PlaybackState.Streaming); await Pump();
                    Check(buttons.All(b => b.IsEnabled) && !engine.Commands.Any(c => c.Text("command") == "set_sound_glow"), language + ": streaming is opt-in", checks);
                    foreach (var dark in new[] { false, true })
                    {
                        ThemeService.SetForVerification(dark); window.Width = 440; await Pump();
                        Check(((SolidColorBrush)window.Background).Color == ThemeService.CurrentPalette.Canvas, language + ": window follows theme", checks);
                        foreach (var button in buttons)
                        {
                            var point = button.TransformToAncestor(window).Transform(new Point());
                            Check(point.X >= 0 && point.X + button.ActualWidth <= window.ActualWidth, language + ": palette buttons fit minimum width", checks);
                        }
                        foreach (var scale in new[] { 1d, 2d }) Render(window, Path.Combine(directory, $"sound-glow-{language}-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
                    }
                    foreach (var button in buttons) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump(); }
                    Check(engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 4, language + ": all four manual actions send a request", checks);
                    var cycle = Descendants(window).OfType<CheckBox>().Single();
                    cycle.IsChecked = true; await Pump();
                    var latest = engine.Commands.Last(c => c.Text("command") == "set_sound_glow").GetProperty("params");
                    engine.EmitEvent!(new { @event = "sound_glow_result", sequence = latest.Integer("sequence"), accepted = false, message = "synthetic rejection" });
                    await Pump();
                    Check(cycle.IsChecked == false && app.Session.Snapshot.State == PlaybackState.Streaming, language + ": rejection stops cycling without stopping playback", checks);
                    var reset = Descendants(window).OfType<Button>().Single(b => b.Name == "SoundGlowReset");
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => engine.Commands.Count(c => c.Text("command") == "start") == 2 && app.Session.Snapshot.State == PlaybackState.Streaming);
                    Check(engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 5, language + ": reconnect does not replay experiment", checks);
                }
                finally { window.Close(); }
                await app.Session.StopAsync();
            }
            L.Initialize(["en-US"]);
            var unsupported = new MockFactory { SupportsSoundGlow = false };
            var pod = new Receiver("old-engine", "Old engine", "127.0.0.1");
            await using var oldApp = new AppViewModel(new MemoryStore(), new MockDiscovery { Items = [pod] }, new MockAutostart(), new MockAudio(), unsupported, Application.Current.Dispatcher);
            oldApp.Start(); await oldApp.Startup;
            await oldApp.Session.StartAsync(pod, oldApp.Settings.Clone());
            await Until(() => oldApp.Session.Snapshot.State == PlaybackState.Streaming);
            var oldWindow = new SoundGlowWindow(oldApp); oldWindow.Show(); await Pump();
            try { Check(Descendants(oldWindow).OfType<Button>().Where(b => b.Tag is string).All(b => !b.IsEnabled), "old engine: actions disabled while normal playback works", checks); }
            finally { oldWindow.Close(); }
        }
        finally { L.Initialize(["en-US"]); ThemeService.SetForVerification(false); }
    }
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(40); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); while (!condition()) await Task.Delay(20, timeout.Token); await Pump(); }
}
