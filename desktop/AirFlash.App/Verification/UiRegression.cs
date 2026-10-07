using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

internal static class UiRegression
{
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var store = new UiSmoke.MemoryStore(); var audio = new UiSmoke.MockAudio();
        var autostart = new UiSmoke.MockAutostart(); var discovery = new UiSmoke.MockDiscovery();
        var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, discovery, autostart, audio, engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup; await Pump();
        var window = new SettingsWindow(app); window.Show(); await Pump();
        var vm = window.ViewModel;
        try
        {
            Check(audio.Enumerations == 0, "general settings never enumerate audio", checks);
            var catalogChanges = 0; app.CatalogChanged += () => catalogChanges++;
            for (var i = 0; i < 40; i++) discovery.Publish();
            await Pump();
            Check(catalogChanges == 0 && audio.Enumerations == 0, "unchanged discovery does not refresh catalogs", checks);

            var retries = UiSmoke.Descendants(window).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == L.Get("Maximum retry attempts"));
            retries.Text = "invalid"; await Pump(); vm.SelectedPage = SettingsViewModel.AudioCapturePage;
            await Until(() => !vm.IsEndpointLoading); await Pump(); vm.SelectedPage = 0;
            Check(retries.Text == "invalid" && !vm.ApplyCommand.CanExecute(null), "first endpoint load preserves invalid input on another page", checks);
            retries.Text = "5"; await Pump();

            var draft = vm.Draft; var row = vm.Receivers[0];
            store.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            store.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.Draft.ForceReconnect = false;
            var apply = vm.ApplyAsync();
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var responsive = false;
            await Application.Current.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            Check(responsive && !apply.IsCompleted && vm.IsApplying && !vm.CanEdit, "blocked save leaves UI responsive with busy feedback", checks);
            await Pump();
            UiSmoke.Render(window, Path.Combine(directory, "settings-applying.png"), 1);
            Check(!vm.ApplyCommand.CanExecute(null) && !vm.OkCommand.CanExecute(null) && !vm.CancelCommand.CanExecute(null), "busy state disables commit and cancel commands", checks);
            var saves = store.Saves;
            Check(!await vm.ApplyAsync() && store.Saves == saves, "method guard rejects duplicate apply", checks);
            window.Close(); vm.CancelCommand.Execute(null);
            Check(window.IsVisible, "closing and cancel cannot interrupt a settings transaction", checks);
            vm.SelectedPage = 1; await Pump();
            Check(vm.SelectedPage == 1 && window.Navigation.IsEnabled, "navigation stays enabled during apply", checks);
            app.MasterVolume = 37;
            var flush = app.FlushVolumeAsync();
            store.SaveGate.TrySetResult();
            Check(await apply, "delayed apply completes", checks);
            await flush;
            Check(store.Saved.MasterVolume == 37 && app.Settings.MasterVolume == 37, "panel edits during save survive and persist in order", checks);
            Check(ReferenceEquals(draft, vm.Draft) && ReferenceEquals(row, vm.Receivers[0]) && !vm.HasChanges, "apply retains draft and receiver rows", checks);
            store.SaveGate = null; store.SaveEntered = null;

            vm.Draft.StartAtLogin = true; autostart.FailEnable = true;
            saves = store.Saves;
            Check(!await vm.ApplyAsync() && store.Saves == saves && !app.Settings.StartAtLogin && vm.HasChanges, "startup failure leaves saved settings unchanged", checks);
            autostart.FailEnable = false; store.Fail = true;
            Check(!await vm.ApplyAsync() && !autostart.Enabled && vm.HasChanges, "save failure restores startup setting", checks);
            autostart.FailDisable = true;
            Check(!await vm.ApplyAsync() && vm.Error.Contains(L.Get("Saving failed and startup settings could not be restored."), StringComparison.Ordinal), "rollback failure is surfaced", checks);
            autostart.FailDisable = false; store.Fail = false;
            Check(await vm.ApplyAsync() && app.Settings.StartAtLogin && autostart.Enabled, "retry succeeds after persistence failure", checks);

