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
                var settings = new SettingsWindow(app); settings.Show(); await Pump();
                try
                {
                    settings.ViewModel.SelectedPage = SettingsViewModel.SoundGlowPage; await Pump();
                    var panel = Descendants(settings).OfType<SoundGlowPanel>().Single();
                    Check(settings.OwnedWindows.Count == 0, language + ": experiment is embedded without a child window", checks);
                    var buttons = Descendants(panel).OfType<Button>().Where(b => b.Tag is string).ToArray();
                    var reset = Descendants(panel).OfType<Button>().Single(b => b.Name == "SoundGlowReset");
                    Check(buttons.Length == 4 && buttons.All(b => !b.IsEnabled), language + ": experiment disabled before streaming", checks);
                    await app.Session.StartAsync(receiver, app.Settings.Clone());
                    await Until(() => app.Session.Snapshot.State == PlaybackState.Streaming); await Pump();
                    Check(buttons.All(b => b.IsEnabled) && !engine.Commands.Any(c => c.Text("command") == "set_sound_glow"), language + ": streaming is opt-in", checks);
                    foreach (var dark in new[] { false, true })
                    {
                        ThemeService.SetForVerification(dark); settings.Width = 640; await Pump();
                        var title = Descendants(panel).OfType<TextBlock>().First(t => t.Text == L.Get("Sound Glow experiment"));
                        Check(((SolidColorBrush)title.Foreground).Color == ThemeService.CurrentPalette.Text, language + ": embedded experiment follows theme", checks);
                        foreach (var button in buttons)
                        {
                            var point = button.TransformToAncestor(settings.PageScroll).Transform(new Point());
                            Check(point.X >= 0 && point.X + button.ActualWidth <= settings.PageScroll.ViewportWidth, language + ": palette buttons fit minimum width", checks);
                        }
                        var resetPoint = reset.TransformToAncestor(settings.PageScroll).Transform(new Point());
                        Check(resetPoint.X >= 0 && resetPoint.X + reset.ActualWidth <= settings.PageScroll.ViewportWidth, language + ": reset button fits minimum width", checks);
                        foreach (var scale in new[] { 1d, 2d }) Render(settings, Path.Combine(directory, $"sound-glow-{language}-{(dark ? "dark" : "light")}-{scale * 100}.png"), scale);
                    }
                    settings.Width = 880; await Pump();
                    foreach (var button in buttons) { button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Pump(); }
                    Check(engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 4, language + ": all four manual actions send a request", checks);
                    var cycle = Descendants(panel).OfType<CheckBox>().Single();
                    cycle.IsChecked = true;
                    await Until(() => engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 5);
                    var latest = engine.Commands.Last(c => c.Text("command") == "set_sound_glow").GetProperty("params");
                    engine.EmitEvent!(new { @event = "sound_glow_result", sequence = latest.Integer("sequence"), accepted = false, message = "synthetic rejection" });
                    await Until(() => cycle.IsChecked == false);
                    Check(app.Session.Snapshot.State == PlaybackState.Streaming, language + ": rejection stops cycling without stopping playback", checks);
                    reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await Until(() => engine.Commands.Count(c => c.Text("command") == "start") == 2 && app.Session.Snapshot.State == PlaybackState.Streaming);
                    Check(engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 5, language + ": reconnect does not replay experiment", checks);

                    cycle.IsChecked = true;
                    await Until(() => engine.Commands.Count(c => c.Text("command") == "set_sound_glow") == 6);
                    settings.ViewModel.SelectedPage = SettingsViewModel.EqualizerPage; await Pump();
                    Check(!panel.IsVisible && cycle.IsChecked == false, language + ": leaving the page hides the experiment and stops cycling", checks);
                    settings.ViewModel.SelectedPage = SettingsViewModel.SoundGlowPage; await Pump();
                    Check(panel.IsVisible, language + ": returning to the page restores the embedded experiment", checks);

                    var log = Descendants(panel).OfType<TextBox>().Single(t => t.Name == "SoundGlowLog");
                    var logHeight = log.ActualHeight; var pageHeight = panel.ActualHeight;
                    log.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(i => $"entry {i}"));
                    panel.UpdateLayout(); log.ScrollToEnd(); await Pump();
                    var logScroll = Descendants(log).OfType<ScrollViewer>().Single();
                    Check(log.ActualHeight <= 160.5 && Math.Abs(log.ActualHeight - logHeight) < 1 && Math.Abs(panel.ActualHeight - pageHeight) < 1,
                        language + ": the log keeps a fixed height", checks);
                    Check(log.ExtentHeight > log.ViewportHeight && logScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible,
                        language + ": the fixed log scrolls internally", checks);
                    Check(settings.OwnedWindows.Count == 0, language + ": actions never open a child window", checks);
                }
                finally { settings.Close(); }
                await app.Session.StopAsync();
            }
            L.Initialize(["en-US"]);
            var unsupported = new MockFactory { SupportsSoundGlow = false };
            var pod = new Receiver("old-engine", "Old engine", "127.0.0.1");
            await using var oldApp = new AppViewModel(new MemoryStore(), new MockDiscovery { Items = [pod] }, new MockAutostart(), new MockAudio(), unsupported, Application.Current.Dispatcher);
            oldApp.Start(); await oldApp.Startup;
            await oldApp.Session.StartAsync(pod, oldApp.Settings.Clone());
            await Until(() => oldApp.Session.Snapshot.State == PlaybackState.Streaming);
            var oldSettings = new SettingsWindow(oldApp); oldSettings.Show(); await Pump();
            try
            {
                oldSettings.ViewModel.SelectedPage = SettingsViewModel.SoundGlowPage; await Pump();
                var oldPanel = Descendants(oldSettings).OfType<SoundGlowPanel>().Single();
                Check(Descendants(oldPanel).OfType<Button>().Where(b => b.Tag is string).All(b => !b.IsEnabled), "old engine: actions disabled while normal playback works", checks);
                Check(oldSettings.OwnedWindows.Count == 0, "old engine: experiment stays embedded", checks);
            }
            finally { oldSettings.Close(); }
        }
        finally { L.Initialize(["en-US"]); ThemeService.SetForVerification(false); }
    }
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(40); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)); while (!condition()) await Task.Delay(20, timeout.Token); await Pump(); }
}
