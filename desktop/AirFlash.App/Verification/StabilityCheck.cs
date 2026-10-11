using System.Windows;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.ViewModels;
using AirFlash.Core;

namespace AirFlash.App.Verification;

internal static class StabilityCheck
{
    public static async Task<int> RunAsync(string[] args)
    {
        AppPaths.DataDirectory = Path.Combine(Path.GetTempPath(), "AirFlash-stability-check", Guid.NewGuid().ToString("N"));
        var checks = new List<string>();
        try
        {
            foreach (var selectedAdapter in new[] { false, true })
                foreach (var stereo in new[] { false, true })
                    await VerifyAsync(selectedAdapter, stereo, checks);
            await VerifyDelayedDiscoveryAsync(false, checks);
            await VerifyDelayedDiscoveryAsync(true, checks);
            App.WriteOutput(args, new { ok = true, checks, note = "Simulated discovery, audio, startup and engine; no real speaker access." });
            return 0;
        }
        catch (Exception error)
        {
            App.WriteOutput(args, new { ok = false, checks, error = error.ToString() });
            return 1;
        }
    }

    private static async Task VerifyAsync(bool selectedAdapter, bool stereo, List<string> checks)
    {
        var label = $"{(selectedAdapter ? "selected adapter" : "all adapters")}, {(stereo ? "stereo" : "single")}";
        var left = new Receiver("left", "Left", "127.0.0.1");
        var right = new Receiver("right", "Right", "127.0.0.2");
        var receiver = stereo ? left with { Id = "stereo:pair", Name = "Pair", Members = [left, right] } : left;
        var store = new UiSmoke.MemoryStore();
        var settings = store.Load();
        settings.DiscoveryInterfaceId = selectedAdapter ? "01db7c77-1016-49cb-8f5c-f5c194425d15" : "";
        settings.ForceReconnect = true;
        settings.Options(receiver.Id).AutoConnect = true;
        store.Save(settings);
        var discovery = new UiSmoke.MockDiscovery { Items = [receiver] };
        var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup;
        await Until(() => app.Snapshot.State == PlaybackState.Streaming);
        var starts = engine.CreatedCount;

        if (stereo)
        {
            discovery.Items = [receiver with { Members = [left] }]; discovery.Publish();
            await Until(() => app.AllReceivers.Any(r => r.Id == receiver.Id && r.Members.Length == 1));
            await Pump();
            Check(app.Snapshot.State == PlaybackState.Streaming && engine.CreatedCount == starts && engine.DisposedCount == 0,
                label + ": partial discovery retains the working transport", checks);
            Check(app.Receivers.Single(r => r.Receiver.Id == receiver.Id).CanPlay && app.StopCommand.CanExecute(null),
                label + ": incomplete discovery retains Stop control", checks);
        }

        discovery.Items = []; discovery.Publish();
        await Until(() => app.AllReceivers.All(r => !r.Online));
        await Pump();
        Check(app.Snapshot.State == PlaybackState.Streaming && engine.CreatedCount == starts && engine.DisposedCount == 0,
            label + ": empty discovery retains the working transport", checks);
        Check(app.Receivers.Any(r => r.Receiver.Id == receiver.Id && r.Active && r.CanPlay) && app.StopCommand.CanExecute(null),
            label + ": missing receiver retains Stop control", checks);

        discovery.Items = [receiver]; discovery.Publish();
        await Until(() => app.AllReceivers.Any(r => r.Id == receiver.Id && r.Online && r.Complete));
        await Task.Delay(1100);
        Check(app.Snapshot.State == PlaybackState.Streaming && engine.CreatedCount == starts,
            label + ": rediscovery does not create another audio session", checks);

        discovery.Items = []; discovery.Publish(); await Until(() => app.AllReceivers.All(r => !r.Online));
        await Pump();
        app.Receivers.Single(r => r.Receiver.Id == receiver.Id).ToggleCommand.Execute(null);
        await Until(() => app.Snapshot.State == PlaybackState.Idle);
        await Pump();
        Check(engine.DisposedCount == engine.CreatedCount && app.Receivers.All(r => r.Receiver.Id != receiver.Id),
            label + ": manual Stop releases the engine and removes the unavailable row", checks);
        discovery.Items = [receiver]; discovery.Publish(); await Task.Delay(1100); await Pump();
        Check(app.Snapshot.State == PlaybackState.Idle && engine.CreatedCount == starts,
            label + ": rediscovery respects manual Stop", checks);
    }

    private static async Task VerifyDelayedDiscoveryAsync(bool pairing, List<string> checks)
    {
        var original = new Receiver("127.0.0.1:7000", "Original", "127.0.0.1");
        var replacement = new Receiver("replacement", "Replacement", "127.0.0.2");
        var store = new UiSmoke.MemoryStore();
        var settings = store.Load();
        settings.Options(original.Id).Volume = 30;
        store.Save(settings);
        var discovery = new UiSmoke.MockDiscovery { Items = [original] };
        var engine = new UiSmoke.MockFactory();
        await using var app = new AppViewModel(store, discovery, new UiSmoke.MockAutostart(), new UiSmoke.MockAudio(), engine, Application.Current.Dispatcher);
        app.SetPanelVisible(true); app.Start(); await app.Startup;
        await Until(() => app.AllReceivers.Count == 1);
        await app.ToggleAsync(app.AllReceivers[0]);
        await Until(() => app.Snapshot.State == PlaybackState.Streaming);
        store.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        store.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var promoted = original with { Id = "aabbccddee01", DeviceId = "AA:BB:CC:DD:EE:01" };
            discovery.Items = pairing ? [promoted, replacement with { Name = "Rediscovered replacement" }] : [promoted];
            discovery.Publish();
            await store.SaveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (pairing)
                await app.Session.PairAsync(replacement, app.Settings.Clone(), (_, _) => Task.FromResult<string?>(null));
            else
                await app.Session.StartAsync(replacement, app.Settings.Clone());
            var state = pairing ? PlaybackState.Pairing : PlaybackState.Streaming;
            await Until(() => app.Snapshot.State == state && app.Snapshot.Receiver?.Id == replacement.Id
                && (!pairing || engine.Commands.Any(c => c.Text("command") == "pair")));
            var starts = engine.CreatedCount;
            store.SaveGate.TrySetResult();
            await Until(() => app.AllReceivers.Any(r => r.Id == promoted.Id));
            // Queue behind reconciliation on the settings gate to observe its completed result.
            await app.ApplyAsync(app.Settings.Clone(), app.Settings.Clone());
            await Pump();
            var label = pairing ? "delayed discovery during Pairing" : "delayed discovery after receiver switch";
            Check(app.Session.Snapshot.State == state && app.Session.Snapshot.Receiver == replacement,
                label + ": preserves the newer target", checks);
            Check(engine.CreatedCount == starts && engine.DisposedCount == starts - 1,
                label + ": does not replace the newer engine", checks);
        }
        finally { store.SaveGate.TrySetResult(); }
    }

    private static void Check(bool value, string name, List<string> checks)
    {
        if (!value) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        await Task.Delay(100);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(20, timeout.Token);
    }
}