            await app.ToggleAsync(app.AllReceivers.First(r => r.Online));
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            var starts = engine.CreatedCount;
            audio.DefaultEndpoint = "endpoint-b";
            for (var i = 0; i < 20; i++) audio.NotifyEndpoints();
            await Until(() => engine.CreatedCount > starts && app.Snapshot.State == PlaybackState.Streaming);
            Check(engine.CreatedCount == starts + 1, "default output change restarts loopback once", checks);
            audio.NotifyEndpoints(); await Task.Delay(250);
            Check(engine.CreatedCount == starts + 1, "unchanged default output does not restart loopback", checks);
            starts = engine.CreatedCount;
            app.LatencyMode = "buffered";
            vm.Draft.CaptureMode = "endpoint"; vm.Draft.CaptureEndpoint = "endpoint-b";
            var enumerations = audio.Enumerations;
            Check(await vm.ApplyAsync(), "combined panel and settings apply succeeds", checks);
            await Until(() => engine.CreatedCount > starts && app.Snapshot.State == PlaybackState.Streaming);
            Check(engine.CreatedCount == starts + 1, "pending panel latency and endpoint change restart only once", checks);
            Check(audio.Enumerations == enumerations + 1, "endpoint apply performs exactly one fresh enumeration", checks);
            Check(app.Settings.LatencyMode == "buffered" && store.Saved.LatencyMode == "buffered", "pending panel latency is merged", checks);

            starts = engine.CreatedCount;
            vm.Draft.ForceReconnect = !vm.Draft.ForceReconnect; vm.Draft.UiLanguage = "en";
            Check(await vm.ApplyAsync() && engine.CreatedCount == starts, "non-audio settings do not restart playback", checks);
            audio.FailMute = true; vm.Draft.MuteWhileStreaming = true;
            Check(!await vm.ApplyAsync() && !vm.HasChanges && store.Saved.MuteWhileStreaming && vm.Error.Contains("mock mute failed", StringComparison.Ordinal), "saved configuration and audio failure are distinguished", checks);
            audio.FailMute = false; vm.Draft.MuteWhileStreaming = false; await vm.ApplyAsync();

            vm.SelectedPage = SettingsViewModel.AudioCapturePage; await vm.LoadEndpointsAsync(false);
            Check(vm.Endpoints.Any(e => e.Id == "endpoint-b"), "audio page uses cached endpoints", checks);
            audio.Items = [new("endpoint-a", "Renamed")];
            for (var i = 0; i < 40; i++) audio.NotifyEndpoints();
            enumerations = audio.Enumerations;
            await Until(() => vm.Endpoints.Any(e => e.Name == "Renamed"));
            Check(audio.Enumerations == enumerations + 1, "endpoint notification burst is coalesced", checks);
            Check(vm.Draft.CaptureEndpoint == "endpoint-b" && vm.Endpoints.Any(e => e.Id == "endpoint-b" && e.Name.StartsWith(L.Get("Unavailable · "), StringComparison.Ordinal)), "missing endpoint selection remains visible", checks);
            vm.Draft.ForceReconnect = !vm.Draft.ForceReconnect;
            saves = store.Saves;
            Check(!await vm.ApplyAsync() && vm.HasChanges && store.Saves == saves, "unavailable endpoint blocks persistence without losing draft", checks);
            var restored = new AudioEndpoint("endpoint-b", "Restored");
            audio.Items = [new("endpoint-a", "Renamed"), restored];
            audio.NotifyEndpoints(); await Until(() => vm.Endpoints.Contains(restored));
            Check(vm.Draft.CaptureEndpoint == "endpoint-b", "restored endpoint keeps stable selection", checks);

