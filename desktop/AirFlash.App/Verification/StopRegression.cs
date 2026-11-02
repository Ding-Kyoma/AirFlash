using System.IO;
using System.Windows;
using System.Windows.Controls;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class StopRegression
{
    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(30);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, timeout.Token);
        await Pump();
    }
    public static async Task RunAsync(List<string> checks, string directory)
    {
        foreach (var connecting in new[] { false, true })
        {
            var label = connecting ? "cancel connection" : "stop playback";
            var store = new MemoryStore(); var engine = new MockFactory { AutoReady = !connecting };
            var audio = new MockAudio();
            var receiver = new Receiver("stop-test", "Stop test", "127.0.0.1");
            var other = new Receiver("other", "Other", "127.0.0.2");
            await using var app = new AppViewModel(store, new MockDiscovery { Items = [receiver, other] }, new MockAutostart(), audio, engine, Application.Current.Dispatcher);
            app.Start(); await app.Startup;
            var panel = new ControlPanel(app) { MenuOpen = true }; panel.Show(); await Pump();
            try
            {
                var row = app.Receivers.Single(r => r.Receiver.Id == receiver.Id);
                var button = Descendants(panel).OfType<Button>().Single(b => ReferenceEquals(b.DataContext, row) && ReferenceEquals(b.Command, row.PlaybackCommand));
                if (connecting)
                {
                    store.SaveEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    store.SaveGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                button.Command.Execute(null);
                await Until(() => app.Snapshot.State == (connecting ? PlaybackState.Connecting : PlaybackState.Streaming));
                if (connecting)
                {
                    await store.SaveEntered!.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    Check(!row.ToggleCommand.CanExecute(null) && button.IsEnabled, label + ": cancellation stays enabled while the start command is saving", checks);
                }
                Check(ReferenceEquals(button.Command, app.StopCommand), label + ": active button uses the independent stop command", checks);
                engine.StopEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
                engine.StopGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
                button.Command.Execute(null);
                var stopped = app.StopAsync();
                Check(ReferenceEquals(stopped, app.StopAsync()), label + ": repeated stops share the same operation", checks);
                await engine.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                await Pump();
                Check(app.IsStopping && app.StatusTitle == L.Get("Disconnecting…") && row.IsStopping && !button.IsEnabled,
                    label + ": immediate disconnect feedback replaces the old button state before cleanup completes", checks);
                Render(panel, Path.Combine(directory, connecting ? "cancelling-connection.png" : "stopping-playback.png"), 1);
                Check(Descendants(button).OfType<System.Windows.Shapes.Path>().Any(p => p.Name == "DisconnectSpinner" && p.IsVisible),
                    label + ": the button displays a visible disconnect spinner; panel=" + panel.IsVisible + "; paths=" +
                    string.Join(",", Descendants(button).OfType<System.Windows.Shapes.Path>().Select(p => p.Name + ":" + p.Visibility + ":" + p.IsVisible)), checks);
                Check(app.Snapshot.IsActive && !app.StopCommand.CanExecute(null) && !app.Receivers.Single(r => r.Receiver.Id == other.Id).PlaybackCommand.CanExecute(null),
                    label + ": cleanup cannot appear idle or start an overlapping session", checks);
                audio.DefaultEndpoint = "endpoint-b"; audio.NotifyEndpoints();
                await app.ToggleAsync(other); await Pump();
                Check(engine.CreatedCount == 1, label + ": endpoint notifications and play requests cannot restart during stop", checks);
                engine.EmitEvent!(new { @event = "streaming" });
                await Pump();
                Check(app.IsStopping, label + ": old engine events cannot clear disconnect feedback", checks);
                engine.StopGate.TrySetResult();
                await stopped.WaitAsync(TimeSpan.FromSeconds(3));
                Check(!app.IsStopping && app.Snapshot.State == PlaybackState.Idle && engine.DisposedCount == 1,
                    label + ": completed cleanup restores an idle session", checks);
                store.SaveGate?.TrySetResult();
                await Until(() => button.IsEnabled && ReferenceEquals(button.Command, row.ToggleCommand));
                store.SaveGate = null;
                engine.AutoReady = true;
                button.Command.Execute(null);
                await Until(() => app.Snapshot.State == PlaybackState.Streaming);
                Check(engine.CreatedCount == 2 && engine.DisposedCount == 1 && row.Active,
                    label + ": the same button reconnects using a fresh engine", checks);
                audio.NotifyEndpoints(); await Pump();
                Check(engine.CreatedCount == 2, label + ": endpoint changes observed during stop do not restart the next session", checks);
            }
            finally { store.SaveGate?.TrySetResult(); engine.StopGate?.TrySetResult(); panel.ShutdownPanel(); }
        }
    }
}
