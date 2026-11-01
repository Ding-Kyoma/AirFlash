using System.IO;
using System.Windows;
using System.Windows.Controls;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class ForcedLatencyRegression
{
    private static void Check(bool condition, string name, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add(name);
    }
    private static async Task Pump()
    {
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        await Task.Delay(70);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, timeout.Token);
        await Pump();
    }
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var store = new MemoryStore(); var engine = new MockFactory();
        var receiver = new Receiver("forced", "Latency test", "127.0.0.1");
        await using var app = new AppViewModel(store, new MockDiscovery { Items = [receiver] }, new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher);
        app.SetPanelVisible(true); app.Start(); await app.Startup; await Pump();
        var window = new SettingsWindow(app);
        window.ViewModel.SelectedPage = SettingsViewModel.StreamingPage;
        window.Show(); await Pump();
        try
        {
            var vm = window.ViewModel;
            var force = Descendants(window).OfType<CheckBox>().Single(c => System.Windows.Automation.AutomationProperties.GetName(c) == L.Get("Force fixed latency"));
            Check(vm.Draft.LatencyMode == "adaptive" && force.IsChecked == false && !force.IsEnabled,
                "fresh settings select adaptive and disable inactive forced latency", checks);
            vm.Draft.LatencyMode = "custom"; await Pump();
            Check(force.IsEnabled, "custom mode enables the independent force switch", checks);
            var input = Descendants(window).OfType<TextBox>().Single(t => System.Windows.Automation.AutomationProperties.GetName(t) == L.Get("Custom latency"));
            input.Text = "invalid"; await Pump();
            Check(!vm.ApplyCommand.CanExecute(null), "forced latency preserves custom input validation", checks);
            input.Text = "750"; force.IsChecked = true; await Pump();
            Check(vm.Draft.ForceLatency && vm.Draft.ForcesLatency(receiver.Id), "the force switch changes the draft and receiver inheritance", checks);
            var row = vm.Receivers.Single(r => r.Receiver.Id == receiver.Id);
            row.Options.ForceLatency = false; await Pump();
            Check(!vm.Draft.ForcesLatency(receiver.Id), "receiver override can retain recovery under a forced global target", checks);
            row.Options.ForceLatency = null;
            vm.Draft.LatencyMode = "adaptive"; await Pump();
            Check(!force.IsEnabled && force.IsChecked == true && !row.CanForceLatency && !vm.Draft.ForcesLatency(receiver.Id),
                "adaptive ignores force without erasing the saved fixed choice", checks);
            vm.Draft.LatencyMode = "custom"; await Pump();
            Check(row.CanForceLatency && vm.Draft.CustomBufferMs == 750, "returning to custom restores the fixed target and switch", checks);
            Check(await vm.ApplyAsync(), "forced latency settings can be applied", checks);
            Check(store.Saved.LatencyMode == "custom" && store.Saved.CustomBufferMs == 750 && store.Saved.ForceLatency,
                "applied forced settings persist together", checks);
        }
        finally { await window.ViewModel.ApplicationCompleted; window.Close(); }

        var cancelled = new SettingsWindow(app);
        cancelled.ViewModel.SelectedPage = SettingsViewModel.StreamingPage;
        cancelled.Show(); await Pump();
        cancelled.ViewModel.Draft.ForceLatency = false;
        cancelled.ViewModel.Draft.CustomBufferMs = 900;
        cancelled.ViewModel.CancelCommand.Execute(null); await Pump();
        await cancelled.ViewModel.ApplicationCompleted;
        Check(store.Saved.ForceLatency && store.Saved.CustomBufferMs == 750, "cancel leaves the saved force policy and custom value intact", checks);
        cancelled.Close();

        await app.Session.StartAsync(receiver, app.Settings);
        await Until(() => app.Snapshot.State == PlaybackState.Streaming);
        engine.EmitEvent!(new { @event = "negotiated", receiver_id = receiver.Id, host = receiver.Address,
            codec = "alac", transport = "realtime", timing = "ptp", force_latency = true,
            requested_latency_ms = 750, effective_latency_ms = 750, receiver_latency_min_ms = 1000 });
        engine.EmitEvent!(new { @event = "capture_metrics", metrics = new { underrun_packets = 0 } });
        await Until(() => app.LatencyDetail.Contains(L.Format("Receiver reports a minimum of {0} ms.", 1000)));
        Check(app.Snapshot.State == PlaybackState.Streaming
            && app.LatencyDetail.Contains(L.Get("Force fixed latency"))
            && app.LatencyDetail.Contains(L.Format("Below the receiver's reported minimum: continuing at {0} ms. Playback may be rejected or silent.", 750)),
            "receiver minimum warning stays visible without blocking playback", checks);
        var panel = new ControlPanel(app); panel.Show(); await Pump();
        try { Render(panel, Path.Combine(directory, "forced-latency-warning.png"), 1); }
        finally { panel.Close(); }
    }
}