            audio.EnumerationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            audio.EnumerationEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var refresh = app.Endpoints.RefreshAsync(); await audio.EnumerationEntered.Task;
            var joined = app.Endpoints.RefreshAsync();
            Check(ReferenceEquals(refresh, joined), "concurrent refresh joins one enumeration", checks);
            audio.Items = [new("endpoint-a", "Newest"), restored];
            audio.NotifyEndpoints(); await Pump();
            enumerations = audio.Enumerations;
            audio.EnumerationGate.TrySetResult(); await refresh;
            Check(audio.Enumerations == enumerations + 1 && audio.MaxEnumerations == 1 && vm.Endpoints.Any(e => e.Name == "Newest"), "changes during enumeration trigger one serial follow-up", checks);
            audio.EnumerationGate = null; audio.EnumerationEntered = null;

            var statusNotifications = 0; var rowNotifications = 0;
            app.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppViewModel.StatusTitle)) statusNotifications++; };
            foreach (var receiver in app.Receivers) receiver.PropertyChanged += (_, _) => rowNotifications++;
            vm.SelectedPage = SettingsViewModel.MonitorPage; await Pump();
            var member = app.MonitorMembers.FirstOrDefault();
            await Task.Delay(800); await Pump();
            Check(statusNotifications == 0 && rowNotifications == 0, "metrics do not refresh status or receiver rows", checks);
            Check(member is not null && ReferenceEquals(member, app.MonitorMembers[0]), "monitor member rows survive metrics updates", checks);

            await app.StopAsync();
            vm.SelectedPage = SettingsViewModel.NetworkPage; await Pump();
            vm.AddManual("Manual LAN", "192.0.2.10", 7000);
            app.Settings.Options("a").Hidden = true;
            vm.Draft.DiscoveryInterfaceId = "01db7c77-1016-49cb-8f5c-f5c194425d15";
            Check(await vm.ApplyAsync(), "network selection persists", checks);
            await Pump();
            Check(vm.NetworkStatus == L.Get("The selected network interface is unavailable. Discovery is paused."), "missing selected interface is reported without fallback", checks);
            Check(discovery.SelectedInterface == vm.Draft.DiscoveryInterfaceId && !app.AllReceivers.Any(r => r.Id == "a") && app.AllReceivers.Any(r => r.IsManual) && app.Settings.ReadOptions("a").Hidden, "network switch clears remote catalog but preserves manual receiver and saved preference", checks);
            vm.Draft.DiscoveryInterfaceId = "";
            var resetApplied = await vm.ApplyAsync();
            Check(resetApplied, "return to all interfaces succeeds: " + vm.Error + "; selected=" + vm.Draft.DiscoveryInterfaceId, checks);
            await Pump();
            Check(app.AllReceivers.Any(r => r.Id == "a"), "all interfaces restores discovery", checks);
            store.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            store.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.Draft.ForceReconnect = !vm.Draft.ForceReconnect;
            apply = vm.ApplyAsync(); await store.SaveEntered.Task;
            var shutdown = app.DisposeAsync().AsTask();
            await Pump(); Check(!shutdown.IsCompleted, "shutdown waits for in-flight persistence", checks);
            store.SaveGate.TrySetResult(); await apply; await shutdown;
            Check(store.Saved.ForceReconnect == vm.Draft.ForceReconnect, "shutdown preserves the committed settings", checks);
        }
        finally { store.SaveGate?.TrySetResult(); audio.EnumerationGate?.TrySetResult(); await vm.ApplicationCompleted; window.Close(); }
        await RunIdentityRegressionAsync(checks, directory);
        await RunAutoConnectIdentityRegressionAsync(checks);
        await RunStereoIdentityRegressionAsync(checks, directory);
    }
    private static async Task RunIdentityRegressionAsync(List<string> checks, string directory)
    {
        var store = new UiSmoke.MemoryStore();
        var original = store.Load(); original.Options("AA:BB:CC:DD:EE:01").Hidden = true;
        original.Options("127.0.0.1:7000").Volume = 55; original.Options("history").StandbySeconds = 20;
        store.Save(original);
        var discovery = new UiSmoke.MockDiscovery { Items = [new("127.0.0.1:7000", "Office", "127.0.0.1")] };
        var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup; await Until(() => app.AllReceivers.Count == 1);
        var window = new SettingsWindow(app); window.Show(); await Pump(); var vm = window.ViewModel;
        try
        {
            Check(vm.Receivers.Single(r => r.Receiver.Id == "127.0.0.1:7000").Detail.StartsWith("127.0.0.1:7000 ·", StringComparison.Ordinal), "endpoint history displays its port only once", checks);
            vm.Draft.ForceReconnect = true; vm.Draft.Options("127.0.0.1:7000").StandbySeconds = 17;
            await app.ToggleAsync(app.AllReceivers[0]); await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            var starts = engine.CreatedCount; store.Fail = true;
            discovery.Items = ReceiverAggregator.Build([new ServiceRecord("Office._airplay._tcp.local", "_airplay._tcp.local", "127.0.0.1", 7000,
                new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:01", ["model"] = "AudioAccessory6,1" })]);
            discovery.Publish(); await Until(() => app.Notice.Contains("模拟保存失败", StringComparison.Ordinal));
            Check(app.AllReceivers.Single().Id == "127.0.0.1:7000" && app.Settings.Receivers.ContainsKey("127.0.0.1:7000") && engine.CreatedCount == starts, "failed identity save preserves catalog preferences and playback", checks);
            store.Fail = false; discovery.Publish();
            await Until(() => app.Snapshot.Receiver?.Id == "aabbccddee01" && vm.Receivers.Count == 2);
            Check(engine.CreatedCount == starts && app.Snapshot.IsActive, "identity promotion updates an active stream without reconnecting", checks);
            Check(app.Settings.ReadOptions("aabbccddee01").Hidden && !app.Settings.Receivers.ContainsKey("127.0.0.1:7000")
                && app.Receivers.Count == 1 && app.Receivers[0].Active && app.Receivers[0].ToggleCommand.CanExecute(null),
                "duplicate settings merge keeps hidden preference while active Stop control remains available", checks);
            Check(vm.Draft.ForceReconnect && vm.Draft.ReadOptions("aabbccddee01").StandbySeconds == 17 && vm.HasChanges, "open settings keeps unapplied changes through identity migration", checks);
            Check(vm.Receivers.Any(r => r.Receiver.Id == "history" && !r.Receiver.Online) && !vm.Receivers.Any(r => r.Receiver.Id == "AA:BB:CC:DD:EE:01"), "confirmed aliases disappear while unrelated offline history remains", checks);
            Check(await vm.ApplyAsync() && app.Settings.ReadOptions("aabbccddee01").StandbySeconds == 17 && engine.CreatedCount == starts, "applying migrated drafts does not resurrect aliases or restart playback", checks);
            vm.Draft.Options("aabbccddee01").Hidden = false;
            Check(await vm.ApplyAsync() && app.Receivers.Count == 1, "merged hidden state remains editable", checks);
            var catalogChanges = 0; app.CatalogChanged += () => catalogChanges++;
            discovery.Items = discovery.Items.Select(r => r with { Aliases = r.Aliases.ToArray() }).ToArray();
            discovery.Publish(); await Pump();
            Check(catalogChanges == 0 && engine.CreatedCount == starts, "equal alias contents do not redraw the catalog or restart playback", checks);
            vm.SelectedPage = SettingsViewModel.ReceiversPage; await Pump(); UiSmoke.Render(window, Path.Combine(directory, "settings-identity-merged.png"), 1);
            vm.AddManual("Manual Office", "127.0.0.1", 7000); await vm.ApplyAsync(); await Pump();
            Check(vm.Receivers.Count(r => r.Receiver.Address == "127.0.0.1") == 2 && vm.Receivers.Any(r => r.IsManual), "manual receivers retain their independent behavior", checks);
        }
        finally { await vm.ApplicationCompleted; window.Close(); }
    }
    private static async Task RunAutoConnectIdentityRegressionAsync(List<string> checks)
    {
        var store = new UiSmoke.MemoryStore();
        var settings = store.Load(); settings.AutoConnectOnDiscover = true; store.Save(settings);
        var discovery = new UiSmoke.MockDiscovery { Items = [new("127.0.0.1:7000", "Office", "127.0.0.1")] };
        var engine = new UiSmoke.MockFactory { FailOpen = true };
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup; await Until(() => app.Snapshot.State == PlaybackState.Error);
        Check(engine.CreatedCount == 1, "automatic connection failure makes one attempt", checks);
        discovery.Items = ReceiverAggregator.Build([new ServiceRecord("Office._airplay._tcp.local", "_airplay._tcp.local", "127.0.0.1", 7000,
            new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:01" })]);
        discovery.Publish(); await Until(() => app.Snapshot.Receiver?.Id == "aabbccddee01");
        await Task.Delay(1200); await Pump();
        Check(engine.CreatedCount == 1 && app.Snapshot.State == PlaybackState.Error && app.Settings.LastReceiverId == "aabbccddee01", "identity promotion preserves failed automatic attempts and updates the error session", checks);
        discovery.Items = []; discovery.Publish(); await Until(() => app.AllReceivers.All(r => !r.Online));
        discovery.Items = ReceiverAggregator.Build([new ServiceRecord("Office._airplay._tcp.local", "_airplay._tcp.local", "127.0.0.1", 7000,
            new Dictionary<string, string> { ["deviceid"] = "AA:BB:CC:DD:EE:01" })]);
        discovery.Publish(); await Until(() => engine.CreatedCount == 2 && app.Snapshot.State == PlaybackState.Error);
        Check(app.AllReceivers.Single().Id == "aabbccddee01", "a real offline-to-online transition permits another automatic attempt", checks);
    }
    private static async Task RunStereoIdentityRegressionAsync(List<string> checks, string directory)
    {
        var store = new UiSmoke.MemoryStore(); var settings = store.Load();
        settings.Options("AA:BB:CC:DD:EE:01").Hidden = true; settings.Options("AA-BB-CC-DD-EE-02").StandbySeconds = 25; store.Save(settings);
        var services = new[] { StereoService("_airplay", "AA:BB:CC:DD:EE:01", "127.0.0.1"), StereoService("_raop", "other-id", "127.0.0.1"), StereoService("_airplay", "AA-BB-CC-DD-EE-02", "127.0.0.2") };
        var discovery = new UiSmoke.MockDiscovery { Items = ReceiverAggregator.Build(services) };
        var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup; await Until(() => app.AllReceivers.Count == 1);
        var window = new SettingsWindow(app); window.Show(); await Pump(); var vm = window.ViewModel;
        try
        {
            Check(vm.Receivers.Count == 1 && vm.Receivers[0].Detail.Contains("2/2", StringComparison.Ordinal) && app.Receivers.Count == 1, "stereo aliases count two physical members and do not add offline member cards", checks);
            Check(app.Settings.ReadOptions("aabbccddee01").Hidden && app.Settings.ReadOptions("aabbccddee02").StandbySeconds == 25 && !app.Settings.ReadOptions("stereo:pair").Hidden, "stereo member preferences remain independent from the visible group", checks);
            vm.SelectedPage = SettingsViewModel.ReceiversPage; await Pump(); UiSmoke.Render(window, Path.Combine(directory, "settings-identity-stereo.png"), 1);
            await app.ToggleAsync(app.AllReceivers[0]); await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            discovery.Items = ReceiverAggregator.Build(services.Take(2)); discovery.Publish();
            await Until(() => app.AllReceivers.Single().Members.Length == 1);
            Check(app.Snapshot.IsActive && app.Snapshot.Receiver?.Members.Length == 2 && app.AllReceivers.Single().Detail.Contains("1/2", StringComparison.Ordinal) && engine.CreatedCount == 1,
                "missing stereo member stays one of two in catalog while owned complete playback continues", checks);
        }
        finally { await vm.ApplicationCompleted; window.Close(); }

        static ServiceRecord StereoService(string type, string id, string address) => new(type == "_raop" ? id + "@Office._raop._tcp.local" : address + "._airplay._tcp.local", type + "._tcp.local", address, 7000,
            new Dictionary<string, string> { ["deviceid"] = id, ["model"] = "AudioAccessory6,1", ["tsid"] = "pair", ["gpn"] = "Office stereo" });
    }
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
