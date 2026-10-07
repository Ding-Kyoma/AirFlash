using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

// These fixtures never construct platform services or open an engine/network connection.
internal static class UiSessionLifecycle
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);
    public static async Task RunAsync(List<string> checks, string directory)
    {
        await IdentityOwnershipAsync(checks);
        await IdentityPromotionAsync(checks);
        await AutomaticOwnershipAsync(checks);
        await AutomaticEligibilityAsync(checks);
        await AutomaticSaveFailureAsync(checks);
        await StopWaitingForAdmissionAsync(checks);
        await QueuedAutomaticAsync(checks);
        await AutomaticFallbackAsync(checks);
        await ManualSelectionAsync(checks);
        await RetainedStatesAsync(checks, directory);
        await RetainedPinCancellationAsync(checks);
        await AdapterRetentionAsync(checks);
        await FaultAndMuteAsync(checks);
        await ShutdownDuringAutomaticSaveAsync(checks);
    }
    private static Receiver Pod(string id, string address = "192.0.2.10", int port = 7000) => new(id, id, address, port);
    private static Receiver Pair(string id = "stereo:fixture") => Pod(id) with
    {
        StereoId = "fixture", Members = [Pod("left") with { IsLeader = true }, Pod("right", "192.0.2.11")]
    };
    private static AppSettings Settings(bool automatic = false) => new()
    {
        AutoConnectOnDiscover = automatic, ForceReconnect = false, MuteWhileStreaming = false, LastReceiverId = "previous"
    };
    private static void SeedAliases(AppSettings settings, params Receiver[] receivers)
    {
        foreach (var receiver in receivers.SelectMany(r => r.Peers))
            if (ReceiverIdentity.IsBroadcast(receiver.Id)) settings.ReceiverAliases[receiver.Id] = receiver.Id;
    }
    private static void Check(bool condition, string message, List<string> checks)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks.Add(message);
    }
    private static Task DrainAsync() => Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task StateAsync(Fixture fixture, Func<SessionSnapshot, bool> predicate)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot snapshot) { if (predicate(snapshot)) arrived.TrySetResult(); }
        fixture.App.Session.Changed += Changed;
        try
        {
            if (predicate(fixture.App.Session.Snapshot)) arrived.TrySetResult();
            await arrived.Task.WaitAsync(Limit);
            await DrainAsync();
        }
        finally { fixture.App.Session.Changed -= Changed; }
    }
    private static Task StateAsync(Fixture fixture, PlaybackState state) => StateAsync(fixture, snapshot => snapshot.State == state);

    private static async Task IdentityOwnershipAsync(List<string> checks)
    {
        foreach (var scenario in new[] { "absent", "changed", "same id", "save failure" })
        {
            var a = Pod("old-a"); var b = Pod("manual-b", "192.0.2.20", 7001) with { IsManual = true };
            var settings = Settings(); settings.ManualReceivers.Add(new(b.Id, b.Name, b.Address, b.Port));
            settings.Options(b.Id).LatencyMode = "custom"; settings.Options(b.Id).CustomBufferMs = 333;
            SeedAliases(settings, a);
            await using var fixture = await Fixture.CreateAsync(settings, [a]);
            await fixture.App.ToggleAsync(a); await StateAsync(fixture, PlaybackState.Streaming);
            var old = fixture.Engine.Connections.Single();
            var pin = new PendingPin(); var pairing = new ScriptedConnection(false);
            fixture.Engine.Enqueue(pairing);
            var gate = fixture.Store.Hold();
            var c = Pod("new-c", "192.0.2.30");
            var next = scenario is "changed" or "same id" ? new[] { a with { Address = "192.0.2.12", Port = 7009 }, c } : new[] { c };
            var reconcile = fixture.App.ApplyDiscoveryAsync(next);
            try
            {
                await gate.Entered.Task.WaitAsync(Limit);
                old.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = 7 } });
                await StateAsync(fixture, snapshot => snapshot.Metrics?.Underruns == 7);
                var selected = scenario == "same id" ? a with { Address = "192.0.2.25", Port = 7010 } : b;
                await fixture.App.PairAsync(selected, pin.RequestAsync);
                await pairing.PairSent.Task.WaitAsync(Limit);
                pairing.Emit(new { @event = "pin_required" });
                await pin.Entered.Task.WaitAsync(Limit);
                await StateAsync(fixture, PlaybackState.Pairing);
                var owned = fixture.App.Session.Capture();
                fixture.Store.Fail = scenario == "save failure";
                gate.Release.TrySetResult();
                try { await reconcile; } catch (IOException) when (fixture.Store.Fail) { }
                await DrainAsync();
                var current = fixture.App.Session.Capture();
                Check(current.Owner == owned.Owner && current.Snapshot.State == PlaybackState.Pairing &&
                    current.Snapshot.Receiver?.Id == selected.Id && current.Snapshot.Receiver.Address == selected.Address &&
                    ReferenceEquals(current.Snapshot.Diagnostics, owned.Snapshot.Diagnostics) && !pairing.Disposed &&
                    !pairing.Commands.Any(command => command.Name == "stop") && !pin.Cancelled,
                    "delayed identity reconciliation preserves the new pairing owner: " + scenario, checks);
                if (scenario != "same id")
                    Check(current.Snapshot.TargetLatency == 333, "stale identity work preserves manual pairing settings: " + scenario, checks);
                if (fixture.Store.Fail)
                    Check(fixture.App.AllReceivers.Any(receiver => receiver.Id == a.Id) &&
                        !fixture.App.Settings.ReceiverAliases.ContainsKey(c.Id), "failed identity save leaves the catalog and settings unchanged", checks);
            }
            finally
            {
                gate.Release.TrySetResult(); fixture.Store.Fail = false;
                pin.Release.TrySetResult(null); await fixture.App.StopAsync();
            }
        }
    }
    private static async Task IdentityPromotionAsync(List<string> checks)
    {
        foreach (var restart in new[] { false, true })
        {
            var endpoint = Pod("192.0.2.10:7000"); var settings = Settings();
            if (restart) settings.Options("aabbccddee01").LatencyMode = "realtime";
            else { settings.Options(endpoint.Id).LatencyMode = "custom"; settings.Options(endpoint.Id).CustomBufferMs = 333; }
            await using var fixture = await Fixture.CreateAsync(settings, [endpoint]);
            await fixture.App.ToggleAsync(endpoint); await StateAsync(fixture, PlaybackState.Streaming);
            var process = fixture.Engine.Connections.Single(); var owner = fixture.App.Session.Capture().Owner;
            var gate = fixture.Store.Hold();
            var promoted = endpoint with { Id = "aabbccddee01", DeviceId = "AA:BB:CC:DD:EE:01", Aliases = [endpoint.Id] };
            var reconcile = fixture.App.ApplyDiscoveryAsync([promoted]);
            try
            {
                await gate.Entered.Task.WaitAsync(Limit);
                process.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = 9 } });
                await StateAsync(fixture, snapshot => snapshot.Metrics?.Underruns == 9);
                Check(fixture.App.Session.Capture().Owner == owner, "metrics do not replace desktop lifecycle ownership", checks);
                gate.Release.TrySetResult(); await reconcile;
                await StateAsync(fixture, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Receiver?.Id == promoted.Id);
                Check(fixture.Engine.OpenCount == (restart ? 2 : 1) && fixture.App.Snapshot.TargetLatency == (restart ? 120 : 333),
                    restart ? "same-owner identity migration restarts once for changed effective latency" : "same-owner identity promotion survives metrics without restarting", checks);
            }
            finally { gate.Release.TrySetResult(); }
        }
    }
    private static async Task AutomaticOwnershipAsync(List<string> checks)
    {
        foreach (var scenario in new[] { "pairing", "streaming", "user stop", "same id" })
        {
            var a = Pod("auto-a"); var b = Pod("manual-b", "192.0.2.20", 7001) with { IsManual = true };
            var settings = Settings(true); settings.ManualReceivers.Add(new(b.Id, b.Name, b.Address, b.Port));
            settings.Options(b.Id).AutoConnect = false; SeedAliases(settings, a);
            await using var fixture = await Fixture.CreateAsync(settings, [a]);
            var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 37;
            var automatic = fixture.App.TryAutoConnectAsync(); var pin = new PendingPin();
            var pairing = new ScriptedConnection(false) { CompletePair = scenario == "streaming" };
            fixture.Engine.Enqueue(pairing);
            try
            {
                await gate.Entered.Task.WaitAsync(Limit);
                Check(fixture.Engine.OpenCount == 0 && !automatic.IsCompleted, "automatic flush is held before opening a connection: " + scenario, checks);
                var selected = scenario == "same id" ? a : b;
                await fixture.App.PairAsync(selected, scenario == "streaming" ? (_, _) => Task.FromResult<string?>("1234") : pin.RequestAsync);
                await pairing.PairSent.Task.WaitAsync(Limit);
                if (scenario is "pairing" or "same id")
                {
                    pairing.Emit(new { @event = "pin_required" }); await pin.Entered.Task.WaitAsync(Limit);
                }
                await StateAsync(fixture, scenario == "streaming" ? PlaybackState.Streaming : PlaybackState.Pairing);
                var owned = fixture.App.Session.Capture(); var opens = fixture.Engine.OpenCount;
                if (scenario == "user stop")
                {
                    Check(!pin.Entered.Task.IsCompleted && fixture.App.StopCommand.CanExecute(null), "ordinary Stop is available before a PIN prompt opens", checks);
                    await fixture.App.StopAsync(); await StateAsync(fixture, PlaybackState.Idle);
                }
                gate.Release.TrySetResult(); await automatic; await DrainAsync();
                Check(fixture.Engine.OpenCount == opens && fixture.Store.Saved.MasterVolume == 37 && fixture.App.Settings.LastReceiverId == "previous",
                    "rejected automatic work preserves valid persistence without recording an attempt: " + scenario, checks);
                if (scenario == "user stop")
                {
                    await fixture.App.TryAutoConnectAsync();
                    Check(fixture.Engine.OpenCount == opens && fixture.App.Snapshot.State == PlaybackState.Idle,
                        "releasing a delayed automatic flush cannot undo user Stop suppression", checks);
                }
                else
                {
                    var current = fixture.App.Session.Capture();
                    Check(current.Owner == owned.Owner && current.Snapshot.Receiver?.Id == selected.Id &&
                        current.Snapshot.State == owned.Snapshot.State && !pin.Cancelled,
                        "delayed automatic admission cannot replace explicit ownership: " + scenario, checks);
                    await fixture.App.StopAsync(false); pin.Release.TrySetResult(null);
                    await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
                    Check(fixture.App.Snapshot.Receiver?.Id == a.Id && fixture.Engine.OpenCount == opens + 1,
                        "a rejected automatic request leaves its candidate available for later valid admission: " + scenario, checks);
                }
            }
            finally { gate.Release.TrySetResult(); pin.Release.TrySetResult(null); }
        }
    }
    private static async Task AutomaticEligibilityAsync(List<string> checks)
    {
        foreach (var change in new[] { "removed", "incomplete", "hidden", "disabled" })
        {
            var group = Pair("stereo:eligible"); var settings = Settings(true); SeedAliases(settings, group);
            await using var fixture = await Fixture.CreateAsync(settings, [group]);
            var owner = fixture.App.Session.Capture().Owner;
            var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 37;
            Task transaction = Task.CompletedTask;
            fixture.App.BeforeAutomaticAdmission = () => transaction;
            var automatic = fixture.App.TryAutoConnectAsync();
            try
            {
                await gate.Entered.Task.WaitAsync(Limit);
                if (change == "removed") transaction = fixture.App.ApplyDiscoveryAsync([]);
                else if (change == "incomplete") transaction = fixture.App.ApplyDiscoveryAsync([group with { Members = [group.Members[0]] }]);
                else
                {
                    var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
                    if (change == "hidden") draft.Options(group.Id).Hidden = true;
                    else draft.Options(group.Id).AutoConnect = false;
                    transaction = fixture.App.ApplyAsync(baseline, draft);
                }
                gate.Release.TrySetResult(); await transaction; await automatic;
                Check(fixture.App.Session.Capture().Owner == owner && fixture.Engine.OpenCount == 0 &&
                    fixture.Store.Saved.MasterVolume == 37 && fixture.App.Settings.LastReceiverId == "previous",
                    "automatic admission rechecks eligibility after persistence with the same lifecycle owner: " + change, checks);
                fixture.App.BeforeAutomaticAdmission = null;
                if (change is "removed" or "incomplete") await fixture.App.ApplyDiscoveryAsync([group]);
                else
                {
                    var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
                    draft.Options(group.Id).Hidden = false; draft.Options(group.Id).AutoConnect = null;
                    await fixture.App.ApplyAsync(baseline, draft);
                }
                await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
                Check(fixture.Engine.OpenCount == 1, "rejected eligibility changes do not consume the receiver's automatic attempt: " + change, checks);
            }
            finally { gate.Release.TrySetResult(); fixture.App.BeforeAutomaticAdmission = null; }
        }
    }
    private static async Task StopWaitingForAdmissionAsync(List<string> checks)
    {
        var a = Pod("auto-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var restore = fixture.Audio.HoldRestore();
        var updating = fixture.App.Session.UpdateSettingsAsync(settings);
        try
        {
            await restore.Entered.Task.WaitAsync(Limit);
            // This call reaches the held controller semaphore synchronously: no dirty save or admission hook intervenes.
            var automatic = fixture.App.TryAutoConnectAsync();
            Check(!automatic.IsCompleted, "automatic admission waits behind serialized controller work", checks);
            var stopping = fixture.App.StopAsync();
            Check(!stopping.IsCompleted, "user Stop invalidates automatic intent before its controller wait finishes", checks);
            restore.Release.TrySetResult(); await Task.WhenAll(updating, automatic, stopping);
            await fixture.App.TryAutoConnectAsync();
            Check(fixture.Engine.OpenCount == 0 && fixture.App.Session.Snapshot.State == PlaybackState.Idle &&
                fixture.App.Settings.LastReceiverId == "previous", "Stop while automatic admission waits for serialization prevents Start and preserves suppression", checks);
        }
        finally { restore.Release.TrySetResult(); }
    }
    private static async Task AutomaticSaveFailureAsync(List<string> checks)
    {
        var a = Pod("save-failure-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 37;
        var automatic = fixture.App.TryAutoConnectAsync();
        var failed = false;
        try
        {
            await gate.Entered.Task.WaitAsync(Limit); fixture.Store.Fail = true; gate.Release.TrySetResult();
            try { await automatic; } catch (IOException) { failed = true; }
        }
        finally { fixture.Store.Fail = false; gate.Release.TrySetResult(); }
        Check(failed && fixture.Engine.OpenCount == 0 && fixture.App.Settings.LastReceiverId == "previous" && fixture.Store.Saved.MasterVolume == 100,
            "failed automatic persistence prevents admission and leaves attempts and last-used untouched", checks);
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.Engine.OpenCount == 1 && fixture.Store.Saved.MasterVolume == 37 && fixture.App.Settings.LastReceiverId == a.Id,
            "a candidate rejected by save failure can make a later valid automatic attempt", checks);
    }
    private static async Task QueuedAutomaticAsync(List<string> checks)
    {
        var a = Pod("auto-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 41;
        var requests = new[] { fixture.App.TryAutoConnectAsync(), fixture.App.TryAutoConnectAsync(), fixture.App.TryAutoConnectAsync() };
        try
        {
            await gate.Entered.Task.WaitAsync(Limit); gate.Release.TrySetResult();
            await Task.WhenAll(requests); await StateAsync(fixture, PlaybackState.Streaming);
            Check(fixture.Engine.OpenCount == 1 && fixture.App.Settings.LastReceiverId == a.Id && fixture.Store.Saved.MasterVolume == 41,
                "multiple queued automatic requests admit one current Idle attempt", checks);
        }
        finally { gate.Release.TrySetResult(); }
    }
    private static async Task AutomaticFallbackAsync(List<string> checks)
    {
        var a = Pod("preferred-a"); var b = Pod("candidate-b", "192.0.2.20");
        var c = Pod("192.0.2.30:7000", "192.0.2.30"); var disabled = Pod("disabled", "192.0.2.40");
        var settings = Settings(true); settings.LastReceiverId = a.Id; settings.Options(disabled.Id).AutoConnect = false;
        SeedAliases(settings, a, b, c, disabled);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        fixture.Engine.FailNext = true;
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Error);
        Check(fixture.Engine.OpenCount == 1 && fixture.App.Settings.LastReceiverId == a.Id,
            "an admitted automatic connection failure still records an attempted receiver", checks);
        await fixture.App.ApplyDiscoveryAsync([a, b, disabled]);
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.Engine.OpenCount == 2 && fixture.App.Snapshot.Receiver?.Id == b.Id,
            "an attempted preferred receiver does not block an unattempted fallback from Error", checks);
        await fixture.App.StopAsync(false); await fixture.App.TryAutoConnectAsync();
        Check(fixture.Engine.OpenCount == 2, "all attempted candidates and explicit false overrides produce no automatic connection", checks);

        var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone(); draft.LastReceiverId = c.Id;
        await fixture.App.ApplyAsync(baseline, draft); await fixture.App.ApplyDiscoveryAsync([a, b, c, disabled]);
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.App.Snapshot.Receiver?.Id == c.Id && fixture.Engine.OpenCount == 3,
            "last-used preference ranks the remaining unattempted candidates", checks);
        await fixture.App.StopAsync(false);
        var promoted = c with { Id = "promoted-c", Aliases = [c.Id] };
        await fixture.App.ApplyDiscoveryAsync([a, b, promoted, disabled]); await fixture.App.TryAutoConnectAsync();
        Check(fixture.Engine.OpenCount == 3 && fixture.App.Settings.LastReceiverId == promoted.Id,
            "identity aliases migrate attempted status rather than retrying the same online receiver", checks);
        await fixture.App.ApplyDiscoveryAsync([a, b, disabled]); await fixture.App.ApplyDiscoveryAsync([a, b, promoted, disabled]);
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.App.Snapshot.Receiver?.Id == promoted.Id && fixture.Engine.OpenCount == 4,
            "a permitted offline-to-online transition resets one canonical receiver's attempt", checks);

        var group = Pair("stereo:transition"); await fixture.App.StopAsync(false);
        await fixture.App.ApplyDiscoveryAsync([a, b, promoted, disabled, group]);
        fixture.Engine.FailNext = true; await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Error);
        var opens = fixture.Engine.OpenCount;
        await fixture.App.ApplyDiscoveryAsync([a, b, promoted, disabled, group with { Members = [group.Members[0]] }]);
        await fixture.App.TryAutoConnectAsync();
        Check(fixture.Engine.OpenCount == opens, "an incomplete stereo transition cannot admit a new automatic start", checks);
        await fixture.App.ApplyDiscoveryAsync([a, b, promoted, disabled, group]); await fixture.App.TryAutoConnectAsync();
        await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.App.Snapshot.Receiver?.Id == group.Id && fixture.Engine.OpenCount == opens + 1,
            "incomplete-to-complete recovery resets only the eligible group's automatic attempt", checks);
        await fixture.App.StopAsync();
        baseline = fixture.App.Settings.Clone(); draft = baseline.Clone(); draft.AutoConnectOnDiscover = false;
        await fixture.App.ApplyAsync(baseline, draft); await fixture.App.TryAutoConnectAsync();
        Check(fixture.Engine.OpenCount == opens + 1, "user Stop remains suppressed when automatic connection is disabled", checks);
        baseline = fixture.App.Settings.Clone(); draft = baseline.Clone(); draft.AutoConnectOnDiscover = true;
        await fixture.App.ApplyAsync(baseline, draft); await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.Engine.OpenCount == opens + 2 && fixture.App.Snapshot.Receiver?.Id == group.Id,
            "explicitly enabling global automatic connection resets suppression and prior attempts", checks);
    }
    private static async Task ManualSelectionAsync(List<string> checks)
    {
        foreach (var port in new[] { 7000, 7001 })
        foreach (var condition in new[] { "complete", "incomplete", "hidden" })
        {
            var group = Pair(); if (condition == "incomplete") group = group with { Members = [group.Members[0]] };
            var manual = Pod("manual-m", "192.0.2.10", port) with { IsManual = true };
            var settings = Settings(); settings.MasterVolume = 10;
            settings.ManualReceivers.Add(new(manual.Id, manual.Name, manual.Address, port));
            settings.Options(manual.Id).LatencyMode = "custom"; settings.Options(manual.Id).CustomBufferMs = 333;
            settings.Options(group.Id).LatencyMode = "buffered";
            settings.Options(group.Id).Hidden = condition == "hidden"; SeedAliases(settings, group);
            await using var fixture = await Fixture.CreateAsync(settings, [group]);
            await fixture.App.ToggleAsync(fixture.App.AllReceivers.Single(receiver => receiver.Id == manual.Id));
            await StateAsync(fixture, PlaybackState.Streaming);
            var command = fixture.Engine.Connections.Single().Commands.Single(command => command.Name == "start");
            var peers = command.Parameters.GetProperty("peers");
            Check(peers.GetArrayLength() == 1 && peers[0].Text("host") == manual.Address && peers[0].Integer("port") == port &&
                command.Parameters.Integer("latency_ms") == 333 && command.Parameters.Number("gain") == .1 &&
                fixture.App.Snapshot.Receiver?.Id == manual.Id && fixture.App.Settings.LastReceiverId == manual.Id &&
                fixture.App.Settings.ReadOptions(group.Id).LatencyMode == "buffered",
                $"manual selection keeps its exact endpoint, identity and options beside a {condition} stereo group at port {port}", checks);
            await fixture.App.ApplyDiscoveryAsync([]);
            Check(fixture.App.Snapshot.IsActive && fixture.App.AllReceivers.Single(receiver => receiver.IsManual).Id == manual.Id,
                "manual persistence and playback survive empty discovery: " + condition + "/" + port, checks);
        }
        var complete = Pair(); var options = Settings(); SeedAliases(options, complete);
        await using var routed = await Fixture.CreateAsync(options, [complete]);
        await routed.App.ToggleAsync(routed.App.AllReceivers.Single()); await StateAsync(routed, PlaybackState.Streaming);
        Check(routed.Engine.Connections.Single().Commands.Single(command => command.Name == "start").Parameters.GetProperty("peers").GetArrayLength() == 2,
            "explicit stereo selection retains its two-peer transport", checks);
        await routed.App.StopAsync(false);
        await routed.App.ToggleAsync(routed.App.AllReceivers.Single().Members[0]); await StateAsync(routed, PlaybackState.Streaming);
        Check(routed.App.Snapshot.Receiver?.Id == complete.Id && routed.Engine.Connections.Last().Commands.Single(command => command.Name == "start").Parameters.GetProperty("peers").GetArrayLength() == 2,
            "a confirmed discovered member routes to its unique stereo owner", checks);

        await routed.App.StopAsync(false);
        var aliasGroup = complete with { Members = [complete.Members[0] with { Aliases = ["historical-member"] }, complete.Members[1]] };
        await routed.App.ApplyDiscoveryAsync([aliasGroup]);
        await routed.App.ToggleAsync(Pod("historical-member", "192.0.2.50")); await StateAsync(routed, PlaybackState.Streaming);
        Check(routed.App.Snapshot.Receiver?.Id == complete.Id && routed.Engine.Connections.Last().Commands.Single(command => command.Name == "start").Parameters.GetProperty("peers")[0].Text("host") == "192.0.2.10",
            "a confirmed broadcast alias routes to the group's current transport rather than an old address", checks);

        foreach (var port in new[] { 7000, 7001 })
        {
            var independent = Pod("independent", port: port); var sharedSettings = Settings(); SeedAliases(sharedSettings, complete, independent);
            await using var shared = await Fixture.CreateAsync(sharedSettings, [complete, independent]);
            await shared.App.ToggleAsync(shared.App.AllReceivers.Single(receiver => receiver.Id == independent.Id)); await StateAsync(shared, PlaybackState.Streaming);
            var command = shared.Engine.Connections.Single().Commands.Single(command => command.Name == "start");
            Check(shared.App.Snapshot.Receiver?.Id == independent.Id && command.Parameters.GetProperty("peers").GetArrayLength() == 1 &&
                command.Parameters.GetProperty("peers")[0].Integer("port") == port,
                "shared address without confirmed member identity cannot substitute a stereo group at port " + port, checks);
        }
        var ambiguousSettings = Settings();
        var first = Pair("stereo:first");
        var second = Pair("stereo:second") with { Members = [first.Members[0] with { Address = "192.0.2.30" }, Pod("other-right", "192.0.2.31")] };
        SeedAliases(ambiguousSettings, first, second);
        await using var ambiguous = await Fixture.CreateAsync(ambiguousSettings, [first, second]);
        var rejected = false;
        try { await ambiguous.App.ToggleAsync(first.Members[0]); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected && ambiguous.Engine.OpenCount == 0 && ambiguous.App.Settings.LastReceiverId == "previous",
            "ambiguous discovered member ownership rejects before any engine command or last-used change", checks);
        await ambiguous.App.ToggleAsync(ambiguous.App.AllReceivers.Single(receiver => receiver.Id == second.Id)); await StateAsync(ambiguous, PlaybackState.Streaming);
        Check(ambiguous.App.Snapshot.Receiver?.Id == second.Id,
            "an explicitly selected group remains selected even when a member identity has another group owner", checks);
    }
    private static async Task RetainedStatesAsync(List<string> checks, string directory)
    {
        foreach (var state in new[] { PlaybackState.Connecting, PlaybackState.Pairing, PlaybackState.Streaming, PlaybackState.Standby })
        {
            var group = Pair(); var settings = Settings(); settings.MuteWhileStreaming = true;
            settings.StandbyEnabled = true; settings.StandbySilenceSeconds = 5; SeedAliases(settings, group);
            await using var fixture = await Fixture.CreateAsync(settings, [group]);
            var process = new ScriptedConnection(state is PlaybackState.Streaming or PlaybackState.Standby);
            fixture.Engine.Enqueue(process);
            if (state == PlaybackState.Pairing) await fixture.App.PairAsync(group, (_, _) => throw new InvalidOperationException("A PIN prompt was not requested by this fixture."));
            else await fixture.App.ToggleAsync(group);
            await (state == PlaybackState.Pairing ? process.PairSent.Task : process.StartSent.Task).WaitAsync(Limit);
            if (state == PlaybackState.Standby)
            {
                await StateAsync(fixture, PlaybackState.Streaming);
                process.Emit(new { @event = "capture_metrics", metrics = new { last_audio_qpc_ns = 1, underrun_packets = 3 } });
            }
            await StateAsync(fixture, state);
            var owner = fixture.App.Session.Capture().Owner; var restores = fixture.Audio.RestoreCalls;
            var snapshots = new IReadOnlyList<Receiver>[]
            {
                [], [group with { Members = [group.Members[0]] }],
                [group with { Members = [.. group.Members, Pod("extra", "192.0.2.12")] }], [], []
            };
            foreach (var snapshot in snapshots)
            {
                await fixture.App.ApplyDiscoveryAsync(snapshot); await DrainAsync();
                var current = fixture.App.Session.Capture();
                var row = fixture.App.Receivers.Single(receiver => receiver.Receiver.Id == group.Id);
                var catalog = fixture.App.AllReceivers.Single(receiver => receiver.Id == group.Id);
                Check(current.Owner == owner && current.Snapshot.State == state && current.Snapshot.Receiver?.Members.Length == 2 &&
                    fixture.Engine.OpenCount == 1 && !process.Disposed && !process.Commands.Any(command => command.Name == "stop") &&
                    fixture.Audio.RestoreCalls == restores && (!catalog.Online || !catalog.Complete) && row.Active && row.CanPlay &&
                    row.StateText.Contains(L.Get("Discovery unavailable"), StringComparison.Ordinal),
                    "passive empty/partial/repeated discovery retains owned transport and truthful Stop controls in " + state + " (" + snapshot.Count + " rows)", checks);
            }
            await fixture.App.ApplyDiscoveryAsync([group]); await DrainAsync();
            Check(fixture.Engine.OpenCount == 1 && fixture.App.Session.Capture().Owner == owner &&
                !fixture.App.Receivers.Single().StateText.Contains(L.Get("Discovery unavailable"), StringComparison.Ordinal),
                "complete reappearance restores discovery presentation without replacing " + state, checks);
            await fixture.App.ApplyDiscoveryAsync([]); await DrainAsync();
            var panel = new ControlPanel(fixture.App);
            try
            {
                panel.ShowPanel(); await DrainAsync(); panel.UpdateLayout();
                var stop = UiSmoke.Descendants(panel).OfType<Button>().Single(button =>
                    System.Windows.Automation.AutomationProperties.GetName(button) == L.Get("Stop playback / disconnect"));
                Check(stop.IsVisible && stop.IsEnabled && stop.Command == fixture.App.Receivers.Single().ToggleCommand,
                    "a visible bound Stop button remains accessible through discovery loss in " + state, checks);
                if (state == PlaybackState.Streaming) UiSmoke.Render(panel, Path.Combine(directory, "panel-retained-discovery.png"), 1);
                // Pairing is held before pin_required, so no modal dialog hides this ordinary control.
                var peer = new ButtonAutomationPeer(stop); ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                await StateAsync(fixture, PlaybackState.Idle); await process.DisposalCompleted.Task.WaitAsync(Limit);
                Check(fixture.App.Receivers.Count == 0 && fixture.Engine.OpenCount == 1 && fixture.Audio.MuteBit == fixture.Audio.OriginalBit,
                    "the retained Stop button cleans up exactly once and removes the inactive overlay in " + state, checks);
            }
            finally { panel.ShutdownPanel(); }
            var unavailable = fixture.App.AllReceivers.Single();
            var rejected = false;
            try { await fixture.App.ToggleAsync(unavailable); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected && fixture.Engine.OpenCount == 1, "retaining " + state + " does not allow a new unavailable start", checks);
        }
    }
    private static async Task AdapterRetentionAsync(List<string> checks)
    {
        var a = Pod("adapter-a"); var manual = new ManualReceiver("manual-persisted", "Manual", "192.0.2.20", 7001);
        var settings = Settings(); settings.ManualReceivers.Add(manual); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        await fixture.App.ToggleAsync(a); await StateAsync(fixture, PlaybackState.Streaming);
        var process = fixture.Engine.Connections.Single(); var owner = fixture.App.Session.Capture().Owner;
        var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
        draft.DiscoveryInterfaceId = "e65a775a-62b3-4a19-9640-9f0d63ae2764";
        draft.Options(a.Id).Hidden = true;
        await fixture.App.ApplyAsync(baseline, draft); await DrainAsync(); await fixture.App.ApplyDiscoveryAsync([]);
        Check(fixture.Discovery.SelectedInterface == draft.DiscoveryInterfaceId && fixture.App.Session.Capture().Owner == owner &&
            fixture.App.Snapshot.State == PlaybackState.Streaming && !process.Disposed && fixture.Engine.OpenCount == 1 &&
            !fixture.App.AllReceivers.Any(receiver => receiver.Id == a.Id) && fixture.App.AllReceivers.Any(receiver => receiver.Id == manual.Id) &&
            fixture.App.Settings.ReadOptions(a.Id).Hidden &&
            fixture.App.Receivers.Single(receiver => receiver.Receiver.Id == a.Id).StateText.Contains(L.Get("Discovery unavailable"), StringComparison.Ordinal),
            "an unavailable selected adapter retains owned playback and its overlay without discovery fallback or manual deletion", checks);
        Check(DiscoveryInterface.ResolveIndex(draft.DiscoveryInterfaceId, []) is null,
            "selected-adapter absence remains a paused selection rather than all interfaces", checks);
        await fixture.App.StopAsync(); await DrainAsync();
        Check(fixture.App.Receivers.All(receiver => receiver.Receiver.Id != a.Id), "explicit Stop removes a receiver retained only by active ownership", checks);
    }
    private static async Task RetainedPinCancellationAsync(List<string> checks)
    {
        var a = Pod("pending-pin"); var settings = Settings(); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var connection = new ScriptedConnection(false); var pin = new PendingPin(); fixture.Engine.Enqueue(connection);
        try
        {
            await fixture.App.PairAsync(a, pin.RequestAsync); await connection.PairSent.Task.WaitAsync(Limit);
            connection.Emit(new { @event = "pin_required" }); await pin.Entered.Task.WaitAsync(Limit);
            await fixture.App.ApplyDiscoveryAsync([]);
            Check(!pin.Cancelled && !connection.Disposed && fixture.App.Session.Snapshot.State == PlaybackState.Pairing,
                "passive discovery loss preserves an already pending cancellation-aware PIN workflow", checks);
            await fixture.App.StopAsync(); await StateAsync(fixture, PlaybackState.Idle);
            Check(pin.Cancelled && connection.Disposed && !connection.Commands.Any(command => command.Name == "start"),
                "explicit cancellation still closes the retained PIN workflow without playback", checks);
        }
        finally { pin.Release.TrySetResult(null); }
    }
    private static async Task FaultAndMuteAsync(List<string> checks)
    {
        foreach (var original in new[] { false, true })
        foreach (var retry in new[] { false, true })
        {
            var a = Pod("fault-a"); var settings = Settings(); settings.MuteWhileStreaming = true;
            settings.ForceReconnect = retry; settings.MaxReconnectAttempts = 1; SeedAliases(settings, a);
            await using var fixture = await Fixture.CreateAsync(settings, [a], original);
            await fixture.App.ToggleAsync(a); await StateAsync(fixture, PlaybackState.Streaming);
            var process = fixture.Engine.Connections.Single(); var owner = fixture.App.Session.Capture().Owner;
            await fixture.App.ApplyDiscoveryAsync([]);
            process.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = 11 } });
            await StateAsync(fixture, snapshot => snapshot.Metrics?.Underruns == 11);
            process.Emit(new { @event = "error", code = "peer_closed", message = "synthetic terminal fault", host = a.Address, channel = "events", retryable = true });
            if (retry)
            {
                await StateAsync(fixture, snapshot => snapshot.State == PlaybackState.Streaming && snapshot.Diagnostics?.ReconnectCount == 1);
                var replacement = fixture.Engine.Connections.Last();
                Check(fixture.App.Session.Capture().Owner == owner && fixture.Engine.OpenCount == 2 && process.Disposed &&
                    replacement.Session != process.Session && fixture.App.Snapshot.Diagnostics?.LastFault?.Code == "peer_closed" &&
                    fixture.App.Snapshot.Diagnostics.CaptureBeforeFault?.Underruns == 11 && fixture.Audio.RestoredBits.Contains(original),
                    "terminal fault retries a fresh native session under the same desktop owner and preserves cleanup/diagnostics (original mute " + original + ")", checks);
            }
            else
            {
                await StateAsync(fixture, PlaybackState.Error); await process.DisposalCompleted.Task.WaitAsync(Limit);
                Check(fixture.Engine.OpenCount == 1 && fixture.App.Snapshot.Diagnostics?.LastFault?.Code == "peer_closed" &&
                    fixture.App.Snapshot.Diagnostics.CaptureBeforeFault?.Underruns == 11 && fixture.Audio.MuteBit == original &&
                    fixture.App.Receivers.Count == 0,
                    "terminal fault still ends retained playback and restores the exact prior mute bit (original mute " + original + ")", checks);
            }
            await fixture.App.StopAsync();
            Check(fixture.Audio.MuteBit == original && !fixture.Audio.HasSavedBit,
                "explicit cleanup after a retained session restores the original " + original + " mute bit (retry " + retry + ")", checks);
        }
        foreach (var pairing in new[] { false, true })
        {
            var a = Pod(pairing ? "pair-fault" : "connect-fault"); var settings = Settings(); SeedAliases(settings, a);
            await using var fixture = await Fixture.CreateAsync(settings, [a]);
            var connection = new ScriptedConnection(false); fixture.Engine.Enqueue(connection);
            if (pairing) await fixture.App.PairAsync(a, (_, _) => throw new InvalidOperationException("No PIN event was sent."));
            else await fixture.App.ToggleAsync(a);
            await (pairing ? connection.PairSent.Task : connection.StartSent.Task).WaitAsync(Limit);
            await fixture.App.ApplyDiscoveryAsync([]);
            connection.Emit(new { @event = "error", message = "synthetic handshake failure", retryable = false });
            await StateAsync(fixture, PlaybackState.Error); await connection.DisposalCompleted.Task.WaitAsync(Limit);
            Check(fixture.Engine.OpenCount == 1 && fixture.App.Snapshot.Message.Contains("synthetic handshake failure", StringComparison.Ordinal),
                "retained " + (pairing ? "pairing" : "connecting") + " remains governed by real worker failure and cleanup", checks);
        }
    }
    private static async Task ShutdownDuringAutomaticSaveAsync(List<string> checks)
    {
        var a = Pod("shutdown-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 29;
        var automatic = fixture.App.TryAutoConnectAsync();
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            var shutdown = fixture.App.DisposeAsync().AsTask();
            var responsive = false;
            await Application.Current.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            Check(responsive && !shutdown.IsCompleted, "shutdown waits asynchronously for held automatic persistence while the dispatcher stays responsive", checks);
            gate.Release.TrySetResult(); await Task.WhenAll(automatic, shutdown);
            Check(fixture.Engine.OpenCount == 0 && fixture.Store.Saved.MasterVolume == 29 && fixture.Store.Saved.LastReceiverId == "previous",
                "shutdown preserves valid pending edits and rejects delayed automatic admission", checks);
        }
        finally { gate.Release.TrySetResult(); }
    }

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync() { Entered.TrySetResult(); await Release.Task; }
    }
    private sealed class Store(AppSettings initial) : ISettingsStore
    {
        private AppSettings _saved = initial.Clone();
        public bool Fail { get; set; }
        public Gate? SaveGate { get; private set; }
        public AppSettings Saved => _saved.Clone();
        public AppSettings Load() => _saved.Clone();
        public Gate Hold() => SaveGate = new();
        public void Save(AppSettings settings)
        {
            if (Fail) throw new IOException("synthetic save failure");
            _saved = settings.Clone();
        }
        public async Task SaveAsync(AppSettings settings)
        {
            if (SaveGate is { } gate) await gate.WaitAsync();
            Save(settings);
        }
    }
    private sealed class Audio(bool original) : IAudioService
    {
        public bool OriginalBit { get; } = original;
        public bool MuteBit { get; private set; } = original;
        public bool HasSavedBit => _saved is not null;
        private bool? _saved;
        public int RestoreCalls { get; private set; }
        public List<bool> RestoredBits { get; } = [];
        private Gate? _restoreGate;
        public Gate HoldRestore() => _restoreGate = new();
        public void Release() => _restoreGate?.Release.TrySetResult();
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId)
        {
            _saved ??= MuteBit; MuteBit = true; return Task.CompletedTask;
        }
        public async Task RestoreAsync()
        {
            RestoreCalls++;
            if (_restoreGate is { } gate) await gate.WaitAsync();
            if (_saved is { } saved) { MuteBit = saved; RestoredBits.Add(saved); _saved = null; }
        }
    }
    private sealed class PendingPin
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; private set; }
        public async Task<string?> RequestAsync(Receiver member, CancellationToken cancellation)
        {
            Entered.TrySetResult();
            try { return await Release.Task.WaitAsync(cancellation); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { Cancelled = true; throw; }
        }
    }
    private sealed record Command(string Session, string Name, JsonElement Parameters);
    private sealed class ScriptedConnection(bool streaming = true) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<Command> Commands { get; } = new();
        public TaskCompletionSource StartSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PairSent { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DisposalCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CompletePair { get; init; }
        public bool Disposed { get; private set; }
        public string Session { get; private set; } = "";
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue(new(session, command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            if (command == "start") { Session = session; StartSent.TrySetResult(); if (streaming) Emit(new { @event = "streaming" }); }
            if (command == "pair") { Session = session; PairSent.TrySetResult(); if (CompletePair) Emit(new { @event = "pin_required" }); }
            if (command == "pair_pin" && CompletePair) Emit(new { @event = "paired" });
            if (command == "stop") _events.Writer.TryComplete();
            return Task.CompletedTask;
        }
        public void Emit(object payload)
        {
            var value = JsonSerializer.SerializeToNode(payload)!.AsObject();
            value["version"] = 1; value["session_id"] = Session;
            if (!_events.Writer.TryWrite(JsonSerializer.SerializeToElement(value))) throw new InvalidOperationException("Fixture event emitted after cleanup.");
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync()
        {
            Disposed = true; _events.Writer.TryComplete(); DisposalCompleted.TrySetResult(); return ValueTask.CompletedTask;
        }
    }
    private sealed class Engine : IEngineFactory
    {
        private readonly ConcurrentQueue<ScriptedConnection> _next = new();
        public ConcurrentQueue<ScriptedConnection> Connections { get; } = new();
        public int OpenCount;
        public bool FailNext { get; set; }
        public void Enqueue(ScriptedConnection connection) => _next.Enqueue(connection);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref OpenCount);
            if (FailNext) { FailNext = false; throw new IOException("synthetic open failure"); }
            var connection = _next.TryDequeue(out var scripted) ? scripted : new ScriptedConnection();
            Connections.Enqueue(connection); return connection;
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; }
        public Audio Audio { get; }
        public Engine Engine { get; } = new();
        public UiSmoke.MockDiscovery Discovery { get; } = new() { Items = [] };
        public AppViewModel App { get; }
        private Fixture(AppSettings settings, bool original)
        {
            Store = new(settings); Audio = new(original);
            App = new(Store, Discovery, new UiSmoke.MockAutostart(), Audio, Engine, Application.Current.Dispatcher);
            App.UseControlledScheduling();
        }
        public static async Task<Fixture> CreateAsync(AppSettings settings, IReadOnlyList<Receiver> receivers, bool original = false)
        {
            var fixture = new Fixture(settings, original);
            try
            {
                fixture.App.Start(); await fixture.App.Startup; await DrainAsync();
                await fixture.App.ApplyDiscoveryAsync(receivers); await DrainAsync(); return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Store.SaveGate?.Release.TrySetResult(); Audio.Release(); await App.DisposeAsync();
        }
    }
}
