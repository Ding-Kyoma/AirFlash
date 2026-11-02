using System.IO;
using System.Windows;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
using static AirFlash.App.Verification.UiSmoke;
namespace AirFlash.App.Verification;

internal static class AdaptiveLatencyRegression
{
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        while (!predicate()) await Task.Delay(20, timeout.Token);
        await Application.Current.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
    public static async Task RunAsync(List<string> checks, string directory)
    {
        var engine = new MockFactory { SupportsAdaptiveLead = true };
        var left = new Receiver("adaptive-left", "Left", "127.0.0.1");
        var right = new Receiver("adaptive-right", "Right", "127.0.0.2");
        var receiver = new Receiver("stereo:adaptive", "Adaptive stereo", left.Address) { Members = [left, right] };
        await using var app = new AppViewModel(new MemoryStore(), new MockDiscovery { Items = [receiver] }, new MockAutostart(), new MockAudio(), engine, Application.Current.Dispatcher);
        app.Start(); await app.Startup;
        await app.Session.StartAsync(receiver, app.Settings);
        await Until(() => app.Snapshot.State == PlaybackState.Streaming);
        foreach (var member in receiver.Peers)
            engine.EmitEvent!(new { @event = "negotiated", receiver_id = member.Id, host = member.Address,
                codec = "alac", transport = "realtime", timing = "ptp", requested_latency_ms = 120, effective_latency_ms = 120 });
        for (var i = 0; i < 3; i++) engine.EmitEvent!(new { @event = "warning", code = "sender_late_recovered", recovered = true });
        await Until(() => app.Snapshot.PendingLatency == 140);
        if (app.Snapshot.TargetLatency != 120 || !app.StatusDetail.Contains(L.Format("Adjusting to {0} ms…", 140)))
            throw new InvalidOperationException("pending adaptive latency must retain the confirmed target");
        checks.Add("adaptive request is shown as pending without claiming it has applied");
        var command = engine.Commands.Last(c => c.Text("command") == "set_lead").GetProperty("params");
        engine.EmitEvent!(new { @event = "lead_changed", session_epoch = 1, lead_sequence = command.Integer("sequence"),
            baseline_ms = 120, lead_extra_ms = 20, effective_latency_ms = 140, lead_ceiling_ms = 100, live_lead_supported = true });
        await Until(() => app.Snapshot.TargetLatency == 140 && app.Snapshot.PendingLatency is null);
        var expected = L.Format("Startup request {0} ms · negotiated baseline {1} ms · adaptive extra {2} ms · current plan {3} ms", 120, 120, 20, 140);
        if (app.LatencyDetail.Split(expected).Length != 3)
            throw new InvalidOperationException("both stereo members must display the confirmed shared plan and startup request separately");
        checks.Add("both stereo member details show startup 120, baseline 120, extra 20 and current plan 140 ms");
        var panel = new ControlPanel(app); panel.Show();
        try
        {
            await Task.Delay(100);
            Render(panel, Path.Combine(directory, "adaptive-latency-applied.png"), 1);
        }
        finally { panel.Close(); }
    }
}
