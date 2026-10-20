using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class ReceiverSettingsRegression
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        await CapabilitiesAfterEngineStartupAsync(checks);
        await ProfilesAndLatencyAsync(checks, directory);
        await ResetAsync(checks);
        await ResetFailuresAsync(checks);
    }
    private static void Check(bool condition, string name, List<string> checks)
    { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(30); }
    private static async Task Until(Func<bool> condition)
    { using var stop = new CancellationTokenSource(5000); while (!condition()) await Task.Delay(20, stop.Token); }
    private static MockDiscovery Discovery() => new() { Items = [new("a", "A", "127.0.0.1") { FollowReceiverClock = true }, new("b", "B", "127.0.0.2")] };
    private static async Task CapabilitiesAfterEngineStartupAsync(List<string> checks)
    {
        await using var engine = new EngineInitialization(_ => Task.FromResult("mock"));
        await using var app = new AppViewModel(new MemoryStore(), Discovery(), new MockAutostart(), new MockAudio(), new MockFactory(), Application.Current.Dispatcher,
            engine, receiverCapabilities: new CapabilityCatalog { Known = true });
        app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump();
        var window = new SettingsWindow(app); window.ViewModel.SelectedPage = SettingsViewModel.ReceiversPage; window.Show(); await Pump();
        try
        {
            var refresh = Descendants(window).OfType<Button>().Single(b => ReferenceEquals(b.Command, window.ViewModel.RefreshReceiverProfilesCommand));
            Check(!refresh.IsEnabled, "receiver capability queries wait for engine availability", checks);
            await engine.InitializeAsync(); await Pump();
            Check(refresh.IsEnabled && window.ViewModel.Receivers.All(r => r.Capabilities.Known), "engine readiness enables capability refresh and loads receiver choices", checks);
        }
        finally { window.Close(); }
    }
    private static async Task ProfilesAndLatencyAsync(List<string> checks, string directory)
    {
        var store = new MemoryStore(); var saved = store.Load(); saved.Options("a").Codec = "pcm"; saved.Options("a").SampleRate = "48000"; store.Save(saved);
        var discovery = Discovery(); var catalog = new CapabilityCatalog(); var engine = new MockFactory { AutoReady = false };
        await using var app = new AppViewModel(store, discovery, new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher, receiverCapabilities: catalog);
        app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump();
        var window = new SettingsWindow(app); var panel = new ControlPanel(app); window.Show();
        var vm = window.ViewModel;
        try
        {
            vm.SelectedPage = SettingsViewModel.ReceiversPage; await Pump();
            foreach (var expander in Descendants(window).OfType<Expander>()) expander.IsExpanded = true;
            await Pump(); var row = vm.Receivers.Single(r => r.Receiver.Id == "a");
            Check(row.CodecChoices.Count == 1 && row.RateChoices.Count == 1 && row.Options.Codec == "pcm" && row.Options.SampleRate == "48000", "capability refresh and WPF bindings retain unverified explicit profiles", checks);
            catalog.Known = true; vm.RefreshReceiverProfilesCommand.Execute(null); await Until(() => row.Capabilities.Known); await Pump();
            Check(row.CodecChoices.Any(c => Equals(c.Value, "pcm")) && row.TimingChoices.Any(c => Equals(c.Value, "follow")), "receiver choices expose only supported stream combinations", checks);
            row.CodecChoice = row.CodecChoices.Single(c => Equals(c.Value, "auto")); row.RateChoice = row.RateChoices.Single(c => c.Value is null);
            row.TransportChoice = row.TransportChoices.Single(c => Equals(c.Value, "buffered"));
            Check(!row.CodecChoices.Any(c => Equals(c.Value, "pcm")) && !row.TimingChoices.Any(c => Equals(c.Value, "ntp")), "buffered transport removes PCM and NTP from selectable options", checks);
            row.TransportChoice = row.TransportChoices.Single(c => Equals(c.Value, "auto"));
            row.Options.LatencyMode = "custom"; row.Options.CustomBufferMs = 650;
            Check(await vm.ApplyAsync(), "automatic receiver profile and custom latency persist", checks);
            Check(app.CanEditGlobalLatency, "idle panel permits editing the global default", checks);
            await app.ToggleAsync(app.AllReceivers.Single(r => r.Id == "a")); await Until(() => engine.EmitEvent is not null); await Pump();
            Check(app.Snapshot.State == PlaybackState.Connecting && !app.CanEditGlobalLatency, "connecting independent receiver latency disables the global default", checks);
            engine.EmitEvent!(new { @event = "streaming" });
            await Until(() => app.Snapshot.State == PlaybackState.Streaming); await Pump(); panel.ShowPanel(); await Pump();
            var latency = Descendants(panel).OfType<ComboBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == L.Get("Global latency mode"));
            Check(!app.CanEditGlobalLatency && !latency.IsEnabled && app.GlobalLatencyHint.Length > 0, "playing independent receiver latency disables global dropdown and explains where to adjust it", checks);
            var global = app.LatencyMode; app.LatencyMode = "buffered";
            Check(app.LatencyMode == global && store.Saved.LatencyMode == global, "disabled global latency rejects writes", checks);
            Check(row.CodecChoices[0].Label == L.Format("Automatic ({0})", "ALAC") && row.TimingChoices[0].Label == L.Format("Automatic ({0})", L.Get("Follow receiver PTP clock")), "automatic labels show negotiated codec and receiver clock role", checks);
            var profileSelectors = Descendants(window).OfType<ComboBox>().Where(c => ReferenceEquals(c.DataContext, row)
                && System.Windows.Data.BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedItemProperty) is not null).ToArray();
            Check(profileSelectors.Length == 4 && profileSelectors.All(c => c.SelectedItem is StreamChoice), "WPF profile selectors display their current selections after linked options and labels change", checks);
            Descendants(window).OfType<Expander>().First(e => ReferenceEquals(e.DataContext, row) && (string?)e.Header == L.Get("Advanced settings")).BringIntoView(); await Pump();
            Render(window, Path.Combine(directory, "receiver-stream-profiles.png"), 1); Render(panel, Path.Combine(directory, "receiver-latency-disabled.png"), 1);
            Check(profileSelectors.All(c => c.SelectedItem is StreamChoice selected && Descendants(c).OfType<TextBlock>().Any(t => t.Text == selected.Label)),
                "current receiver choices render their automatic labels in the collapsed dropdowns", checks);
            engine.EmitEvent!(new { @event = "standby", reason = "silence" }); await Until(() => app.Snapshot.State == PlaybackState.Standby); await Pump();
            Check(!app.CanEditGlobalLatency && !latency.IsEnabled, "standby retains the current receiver latency override", checks);
            engine.EmitEvent!(new { @event = "resuming" }); engine.EmitEvent!(new { @event = "streaming" });
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            row.Options.Codec = "pcm"; await Pump();
            Check(row.TransportChoices[0].Label == L.Format("Automatic (expected: {0})", L.Get("Real-time UDP")), "unapplied stream edits display predictions instead of active-session values", checks);
            row.Options.Codec = "auto"; row.Options.LatencyMode = null; row.Options.CustomBufferMs = null;
            engine.AutoReady = true;
            Check(await vm.ApplyAsync(), "receiver latency can return to the global default", checks);
            await Until(() => app.Snapshot.State == PlaybackState.Streaming && app.CanEditGlobalLatency); await Pump(); Check(latency.IsEnabled, "returning to inheritance immediately enables the global dropdown", checks);
            await app.StopAsync(); await Pump();
            await app.ToggleAsync(app.AllReceivers.Single(r => r.Id == "b")); await Until(() => app.Snapshot.State == PlaybackState.Streaming); await Pump();
            Check(app.CanEditGlobalLatency && latency.IsEnabled, "switching to an inheriting target enables the global latency control", checks);
            await app.StopAsync(); await Pump();
            vm.Draft.SoftwareGroups.Add(new() { Id = "software:verification", Name = "G", LeftId = "a", RightId = "b" }); vm.Draft.Options("b").LatencyMode = "buffered";
            Check(await vm.ApplyAsync(), "software group and independent member latency persist", checks);
            await Pump();
            var groupSelector = Descendants(window).OfType<ComboBox>().Single(c => c.DataContext is SoftwareGroupEditor && System.Windows.Data.BindingOperations.GetBinding(c, System.Windows.Controls.Primitives.Selector.SelectedItemProperty) is not null);
            Check(groupSelector.SelectedItem is StreamChoice groupRate && Descendants(groupSelector).OfType<TextBlock>().Any(t => t.Text == groupRate.Label), "software group sample rate remains selected and visibly rendered", checks);
            await app.ToggleAsync(app.AllReceivers.Single(r => r.IsSoftwareGroup)); await Until(() => app.Snapshot.State == PlaybackState.Streaming); await Pump();
            Check(!app.CanEditGlobalLatency && !row.CanSetSampleRate, "software member latency disables global control and group owns member sample rate", checks);
            await app.StopAsync(); await Pump(); Check(app.CanEditGlobalLatency && latency.IsEnabled, "stopping a software group restores global latency editing", checks);
        }
        finally { window.Close(); panel.ShutdownPanel(); }
    }
    private static async Task ResetAsync(List<string> checks)
    {
        var store = new MemoryStore(); var discovery = Discovery(); var audio = new MockAudio(); var startup = new MockAutostart(); var restart = new RestartProbe();
        await using var app = new AppViewModel(store, discovery, startup, audio, new MockFactory(), Application.Current.Dispatcher, restart: restart);
        app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump(); await app.ToggleAsync(app.AllReceivers.Single(r => r.Id == "a")); await Until(() => app.Snapshot.State == PlaybackState.Streaming);
        var window = new SettingsWindow(app); window.Show(); var vm = window.ViewModel;
        try
        {
            vm.Draft.Theme = "dark"; vm.ConfirmReset = () => false; await vm.ResetAsync();
            Check(store.Resets == 0 && restart.Calls == 0 && app.Snapshot.IsActive, "cancelling reset retains playback, settings and draft", checks);
            store.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously); store.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            app.MasterVolume = 56; var flush = app.FlushVolumeAsync(); await store.SaveEntered.Task;
            vm.ConfirmReset = () => true; var resetting = vm.ResetAsync(); await Pump();
            Check(!resetting.IsCompleted && store.Resets == 0 && vm.IsApplying, "reset waits for already-started settings persistence", checks);
            store.SaveGate.TrySetResult(); await flush; await resetting;
            Check(store.Resets == 1 && restart.Calls == 1 && !app.Session.Snapshot.IsActive && !discovery.Running && !startup.Enabled && audio.Restores > 0, "reset stops streaming and discovery, restores audio, removes data and schedules one restart", checks);
            Check(store.Saved.MasterVolume == 100 && store.Saved.Theme == "system", "reset discards pending volume and unsaved appearance", checks);
            var saves = store.Saves; await app.FlushVolumeAsync(); await app.DisposeAsync();
            Check(store.Saves == saves, "shutdown after reset never saves the old configuration", checks);
        }
        finally { store.SaveGate?.TrySetResult(); window.Close(); }
    }
    private static async Task ResetFailuresAsync(List<string> checks)
    {
        var store = new MemoryStore { FailReset = true }; var saved = store.Load(); saved.StartAtLogin = true; store.Save(saved);
        var startup = new MockAutostart(); var restart = new RestartProbe();
        await using var app = new AppViewModel(store, Discovery(), startup, new MockAudio(), new MockFactory(), Application.Current.Dispatcher, restart: restart);
        app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump();
        using var vm = new SettingsViewModel(app) { ConfirmReset = () => true };
        await vm.ResetAsync();
        Check(vm.Error.Length > 0 && vm.CanEdit && !app.IsClosing && restart.Calls == 0 && startup.Enabled, "reset deletion failure is visible and restores startup settings", checks);
        store.FailReset = false; restart.Fail = true; await vm.ResetAsync();
        Check(vm.Error.Length > 0 && vm.CanEdit && !app.IsClosing && app.Settings.MasterVolume == 100, "restart failure leaves defaults and usable settings with a visible error", checks);
        restart.Fail = false; await vm.ResetAsync();
        Check(app.IsClosing && restart.Calls == 2, "failed reset can be retried without restarting twice", checks);
    }
    private sealed class RestartProbe : IRestartService
    {
        public int Calls; public bool Fail;
        public void Schedule() { Calls++; if (Fail) throw new IOException("mock restart failed"); }
    }
    private sealed class CapabilityCatalog : IReceiverCapabilityCatalog
    {
        public bool Known;
        public Task<ReceiverCapabilitySet> QueryAsync(Receiver receiver, CancellationToken cancellation = default)
        {
            if (!Known) return Task.FromResult(ReceiverCapabilitySet.Unknown);
            var members = receiver.Peers.Select(peer =>
            {
                var profiles = new List<ReceiverProfile>();
                foreach (var rate in new[] { 44100, 48000 })
                    foreach (var codec in new[] { "auto", "pcm", "alac" })
                        foreach (var transport in new[] { "auto", "realtime", "buffered" })
                            foreach (var timing in new[] { "auto", "ptp", "ntp", "follow" })
                                if (!(transport == "buffered" && (codec == "pcm" || timing == "ntp")) && (timing != "follow" || peer.FollowReceiverClock))
                                    profiles.Add(new(codec, transport, timing, rate, codec == "pcm" ? "pcm" : "alac", transport == "buffered" ? "buffered" : "realtime", timing == "ntp" ? "ntp" : timing == "follow" || timing == "auto" && peer.FollowReceiverClock ? "follow" : "ptp"));
                return new ReceiverCapabilities(peer.Id, peer.Address, peer.Port, true, profiles.ToArray());
            }).ToArray();
            return Task.FromResult(new ReceiverCapabilitySet(members));
        }
    }
}

