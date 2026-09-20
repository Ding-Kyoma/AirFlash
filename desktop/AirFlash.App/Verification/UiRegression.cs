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
            retries.Text = "invalid"; await Pump(); vm.SelectedPage = 2;
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

            vm.SelectedPage = 2; await vm.LoadEndpointsAsync(false);
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
            vm.SelectedPage = 4; await Pump();
            var member = app.MonitorMembers.FirstOrDefault();
            await Task.Delay(800); await Pump();
            Check(statusNotifications == 0 && rowNotifications == 0, "metrics do not refresh status or receiver rows", checks);
            Check(member is not null && ReferenceEquals(member, app.MonitorMembers[0]), "monitor member rows survive metrics updates", checks);

            await app.StopAsync();
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
    }
    private static void Check(bool value, string name, List<string> checks) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
    private static async Task Pump() { await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
}
