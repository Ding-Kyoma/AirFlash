using System.Windows;
using System.Windows.Threading;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class UiOwnershipRecovery
{
    public static async Task RunAsync(List<string> checks)
    {
        foreach (var code in new[] { "preempted", "ownership_unconfirmed" })
        {
            var store = new MemoryStore();
            var settings = store.Load(); settings.AutoConnectOnDiscover = true;
            settings.ForceReconnect = true; settings.MuteWhileStreaming = true; store.Save(settings);
            var receiver = new Receiver("ownership-test", "Ownership test", "192.0.2.10");
            var discovery = new MockDiscovery { Items = [receiver] }; var audio = new MockAudio(); var engine = new MockFactory();
            await using var app = new AppViewModel(store, discovery, new MockAutostart(), audio, engine, Application.Current.Dispatcher);
            app.SetPanelVisible(true); app.Start(); await app.Startup;
            await app.TryAutoConnectAsync();
            await Until(() => app.Snapshot.State == PlaybackState.Streaming);
            Check(engine.Commands.Single(c => c.Text("command") == "start").GetProperty("params").Text("intent") == "automatic",
                "discovery connection carries automatic intent", checks);
            var restores = audio.Restores;
            engine.EmitEvent!(new { @event = "error", code, host = receiver.Address, channel = "preemption", retryable = false, message = "synthetic ownership loss" });
            await Until(() => app.CanReconnect && !app.Snapshot.IsActive && audio.Restores > restores);
            Check(app.Snapshot.State == (code == "preempted" ? PlaybackState.Preempted : PlaybackState.Error),
                code + " exposes manual reconnect and restores local audio", checks);
            discovery.Publish(); await app.TryAutoConnectAsync();
            var baseline = app.Settings.Clone(); var draft = baseline.Clone(); draft.AutoConnectOnDiscover = false;
            await app.ApplyAsync(baseline, draft);
            baseline = app.Settings.Clone(); draft = baseline.Clone(); draft.AutoConnectOnDiscover = true;
            await app.ApplyAsync(baseline, draft); discovery.Publish(); await app.TryAutoConnectAsync();
            Check(engine.CreatedCount == 1 && app.CanReconnect,
                code + " survives rediscovery and automatic-connection settings refresh", checks);
            app.ReconnectCommand.Execute(null);
            await Until(() => engine.CreatedCount == 2 && app.Snapshot.State == PlaybackState.Streaming);
            Check(engine.Commands.Last(c => c.Text("command") == "start").GetProperty("params").Text("intent") == "manual"
                && !app.CanReconnect, code + " resumes only through manual reconnect", checks);
        }
    }
    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
