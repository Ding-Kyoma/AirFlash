using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class UiEqualizer
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var store = new UiSmoke.MemoryStore(); var audio = new UiSmoke.MockAudio(); var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, new UiSmoke.MockDiscovery(), new UiSmoke.MockAutostart(), audio, engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup;
        var window = new SettingsWindow(app); window.Show(); await Pump();
        try
        {
            var vm = window.ViewModel;
            Check(vm.Pages.SequenceEqual(new[] { "General", "AirFlash streaming", "Equalizer", "Audio capture", "Receivers", "Monitor", "Network", "About" }.Select(L.Get)), "equalizer navigation follows streaming and preserves network page", checks);
            vm.SelectedPage = SettingsViewModel.EqualizerPage; await Pump();
            Check(audio.Enumerations == 0 && !vm.Equalizer.Enabled && !vm.HasChanges, "equalizer opens disabled without enumerating audio", checks);
            var sliders = UiSmoke.Descendants(window).OfType<Slider>().Where(s => s.Orientation == Orientation.Vertical).ToArray();
            Check(sliders.Length == 10 && sliders.All(s => !s.IsEnabled && s.TickFrequency == 0.5 && s.Minimum == -12 && s.Maximum == 12), "ten disabled half-decibel frequency sliders", checks);
            await app.ToggleAsync(app.AllReceivers.First(r => r.Online));
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            var starts = engine.CreatedCount; var saves = store.Saves;
            var enabled = UiSmoke.Descendants(window).OfType<CheckBox>().Single(c => (string?)c.Content == L.Get("Enable equalizer"));
            enabled.IsChecked = true;
            var presets = UiSmoke.Descendants(window).OfType<ComboBox>().Single(c => c.Name == "EqualizerPreset");
            presets.SelectedValue = "1"; await Until(() => LatestGain(engine) == 6);
            Check(vm.Equalizer.SelectedPreset == "1" && vm.Draft.Equalizer.BandGainsDb.SequenceEqual(new double[] { 6, 4, 2, 0, 0, 0, 0, 0, 0, 0 }), "bass preset populates all bands", checks);
            sliders[0].Value = 7.5;
            var preamp = UiSmoke.Descendants(window).OfType<Slider>().Single(s => s.Orientation == Orientation.Horizontal);
            preamp.Value = -3.5;
            await Until(() => LatestGain(engine) == 7.5);
            Check(vm.Equalizer.SelectedPreset == "custom" && vm.Draft.Equalizer.PreampDb == -3.5 && store.Saves == saves && !app.Settings.Equalizer.Enabled && engine.CreatedCount == starts, "sliders preview custom sound without saving or reconnecting", checks);
            vm.Equalizer.ResetCommand.Execute(null); await Until(() => LatestGain(engine) == 0);
            Check(vm.Equalizer.Enabled && vm.Equalizer.SelectedPreset == "0" && vm.Draft.Equalizer.PreampDb == 0, "reset keeps enabled state and clears gains", checks);
            presets.SelectedValue = "4"; await Until(() => LatestGain(engine) == 4);
            enabled.IsChecked = false;
            await Until(() => engine.Commands.Last(c => c.Text("command") == "set_equalizer").GetProperty("params").GetProperty("equalizer").Boolean("enabled") == false);
            Check(vm.Draft.Equalizer.BandGainsDb[0] == 4 && vm.Equalizer.SelectedPreset == "4", "disabling equalizer remembers the curve", checks);
            enabled.IsChecked = true;
            await Until(() => engine.Commands.Last(c => c.Text("command") == "set_equalizer").GetProperty("params").GetProperty("equalizer").Boolean("enabled") == true);
            store.Fail = true;
            Check(!await vm.ApplyAsync() && vm.HasChanges && !store.Saved.Equalizer.Enabled && LatestGain(engine) == 4, "failed equalizer save preserves draft and preview", checks);
            store.Fail = false;
            Check(await vm.ApplyAsync() && !vm.HasChanges && store.Saved.Equalizer.Enabled && store.Saved.Equalizer.BandGainsDb[0] == 4 && engine.CreatedCount == starts, "equalizer apply saves without restarting", checks);
            foreach (var dark in new[] { false, true })
            {
                ThemeService.SetForVerification(dark); await Pump();
                UiSmoke.Render(window, Path.Combine(directory, $"equalizer-enabled-{(dark ? "dark" : "light")}.png"), 1);
                window.PageScroll.ScrollToBottom(); await Pump();
                UiSmoke.Render(window, Path.Combine(directory, $"equalizer-details-{(dark ? "dark" : "light")}.png"), 1);
                window.PageScroll.ScrollToTop();
            }
            window.Width = 640; window.Height = 440; await Pump();
            var frequencyScroll = UiSmoke.Descendants(window).OfType<ScrollViewer>().Single(s => s.Name == "EqualizerBandScroll");
            Check(frequencyScroll.ScrollableWidth > 0, "minimum window exposes horizontal band scrolling", checks);
            UiSmoke.Render(window, Path.Combine(directory, "equalizer-minimum.png"), 1);
            frequencyScroll.ScrollToRightEnd(); await Pump();
            Check(frequencyScroll.HorizontalOffset > 0, "high bands remain reachable at minimum width", checks);
            vm.Equalizer.Bands[0].Gain = 9; vm.CancelCommand.Execute(null);
            await Until(() => LatestGain(engine) == 4);
            Check(store.Saved.Equalizer.BandGainsDb[0] == 4 && app.Settings.Equalizer.BandGainsDb[0] == 4, "cancel restores last applied equalizer", checks);
            window = new SettingsWindow(app); window.Show(); await Pump();
            Check(window.ViewModel.Equalizer.SelectedPreset == "4", "reopening derives saved preset", checks);
            window.ViewModel.Equalizer.Bands[0].Gain = 10;
            window.Close(); await Task.Delay(100);
            Check(LatestGain(engine) == 4, "closing cancels pending preview and restores saved sound", checks);
        }
        finally { window.Close(); }
    }
    private static double? LatestGain(UiSmoke.MockFactory engine) => engine.Commands.LastOrDefault(c => c.Text("command") == "set_equalizer") is { ValueKind: JsonValueKind.Object } command
        ? command.GetProperty("params").GetProperty("equalizer").GetProperty("band_gains_db")[0].GetDouble() : null;
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(80); }
    private static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); while (!condition()) await Task.Delay(20, timeout.Token); await Pump(); }
}
