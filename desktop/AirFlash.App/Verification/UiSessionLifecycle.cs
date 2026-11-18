using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Threading;
using AirFlash.App.Services;
using AirFlash.App.Ui;
using AirFlash.App.ViewModels;
using AirFlash.Core;
namespace AirFlash.App.Verification;

// Opt-in dispatcher fixtures for a future explicitly authorized UI qualification.
// Every application boundary is fake; this harness never constructs platform services.
internal static class UiSessionLifecycle
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(6);
    private static Action<PinDialog>? _identityPromptLoaded;
    static UiSessionLifecycle()
    {
        EventManager.RegisterClassHandler(typeof(PinDialog), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => _identityPromptLoaded?.Invoke((PinDialog)sender)));
    }
    public static async Task RunAsync(List<string> checks, string directory)
    {
        _ = directory;
        await IdentityOwnershipAsync(checks);
        await EndpointObservationAcrossPairAsync(checks, observationFinishesAfterPair: false);
        await EndpointObservationAcrossPairAsync(checks, observationFinishesAfterPair: true);
        await EndpointObservationAcrossApplyAsync(checks);
        await IdentitySettingsContinuationAsync(checks, false);
        await IdentitySettingsContinuationAsync(checks, true);
        await LatencyHintUsesPanelDraftAsync(checks);
        await AutomaticOwnershipAsync(checks);
        await AutomaticEligibilityAsync(checks);
        await AutomaticSaveFailureAsync(checks);
        await RejectedPlayRetainsPendingGainAsync(checks);
        await OtherSavesRetainPendingGainAsync(checks);
        await VolumeEditsCoalesceAcrossLiveApplicationAsync(checks);
        await RemoteAudioCommandsRespectStopAsync(checks);
        await StopWaitingForAdmissionAsync(checks);
        await EligibilityArrivalAsync(checks);
        await ManualSelectionAsync(checks);
        await QueuedManualSelectionAsync(checks);
        await RetainedStatesAsync(checks);
        await WorkerCleanupFailureRetainsStopAsync(checks, originalMute: false);
        await WorkerCleanupFailureRetainsStopAsync(checks, originalMute: true);
        await RetainOnInterfaceChangeAsync(checks);
        await ResetDuringAutomaticSaveAsync(checks);
        await ShutdownDuringAutomaticSaveAsync(checks);
        await ShellShutdownRetainsVisibleStopAsync(checks, holdApply: false);
        await ShellShutdownRetainsVisibleStopAsync(checks, holdApply: true);
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
    private static async Task CatalogAsync(Fixture fixture, Func<bool> predicate)
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed() { if (predicate()) arrived.TrySetResult(); }
        fixture.App.CatalogChanged += Changed;
        try
        {
            Changed(); await arrived.Task.WaitAsync(Limit);
        }
        finally { fixture.App.CatalogChanged -= Changed; }
    }
    private static async Task RemoteAudioCommandsRespectStopAsync(List<string> checks)
    {
        var receiver = Pod("remote-audio");
        var settings = Settings();
        settings.MasterVolume = 40;
        await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
        await fixture.App.ToggleAsync(receiver);
        await StateAsync(fixture, PlaybackState.Streaming);

        var reported = new TaskCompletionSource<RemoteCommandAuthority>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Capture(RemoteCommand command, RemoteCommandAuthority authority)
        {
            if (command.Name == "volume") reported.TrySetResult(authority);
        }

        RemoteCommandAuthority authority;
        fixture.App.Session.OwnedRemoteCommandReceived += Capture;
        try
        {
            fixture.Engine.Connections.Single().Emit(new { @event = "remote_command", volume = 0.4 });
            authority = await reported.Task.WaitAsync(Limit);
        }
        finally { fixture.App.Session.OwnedRemoteCommandReceived -= Capture; }

        await fixture.App.QueueRemoteAudioCommandAsync(new("volume_up"), authority).WaitAsync(Limit);
        await fixture.App.QueueRemoteAudioCommandAsync(new("mute"), authority).WaitAsync(Limit);
        Check(fixture.App.MasterVolume == 45 && fixture.App.Muted,
            "Current receiver commands adjust AirFlash volume and mute", checks);

        // Queue both callbacks on the dispatcher, then invalidate ownership in the
        // synchronous prefix of Stop before either callback can run.
        var staleVolume = fixture.App.QueueRemoteAudioCommandAsync(new("volume_down"), authority);
        var staleMute = fixture.App.QueueRemoteAudioCommandAsync(new("mute"), authority);
        var stop = fixture.App.StopAsync();
        await Task.WhenAll(staleVolume, staleMute, stop).WaitAsync(Limit);
        Check(fixture.App.MasterVolume == 45 && fixture.App.Muted,
            "Queued receiver volume and mute commands have no effect after Stop intent", checks);
    }

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
                try { await reconcile.WaitAsync(Limit); }
                catch (IOException) when (scenario == "save failure") { }
                finally { pin.Release.TrySetResult(null); await fixture.App.StopAsync(); }
            }
        }
    }
    private static async Task EndpointObservationAcrossPairAsync(List<string> checks, bool observationFinishesAfterPair)
    {
        const string pairedEndpoint = "synthetic-endpoint-b", changedEndpoint = "synthetic-endpoint-c";
        var old = Pod("endpoint-old");
        var selected = Pod("endpoint-pair", "192.0.2.20", 7001);
        var settings = Settings();
        SeedAliases(settings, old, selected);
        await using var fixture = await Fixture.CreateAsync(settings, [old, selected]);
        if (observationFinishesAfterPair) fixture.Audio.DefaultEndpoint = pairedEndpoint;
        await fixture.App.ToggleAsync(old);
        await StateAsync(fixture, PlaybackState.Streaming);
        var first = fixture.Engine.Connections.Single();

        fixture.Audio.DefaultEndpoint = pairedEndpoint;
        var lookup = fixture.Audio.HoldNextDefaultEndpointLookup();
        var observation = fixture.App.OnEndpointsChangedAsync();
        var pin = new PendingPin();
        var pairing = new ScriptedConnection(false) { CompletePair = true };
        var pairedPlayback = new ScriptedConnection();
        fixture.Engine.Enqueue(pairing);
        fixture.Engine.Enqueue(pairedPlayback);
        try
        {
            await lookup.Entered.Task.WaitAsync(Limit);
            await fixture.App.PairAsync(selected, pin.RequestAsync).WaitAsync(Limit);
            await pairing.PairSent.Task.WaitAsync(Limit);
            await pin.Entered.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Pairing);
            var pairedOwner = fixture.App.Session.Capture().Owner;

            if (!observationFinishesAfterPair)
            {
                lookup.Release.TrySetResult();
                await observation.WaitAsync(Limit);
                Check(first.Disposed && !pairing.Disposed && !pin.Cancelled
                    && fixture.App.Session.Capture().Owner == pairedOwner && fixture.Engine.OpenCount == 2,
                    "a held default-endpoint observation cannot restart or cancel a newer Pair", checks);
            }

            pin.Release.TrySetResult("1234");
            await pairedPlayback.StartSent.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Streaming);
            Check(fixture.Audio.DefaultEndpoint == pairedEndpoint && first.Disposed && pairing.Disposed
                && fixture.App.Session.Capture().Owner == pairedOwner && fixture.Engine.OpenCount == 3,
                "the newer Pair reaches playback while the observed default endpoint remains selected", checks);

            if (observationFinishesAfterPair)
            {
                fixture.Audio.DefaultEndpoint = changedEndpoint;
                lookup.Release.TrySetResult();
                await observation.WaitAsync(Limit);
                Check(fixture.App.Session.Capture().Owner == pairedOwner && !pairedPlayback.Disposed && fixture.Engine.OpenCount == 3,
                    "an obsolete endpoint notification cannot restart newer playback even when its lookup returns a changed default", checks);
            }
            else
            {
                await fixture.App.OnEndpointsChangedAsync().WaitAsync(Limit);
                Check(fixture.App.Session.Capture().Owner == pairedOwner && !pairedPlayback.Disposed
                    && !pairedPlayback.Commands.Any(command => command.Name == "stop") && fixture.Engine.OpenCount == 3,
                    "a repeated notification for the same default endpoint does not restart newer playback", checks);
            }

            var replacement = new ScriptedConnection();
            fixture.Engine.Enqueue(replacement);
            fixture.Audio.DefaultEndpoint = changedEndpoint;
            await fixture.App.OnEndpointsChangedAsync().WaitAsync(Limit);
            await replacement.StartSent.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Streaming);
            var start = replacement.Commands.Single(command => command.Name == "start");
            var peer = start.Parameters.GetProperty("peers")[0];
            Check(fixture.App.Session.Capture().Owner != pairedOwner && pairedPlayback.Disposed
                && fixture.Engine.OpenCount == 4 && fixture.App.Session.Snapshot.Receiver?.Id == selected.Id
                && peer.Text("host") == selected.Address && peer.Integer("port") == selected.Port,
                "a fresh notification restarts the same selected receiver exactly once for the changed default"
                + (observationFinishesAfterPair ? " after an obsolete lookup observed it" : ""), checks);
        }
        finally
        {
            lookup.Release.TrySetResult();
            pin.Release.TrySetResult(null);
            await observation.WaitAsync(Limit);
        }
    }

    private static async Task EndpointObservationAcrossApplyAsync(List<string> checks)
    {
        var receiver = Pod("endpoint-apply");
        var settings = Settings(); SeedAliases(settings, receiver);
        await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
        await fixture.App.ToggleAsync(receiver);
        await StateAsync(fixture, PlaybackState.Streaming);
        var original = fixture.App.Session.Capture();

        var baseline = fixture.App.Settings.Clone();
        var draft = baseline.Clone(); draft.LatencyMode = "buffered";
        var save = fixture.Store.Hold();
        var lookup = fixture.Audio.HoldNextDefaultEndpointLookup();
        var applying = fixture.App.ApplyAsync(baseline, draft);
        Task observing = Task.CompletedTask;
        try
        {
            await save.Entered.Task.WaitAsync(Limit);
            observing = fixture.App.OnEndpointsChangedAsync();
            Check(!observing.IsCompleted && !lookup.Entered.Task.IsCompleted,
                "default endpoint observation queues behind the accepted Settings Apply", checks);
            save.Release.TrySetResult();
            var applied = await applying.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Streaming);
            await lookup.Entered.Task.WaitAsync(Limit);
            var replacement = fixture.App.Session.Capture();
            Check(applied.AudioError is null && replacement.Owner != original.Owner
                && replacement.AutomaticIntent == original.AutomaticIntent && fixture.Engine.OpenCount == 2,
                "a settings-triggered restart replaces playback ownership without changing explicit intent", checks);

            fixture.Audio.DefaultEndpoint = "synthetic-endpoint-after-apply";
            lookup.Release.TrySetResult();
            await observing.WaitAsync(Limit);
            Check(fixture.App.Session.Capture().Owner == replacement.Owner && fixture.Engine.OpenCount == 2,
                "an old-owner observation cannot restart same-intent replacement playback", checks);

            var latest = new ScriptedConnection();
            fixture.Engine.Enqueue(latest);
            await fixture.App.OnEndpointsChangedAsync().WaitAsync(Limit);
            Check(fixture.App.Session.Capture().Owner != replacement.Owner,
                "the current owner still handles the changed default after a same-intent replacement", checks);
            await latest.StartSent.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Streaming);
            Check(fixture.Engine.OpenCount == 3 && fixture.App.Session.Snapshot.Receiver?.Id == receiver.Id,
                "same-intent endpoint recovery restarts exactly one worker for the selected receiver", checks);
        }
        finally
        {
            save.Release.TrySetResult();
            lookup.Release.TrySetResult();
            await Task.WhenAll(applying, observing).WaitAsync(Limit);
        }
    }

    private static async Task IdentitySettingsContinuationAsync(List<string> checks, bool flushWhilePairing)
    {
        const string address = "192.0.2.10";
        const int port = 7009;
        var legacy = Pod(ReceiverIdentity.Endpoint(address, port), address, port);
        var canonical = Pod("paired-canonical", address, port) with { DeviceId = "paired-canonical" };
        var settings = Settings();
        settings.Options(legacy.Id).LatencyMode = "custom";
        settings.Options(legacy.Id).CustomBufferMs = 333;
        settings.Options(legacy.Id).SampleRate = "48000";
        settings.Options(legacy.Id).Codec = "pcm";
        await using var fixture = await Fixture.CreateAsync(settings, [legacy]);
        await fixture.App.ToggleAsync(legacy); await StateAsync(fixture, PlaybackState.Streaming);
        var pairing = new ScriptedConnection(false) { CompletePair = true };
        var playback = new ScriptedConnection();
        fixture.Engine.Enqueue(pairing); fixture.Engine.Enqueue(playback);
        var owner = new Window { Width = 320, Height = 100, Title = "Synthetic identity pairing verification", ShowInTaskbar = false };
        var promptShown = new TaskCompletionSource<PinDialog>(TaskCreationOptions.RunContinuationsAsynchronously);
        var promptClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        PinDialog? prompt = null;
        Gate? save = null;
        Task reconcile = Task.CompletedTask;
        try
        {
            owner.Show();
            _identityPromptLoaded = dialog =>
            {
                if (!ReferenceEquals(dialog.Owner, owner)) return;
                prompt = dialog; dialog.Closed += (_, _) => promptClosed.TrySetResult();
                promptShown.TrySetResult(dialog);
            };
            save = fixture.Store.Hold();
            reconcile = fixture.App.ApplyDiscoveryAsync([canonical]);
            await save.Entered.Task.WaitAsync(Limit);
            await fixture.App.PairAsync(legacy, (member, cancellation) => PinDialog.RequestAsync(owner, member, cancellation));
            await pairing.PairSent.Task.WaitAsync(Limit);
            prompt = await promptShown.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Pairing);
            var pairedOwner = fixture.App.Session.Capture();
            save.Release.TrySetResult(); await reconcile.WaitAsync(Limit); await DrainAsync();
            var retained = fixture.App.Session.Capture();
            Check(retained.Owner == pairedOwner.Owner && retained.Snapshot.State == PlaybackState.Pairing &&
                retained.Snapshot.Receiver?.Id == legacy.Id && retained.Snapshot.Receiver.Address == address &&
                retained.Snapshot.Receiver.Port == port && retained.Snapshot.TargetLatency == 333 &&
                ReferenceEquals(retained.Snapshot.Diagnostics, pairedOwner.Snapshot.Diagnostics) &&
                !pairing.Disposed && !pairing.Commands.Any(command => command.Name == "stop") &&
                !playback.StartSent.Task.IsCompleted && prompt.IsVisible,
                "endpoint identity save leaves the newer actual PIN dialog, owner, transport and options untouched", checks);
            var saved = fixture.Store.Saved;
            Check(!saved.Receivers.ContainsKey(legacy.Id) && saved.ReadOptions(canonical.Id) is
                { LatencyMode: "custom", CustomBufferMs: 333, SampleRate: "48000", Codec: "pcm" } &&
                !fixture.App.Settings.Receivers.ContainsKey(legacy.Id) &&
                !fixture.App.Settings.ReceiverAliases.ContainsKey(legacy.Id),
                "identity persistence moves endpoint options to the canonical key without persisting an endpoint alias", checks);
            if (flushWhilePairing)
            {
                var pairOpens = fixture.Engine.OpenCount;
                var pairStarts = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "start"));
                var pairStops = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "stop"));
                fixture.App.MasterVolume = 29;
                await fixture.App.FlushVolumeAsync(); await DrainAsync();
                var waiting = fixture.App.Session.Capture();
                Check(waiting.Owner == pairedOwner.Owner && waiting.Snapshot.State == PlaybackState.Pairing &&
                    waiting.Snapshot.Receiver is { Address: address, Port: port } && waiting.Snapshot.TargetLatency == 333 &&
                    fixture.Engine.OpenCount == pairOpens &&
                    fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "start")) == pairStarts &&
                    fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "stop")) == pairStops &&
                    !pairing.Disposed && !playback.StartSent.Task.IsCompleted && prompt.IsVisible &&
                    !pairing.Commands.Any(command => command.Name == "pair_pin"),
                    "a canonical volume flush while the actual PIN dialog is open preserves Pair ownership, transport and pending input", checks);
                Check(fixture.Store.Saved.MasterVolume == 29 && !fixture.Store.Saved.Receivers.ContainsKey(legacy.Id) &&
                    fixture.Store.Saved.ReadOptions(canonical.Id) is { LatencyMode: "custom", CustomBufferMs: 333, SampleRate: "48000", Codec: "pcm" },
                    "Pairing-time gain persistence retains only canonical receiver options", checks);
            }
            var code = UiSmoke.Descendants(prompt).OfType<TextBox>().Single();
            var submit = UiSmoke.Descendants(prompt).OfType<Button>().Single(button => button.IsDefault);
            code.Text = "12-34";
            Check(submit.IsEnabled, "the actual identity-continuation PIN control accepts a valid formatted PIN", checks);
            ((IInvokeProvider)new ButtonAutomationPeer(submit).GetPattern(PatternInterface.Invoke)).Invoke();
            await promptClosed.Task.WaitAsync(Limit);
            await playback.StartSent.Task.WaitAsync(Limit); await StateAsync(fixture, PlaybackState.Streaming);
            var start = playback.Commands.Single(command => command.Name == "start").Parameters;
            var peer = start.GetProperty("peers")[0];
            Check(pairing.Commands.Single(command => command.Name == "pair_pin").Parameters.Text("pin") == "1234" &&
                start.GetProperty("peers").GetArrayLength() == 1 && peer.Text("host") == address && peer.Integer("port") == port &&
                start.Integer("latency_ms") == 333 && start.Integer("sample_rate") == 48000 && peer.GetProperty("profile").Text("codec") == "pcm",
                "the newer Pair completes with its exact synthetic endpoint and retained custom latency, rate and codec (Pairing flush " + flushWhilePairing + ")", checks);
            Check(!fixture.App.CanEditGlobalLatency && fixture.App.GlobalLatencyHint.Length > 0,
                "the global latency control recognizes the effective owned override after canonical identity persistence", checks);
            if (flushWhilePairing)
                Check(start.Number("gain") == .29, "playback after a Pairing-time flush uses the newly saved global gain", checks);
            var opens = fixture.Engine.OpenCount;
            var starts = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "start"));
            var stops = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "stop"));
            fixture.App.MasterVolume = 37;
            await fixture.App.FlushVolumeAsync(); await DrainAsync();
            var current = fixture.App.Session.Capture();
            var currentStarts = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "start"));
            var currentStops = fixture.Engine.Connections.Sum(connection => connection.Commands.Count(command => command.Name == "stop"));
            Check(fixture.Engine.OpenCount == opens && currentStarts == starts && currentStops == stops && !playback.Disposed &&
                current.Snapshot.State == PlaybackState.Streaming && current.Snapshot.Receiver?.Address == address &&
                current.Snapshot.Receiver.Port == port && current.Snapshot.TargetLatency == 333 && current.Snapshot.StreamRate == 48000,
                "a volume-only flush after newer Pair preserves transport/profile without another engine start or stop: " +
                $"opens={fixture.Engine.OpenCount}/{opens}; starts={currentStarts}/{starts}; stops={currentStops}/{stops}; " +
                $"disposed={playback.Disposed}; state={current.Snapshot.State}; latency={current.Snapshot.TargetLatency}; rate={current.Snapshot.StreamRate}", checks);
            Check(playback.Commands.Last(command => command.Name == "set_gain").Parameters.Number("gain") == .37 &&
                fixture.Store.Saved.MasterVolume == 37 && !fixture.Store.Saved.Receivers.ContainsKey(legacy.Id) &&
                fixture.Store.Saved.ReadOptions(canonical.Id) is { LatencyMode: "custom", CustomBufferMs: 333, SampleRate: "48000", Codec: "pcm" },
                "the unrelated panel volume edit changes gain while canonical persisted receiver options stay intact", checks);
            playback.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = 29 } });
            await StateAsync(fixture, snapshot => snapshot.Metrics?.Underruns == 29);
            Check(fixture.App.Snapshot.TargetLatency == 333 && fixture.App.Snapshot.StreamRate == 48000 &&
                fixture.App.Snapshot.Receiver is { Address: address, Port: port } && fixture.App.StopCommand.CanExecute(null),
                "later current publications retain effective receiver options and active Stop after canonical persistence", checks);
            var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
            draft.Receivers.Remove(canonical.Id);
            var resetPlayback = new ScriptedConnection(); fixture.Engine.Enqueue(resetPlayback);
            var applied = await fixture.App.ApplyAsync(baseline, draft);
            await resetPlayback.StartSent.Task.WaitAsync(Limit); await StateAsync(fixture, PlaybackState.Streaming);
            var resetStart = resetPlayback.Commands.Single(command => command.Name == "start").Parameters;
            var resetPeer = resetStart.GetProperty("peers")[0];
            Check(applied.AudioError is null && playback.Disposed && fixture.Engine.OpenCount == opens + 1 &&
                resetPeer.Text("host") == address && resetPeer.Integer("port") == port &&
                resetStart.Integer("latency_ms") == 200 && resetStart.Integer("sample_rate") == 44100 &&
                resetPeer.GetProperty("profile").Text("codec") == "auto" &&
                !fixture.Store.Saved.Receivers.ContainsKey(legacy.Id) && !fixture.Store.Saved.Receivers.ContainsKey(canonical.Id),
                "explicitly removing canonical receiver overrides intentionally restarts the same transport with inherited options", checks);
            Check(fixture.App.CanEditGlobalLatency && fixture.App.GlobalLatencyHint.Length == 0 && fixture.App.StopCommand.CanExecute(null),
                "explicit override removal restores the global latency control and leaves ordinary Stop available", checks);
        }
        finally
        {
            save?.Release.TrySetResult(); _identityPromptLoaded = null;
            try { if (prompt is { IsVisible: true }) prompt.Close(); }
            finally
            {
                try { await reconcile.WaitAsync(Limit); }
                finally
                {
                    try { await fixture.App.StopAsync().WaitAsync(Limit); }
                    finally { owner.Close(); }
                }
            }
        }
    }
    private static async Task LatencyHintUsesPanelDraftAsync(List<string> checks)
    {
        var receiver = Pod("latency-hint-control"); var settings = Settings();
        settings.Options(receiver.Id).CustomBufferMs = 333;
        await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
        await fixture.App.ToggleAsync(receiver); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.App.CanEditGlobalLatency && fixture.App.GlobalLatencyHint.Length == 0,
            "a receiver custom buffer alone does not hide the normal global latency control", checks);
        fixture.App.LatencyMode = "custom";
        Check(!fixture.App.CanEditGlobalLatency && fixture.App.GlobalLatencyHint.Length > 0 && fixture.App.Settings.LatencyMode == "custom",
            "the effective receiver latency hint uses the current panel global mode before its pending flush", checks);
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
            finally { gate.Release.TrySetResult(); pin.Release.TrySetResult(null); await automatic.WaitAsync(Limit); }
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
            finally
            {
                gate.Release.TrySetResult();
                await Task.WhenAll(transaction, automatic).WaitAsync(Limit);
            }
        }
    }
    private static async Task StopWaitingForAdmissionAsync(List<string> checks)
    {
        var a = Pod("auto-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var restore = fixture.Audio.HoldRestore();
        var updating = fixture.App.Session.UpdateSettingsAsync(settings);
        Task automatic = Task.CompletedTask, stopping = Task.CompletedTask;
        try
        {
            await restore.Entered.Task.WaitAsync(Limit);
            // This call reaches the held controller semaphore synchronously: no dirty save or admission hook intervenes.
            automatic = fixture.App.TryAutoConnectAsync();
            Check(!automatic.IsCompleted, "automatic admission waits behind serialized controller work", checks);
            stopping = fixture.App.StopAsync();
            Check(!stopping.IsCompleted, "user Stop invalidates automatic intent before its controller wait finishes", checks);
            restore.Release.TrySetResult(); await Task.WhenAll(updating, automatic, stopping);
            await fixture.App.TryAutoConnectAsync();
            Check(fixture.Engine.OpenCount == 0 && fixture.App.Session.Snapshot.State == PlaybackState.Idle &&
                fixture.App.Settings.LastReceiverId == "previous", "Stop while automatic admission waits for serialization prevents Start and preserves suppression", checks);
        }
        finally { restore.Release.TrySetResult(); await Task.WhenAll(updating, automatic, stopping).WaitAsync(Limit); }
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
        finally
        {
            fixture.Store.Fail = false; gate.Release.TrySetResult();
            try { await automatic.WaitAsync(Limit); } catch (IOException) when (failed) { }
        }
        Check(failed && fixture.Engine.OpenCount == 0 && fixture.App.Settings.LastReceiverId == "previous" && fixture.Store.Saved.MasterVolume == 100,
            "failed automatic persistence prevents admission and leaves attempts and last-used untouched", checks);
        await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
        Check(fixture.Engine.OpenCount == 1 && fixture.Store.Saved.MasterVolume == 37 && fixture.App.Settings.LastReceiverId == a.Id,
            "a candidate rejected by save failure can make a later valid automatic attempt", checks);
    }
    private static async Task ShutdownDuringAutomaticSaveAsync(List<string> checks)
    {
        var a = Pod("shutdown-a"); var settings = Settings(true); SeedAliases(settings, a);
        await using var fixture = await Fixture.CreateAsync(settings, [a]);
        var gate = fixture.Store.Hold(); fixture.App.MasterVolume = 29;
        var automatic = fixture.App.TryAutoConnectAsync();
        Task shutdown = Task.CompletedTask;
        try
        {
            await gate.Entered.Task.WaitAsync(Limit);
            shutdown = fixture.App.DisposeAsync().AsTask();
            var responsive = false;
            await Application.Current.Dispatcher.InvokeAsync(() => responsive = true, DispatcherPriority.Input);
            Check(responsive && !shutdown.IsCompleted, "shutdown waits asynchronously for held automatic persistence while the dispatcher stays responsive", checks);
            gate.Release.TrySetResult(); await Task.WhenAll(automatic, shutdown);
            Check(fixture.Engine.OpenCount == 0 && fixture.Store.Saved.MasterVolume == 29 && fixture.Store.Saved.LastReceiverId == "previous",
                "shutdown preserves valid pending edits and rejects delayed automatic admission", checks);
        }
        finally { gate.Release.TrySetResult(); await Task.WhenAll(automatic, shutdown).WaitAsync(Limit); }
    }

    private static async Task RejectedPlayRetainsPendingGainAsync(List<string> checks)
    {
        var current = Pod("pending-gain-current");
        var other = Pod("pending-gain-rejected", "192.0.2.20");
        var settings = Settings(); SeedAliases(settings, current, other);
        await using var fixture = await Fixture.CreateAsync(settings, [current, other]);
        await fixture.App.ToggleAsync(current);
        await StateAsync(fixture, PlaybackState.Streaming);
        var process = fixture.Engine.Connections.Single();
        var owner = fixture.App.Session.Capture();
        Check(CurrentGain(process) == 1, "the original worker starts at the saved master gain", checks);

        var save = fixture.Store.Hold();
        fixture.App.MasterVolume = 37;
        var flushing = fixture.App.FlushVolumeAsync();
        Task discovery = Task.CompletedTask;
        Task<Exception?> playing = Task.FromResult<Exception?>(null);
        try
        {
            await save.Entered.Task.WaitAsync(Limit);
            // Eligibility is queued before Play's own flush. Both wait behind the
            // entered save, while Play captures the still-online selection.
            discovery = fixture.App.ApplyDiscoveryAsync([current]);
            playing = AttemptPlayAsync(fixture.App, other);
            Check(!discovery.IsCompleted && !playing.IsCompleted
                && fixture.App.Session.Capture().Owner == owner.Owner
                && fixture.App.Session.Capture().AutomaticIntent != owner.AutomaticIntent,
                "a new Play invalidates the held gain update before queued discovery removes its receiver", checks);
            var staleAccepted = await fixture.App.Session.TryUpdateSettingsAsync(fixture.App.Settings.Clone(), owner);
            Check(!staleAccepted && CurrentGain(process) == 1,
                "the old captured settings update is legitimately rejected without altering the owned worker", checks);

            save.Release.TrySetResult();
            await Task.WhenAll(flushing, discovery).WaitAsync(Limit);
            var rejection = await playing.WaitAsync(Limit);
            await DrainAsync();
            Check(rejection is InvalidOperationException
                && rejection.Message == L.Get("The receiver is unavailable.")
                && fixture.App.Session.Capture().Owner == owner.Owner
                && fixture.Engine.OpenCount == 1 && !process.Disposed
                && fixture.App.Snapshot.State == PlaybackState.Streaming
                && fixture.App.Settings.MasterVolume == 37 && fixture.Store.Saved.MasterVolume == 37
                && fixture.App.Settings.LastReceiverId == current.Id,
                "an unavailable queued Play preserves the original stream and the successfully saved volume", checks);

            // Controlled scheduling substitutes this explicit flush for the next
            // normal debounce tick. Persistence must not consume an unapplied gain.
            await fixture.App.FlushVolumeAsync().WaitAsync(Limit);
            Check(CurrentGain(process) == 0.37 && fixture.App.Session.Capture().Owner == owner.Owner
                && fixture.Engine.OpenCount == 1 && !process.Disposed,
                "a saved gain still reaches the original worker after the newer Play is rejected", checks);
        }
        finally
        {
            save.Release.TrySetResult();
            await Task.WhenAll(flushing, discovery, playing).WaitAsync(Limit);
        }

        static double? CurrentGain(ScriptedConnection process) =>
            process.Commands.Last(command => command.Name is "start" or "set_gain").Parameters.Number("gain");

        static async Task<Exception?> AttemptPlayAsync(AppViewModel app, Receiver receiver)
        {
            try { await app.ToggleAsync(receiver); return null; }
            catch (InvalidOperationException error) { return error; }
        }
    }

    private static async Task OtherSavesRetainPendingGainAsync(List<string> checks)
    {
        foreach (var consumer in new[] { "identity discovery", "settings Apply" })
        {
            const string address = "192.0.2.10";
            var current = Pod(ReceiverIdentity.Endpoint(address, 7000), address);
            var canonical = Pod("saved-gain-canonical", address);
            var other = Pod("saved-gain-unavailable", "192.0.2.20");
            var settings = Settings(); settings.Options(current.Id).SampleRate = "48000";
            SeedAliases(settings, other);
            await using var fixture = await Fixture.CreateAsync(settings, [current, other]);
            await fixture.App.ToggleAsync(current); await StateAsync(fixture, PlaybackState.Streaming);
            var process = fixture.Engine.Connections.Single();
            var owner = fixture.App.Session.Capture(); var saves = fixture.Store.SaveCalls;
            var save = fixture.Store.Hold(); fixture.App.MasterVolume = 37;
            Task transaction;
            if (consumer == "identity discovery") transaction = fixture.App.ApplyDiscoveryAsync([canonical]);
            else
            {
                var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
                draft.Options(other.Id).Hidden = true;
                transaction = fixture.App.ApplyAsync(baseline, draft);
            }
            Task<Exception?> playing = Task.FromResult<Exception?>(null);
            try
            {
                await save.Entered.Task.WaitAsync(Limit);
                playing = AttemptPlayAsync(fixture.App, other);
                Check(!playing.IsCompleted && fixture.App.Session.Capture().Owner == owner.Owner
                    && fixture.App.Session.Capture().AutomaticIntent != owner.AutomaticIntent,
                    "a new Play waits behind a save that consumed the pending panel gain: " + consumer, checks);
                save.Release.TrySetResult(); await transaction.WaitAsync(Limit);
                var error = await playing.WaitAsync(Limit);
                Check(error is InvalidOperationException && error.Message == L.Get("The receiver is unavailable.")
                    && fixture.Store.Saved.MasterVolume == 37 && fixture.App.Settings.MasterVolume == 37,
                    "the rejected click leaves the consumed panel edit durably saved: " + consumer, checks);
                await fixture.App.FlushVolumeAsync();
                // Controller changes queue the view-model snapshot on the dispatcher.
                // Join that publication before checking the panel's current identity.
                await DrainAsync();
                Check(process.Commands.Last(command => command.Name is "start" or "set_gain").Parameters.Number("gain") == .37
                    && fixture.Store.SaveCalls == saves + 1 && fixture.App.Session.Capture().Owner == owner.Owner
                    && fixture.App.Snapshot.State == PlaybackState.Streaming && !process.Disposed
                    && fixture.Engine.OpenCount == 1,
                    "a consumed dirty gain reaches the same worker without repeating its save: " + consumer, checks);
                if (consumer == "identity discovery")
                    Check(fixture.App.Snapshot.Receiver?.Id == canonical.Id
                        && fixture.Store.Saved.ReadOptions(canonical.Id).SampleRate == "48000"
                        && !fixture.Store.Saved.Receivers.ContainsKey(current.Id),
                        "the gain retry preserves canonical identity persistence and the owned stream profile", checks);
            }
            finally
            {
                save.Release.TrySetResult(); await Task.WhenAll(transaction, playing).WaitAsync(Limit);
            }
        }

        static async Task<Exception?> AttemptPlayAsync(AppViewModel app, Receiver receiver)
        {
            try { await app.ToggleAsync(receiver); return null; }
            catch (InvalidOperationException error) { return error; }
        }
    }

    private static async Task VolumeEditsCoalesceAcrossLiveApplicationAsync(List<string> checks)
    {
        var receiver = Pod("coalesced-gain"); var settings = Settings(); SeedAliases(settings, receiver);
        await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
        await fixture.App.ToggleAsync(receiver); await StateAsync(fixture, PlaybackState.Streaming);
        var process = fixture.Engine.Connections.Single(); var owner = fixture.App.Session.Capture();
        var saves = fixture.Store.SaveCalls; var applying = fixture.Audio.HoldRestore();
        fixture.App.MasterVolume = 37;
        var flushing = fixture.App.FlushVolumeAsync();
        try
        {
            await applying.Entered.Task.WaitAsync(Limit);
            Check(fixture.Store.Saved.MasterVolume == 37 && !flushing.IsCompleted,
                "the first volume edit is saved before its held live application", checks);
            fixture.App.MasterVolume = 53;
            applying.Release.TrySetResult(); await flushing.WaitAsync(Limit);
            await fixture.App.FlushVolumeAsync();
            Check(fixture.Store.Saved.MasterVolume == 53 && fixture.App.Settings.MasterVolume == 53
                && process.Commands.Last(command => command.Name == "set_gain").Parameters.Number("gain") == .53
                && fixture.Store.SaveCalls == saves + 2 && fixture.App.Session.Capture().Owner == owner.Owner
                && !process.Disposed && fixture.Engine.OpenCount == 1,
                "an edit made during live application survives and is saved and applied once at its latest gain", checks);
            await fixture.App.FlushVolumeAsync();
            Check(fixture.Store.SaveCalls == saves + 2,
                "a clean flush after coalescing does not write settings again", checks);
        }
        finally { applying.Release.TrySetResult(); await flushing.WaitAsync(Limit); }
    }

    private static async Task EligibilityArrivalAsync(List<string> checks)
    {
        foreach (var change in new[] { "discovery", "apply" })
        {
            var receiver = Pod("arrival-a"); var settings = Settings(true); SeedAliases(settings, receiver);
            await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
            var restore = fixture.Audio.HoldRestore(); Task automatic = Task.CompletedTask, transaction = Task.CompletedTask;
            var holding = fixture.App.Session.UpdateSettingsAsync(settings);
            try
            {
                await restore.Entered.Task.WaitAsync(Limit);
                var captured = fixture.App.Session.Capture();
                automatic = fixture.App.TryAutoConnectAsync();
                Check(!automatic.IsCompleted, "automatic request holds the settings gate while waiting for controller serialization: " + change, checks);
                if (change == "discovery")
                {
                    // Publish invokes the real AppViewModel subscription immediately; reconciliation
                    // itself is still queued behind the settings gate held by automatic admission.
                    transaction = CatalogAsync(fixture, () => fixture.App.AllReceivers.All(candidate => !candidate.Online));
                    fixture.Discovery.Items = []; fixture.Discovery.Publish();
                }
                else
                {
                    var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
                    draft.Options(receiver.Id).Hidden = true;
                    transaction = fixture.App.ApplyAsync(baseline, draft);
                    Check(!transaction.IsCompleted, "settings Apply is queued behind automatic admission", checks);
                }
                Check(fixture.App.Session.Capture().Owner == captured.Owner &&
                    fixture.App.Session.Capture().AutomaticIntent == captured.AutomaticIntent,
                    "queued eligibility arrivals do not invalidate explicit intent or replace its owner: " + change, checks);
                restore.Release.TrySetResult();
                await Task.WhenAll(holding, automatic, transaction).WaitAsync(Limit);
                await DrainAsync();
                Check(fixture.Engine.OpenCount == 0 && fixture.App.Settings.LastReceiverId == "previous",
                    "arrival fence rejects stale automatic admission before engine startup: " + change, checks);
                if (change == "discovery") await fixture.App.ApplyDiscoveryAsync([receiver]);
                else
                {
                    var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
                    draft.Options(receiver.Id).Hidden = false;
                    await fixture.App.ApplyAsync(baseline, draft);
                }
                await fixture.App.TryAutoConnectAsync(); await StateAsync(fixture, PlaybackState.Streaming);
                Check(fixture.Engine.OpenCount == 1, "arrival rejection leaves the receiver available for a later valid attempt: " + change, checks);
            }
            finally { restore.Release.TrySetResult(); await Task.WhenAll(holding, automatic, transaction).WaitAsync(Limit); }
        }
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
                fixture.App.Snapshot.Receiver?.Id == manual.Id && fixture.App.Settings.LastReceiverId == manual.Id,
                $"manual route preserves its exact endpoint, identity and options beside a {condition} native group at port {port}", checks);
            await fixture.App.ApplyDiscoveryAsync([]);
            Check(fixture.App.Snapshot.IsActive && fixture.App.AllReceivers.Single(receiver => receiver.IsManual).Id == manual.Id,
                "manual persistence survives empty discovery: " + condition + "/" + port, checks);
        }
    }
    private static async Task QueuedManualSelectionAsync(List<string> checks)
    {
        foreach (var edit in new[] { "remove", "address", "port" })
        {
            var settings = Settings();
            var saved = new ManualReceiver("manual-m", "Synthetic manual receiver", "192.0.2.10", 7000);
            settings.ManualReceivers.Add(saved);
            await using var fixture = await Fixture.CreateAsync(settings, []);
            var selected = fixture.App.AllReceivers.Single();
            var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
            if (edit == "remove") draft.ManualReceivers.Clear();
            else draft.ManualReceivers[0] = edit == "address" ? saved with { Host = "192.0.2.20" } : saved with { Port = 7001 };
            var gate = fixture.Store.Hold();
            var apply = fixture.App.ApplyAsync(baseline, draft);
            Task play = Task.CompletedTask;
            try
            {
                await gate.Entered.Task.WaitAsync(Limit);
                play = fixture.App.ToggleAsync(selected);
                Check(!play.IsCompleted, "manual Play waits for the current settings write: " + edit, checks);
                gate.Release.TrySetResult();
                await apply.WaitAsync(Limit);
                var rejected = false;
                try { await play.WaitAsync(Limit); }
                catch (InvalidOperationException error) when (error.Message == L.Get("The receiver is unavailable.")) { rejected = true; }
                Check(rejected && fixture.Engine.OpenCount == 0 && fixture.App.Session.Snapshot.State == PlaybackState.Idle
                    && fixture.App.Settings.LastReceiverId == settings.LastReceiverId && fixture.Store.Saved.LastReceiverId == settings.LastReceiverId
                    && fixture.Store.Saved.ManualReceivers.SequenceEqual(draft.ManualReceivers),
                    "the stale manual click cannot start or undo a committed endpoint change: " + edit, checks);
                if (edit != "remove")
                {
                    var current = fixture.App.AllReceivers.Single();
                    await fixture.App.ToggleAsync(current);
                    await StateAsync(fixture, PlaybackState.Streaming);
                    var start = fixture.Engine.Connections.Single().Commands.Single(command => command.Name == "start");
                    var peer = start.Parameters.GetProperty("peers")[0];
                    Check(peer.Text("host") == current.Address && peer.Integer("port") == current.Port,
                        "a new click uses the newly saved manual endpoint: " + edit, checks);
                }
            }
            finally
            {
                gate.Release.TrySetResult();
                await apply.WaitAsync(Limit);
                try { await play.WaitAsync(Limit); }
                catch (InvalidOperationException error) when (error.Message == L.Get("The receiver is unavailable.")) { }
            }
        }
    }
    private static async Task RetainedStatesAsync(List<string> checks)
    {
        foreach (var original in new[] { false, true })
        foreach (var state in new[] { PlaybackState.Connecting, PlaybackState.Pairing, PlaybackState.Streaming, PlaybackState.Standby })
        {
            var group = Pair(); var settings = Settings(); settings.MuteWhileStreaming = true;
            settings.StandbyEnabled = true; SeedAliases(settings, group);
            await using var fixture = await Fixture.CreateAsync(settings, [group], original);
            var process = new ScriptedConnection(state is PlaybackState.Streaming or PlaybackState.Standby);
            fixture.Engine.Enqueue(process);
            if (state == PlaybackState.Pairing)
                await fixture.App.PairAsync(group, (_, _) => throw new InvalidOperationException("This fixture holds Pair before pin_required."));
            else await fixture.App.ToggleAsync(group);
            await (state == PlaybackState.Pairing ? process.PairSent.Task : process.StartSent.Task).WaitAsync(Limit);
            if (state == PlaybackState.Standby)
            {
                await StateAsync(fixture, PlaybackState.Streaming);
                Check(fixture.Audio.HasSavedBit && fixture.Audio.MuteBit, "streaming captured the original mute bit before entering Standby", checks);
                process.Emit(new { @event = "standby", reason = "silence" });
            }
            await StateAsync(fixture, state);
            if (state == PlaybackState.Streaming)
                Check(fixture.Audio.HasSavedBit && fixture.Audio.MuteBit, "streaming captured the original mute bit before discovery changed", checks);
            if (state == PlaybackState.Standby)
                Check(!fixture.Audio.HasSavedBit && fixture.Audio.MuteBit == original, "entering Standby restored the original mute bit", checks);
            var owner = fixture.App.Session.Capture().Owner;
            var snapshots = new IReadOnlyList<Receiver>[] { [], [group with { Members = [group.Members[0]] }], [], [] };
            foreach (var snapshot in snapshots)
            {
                await fixture.App.ApplyDiscoveryAsync(snapshot); await DrainAsync();
                var current = fixture.App.Session.Capture();
                var row = fixture.App.Receivers.Single(receiver => receiver.Receiver.Id == group.Id);
                var catalog = fixture.App.AllReceivers.Single(receiver => receiver.Id == group.Id);
                Check(current.Owner == owner && current.Snapshot.State == state && current.Snapshot.Receiver?.Members.Length == 2 &&
                    fixture.Engine.OpenCount == 1 && !process.Disposed && !process.Commands.Any(command => command.Name == "stop") &&
                    (!catalog.Online || !catalog.Complete) && row.Active && row.NeedsDisconnect &&
                    ReferenceEquals(row.PlaybackCommand, fixture.App.StopCommand) && row.PlaybackCommand.CanExecute(null),
                    "passive absence retains transport and the Stop command in " + state + " (prior mute " + original + "): " +
                    $"owner={current.Owner == owner}; controller={current.Snapshot.State}; app={fixture.App.Snapshot.State}; " +
                    $"current publication={ReferenceEquals(current.Snapshot, fixture.App.Snapshot)}; rate={current.Snapshot.StreamRate}; " +
                    $"members={current.Snapshot.Receiver?.Members.Length}; opens={fixture.Engine.OpenCount}; disposed={process.Disposed}; " +
                    $"stop sent={process.Commands.Any(command => command.Name == "stop")}; catalog={catalog.Online}/{catalog.Complete}; " +
                    $"active={row.Active}; can play={row.CanPlay}; stop enabled={row.PlaybackCommand.CanExecute(null)}; text={row.StateText}", checks);
            }
            await fixture.App.ApplyDiscoveryAsync([group]); await DrainAsync();
            Check(fixture.Engine.OpenCount == 1 && fixture.App.Session.Capture().Owner == owner &&
                fixture.App.Receivers.Single().Receiver is { Online: true, Complete: true },
                "reappearance restores catalog presentation without replacing " + state, checks);
            await fixture.App.ApplyDiscoveryAsync([]); await DrainAsync();
            // Pairing remains before pin_required: no modal PIN dialog hides this command.
            fixture.App.Receivers.Single().PlaybackCommand.Execute(null);
            await StateAsync(fixture, PlaybackState.Idle); await process.DisposalCompleted.Task.WaitAsync(Limit); await DrainAsync();
            Check(fixture.App.Receivers.Count == 0 && fixture.Engine.OpenCount == 1 &&
                fixture.Audio.MuteBit == original && !fixture.Audio.HasSavedBit,
                "explicit Stop removes the inactive overlay and restores the exact prior mute bit from " + state + "/" + original, checks);
        }
    }
    private static async Task WorkerCleanupFailureRetainsStopAsync(List<string> checks, bool originalMute)
    {
        var receiver = Pod("offline-cleanup");
        var settings = Settings();
        settings.MuteWhileStreaming = true;
        SeedAliases(settings, receiver);
        await using var fixture = await Fixture.CreateAsync(settings, [receiver], originalMute);
        fixture.Discovery.Items = [receiver];
        var process = new ScriptedConnection();
        var restoredBeforeRetry = false;
        process.BeforeDispose = attempt =>
        {
            if (attempt == 1) throw new EngineShutdownException(process, new IOException("synthetic engine cleanup failure"));
            restoredBeforeRetry = !fixture.Audio.HasSavedBit && fixture.Audio.MuteBit == originalMute;
        };
        fixture.Engine.Enqueue(process);
        var panel = new ControlPanel(fixture.App) { MenuOpen = true };
        Gate? restore = null;
        var label = " (prior mute " + originalMute + ")";
        try
        {
            panel.ShowPanel();
            await DrainAsync();
            await fixture.App.ToggleAsync(receiver);
            await StateAsync(fixture, PlaybackState.Streaming);
            Check(fixture.Audio.HasSavedBit && fixture.Audio.MuteBit,
                "the synthetic stream owns the prior mute bit before the terminal fault" + label, checks);

            fixture.Discovery.Items = [];
            var absent = CatalogAsync(fixture, () => fixture.App.AllReceivers.All(row => !row.Online));
            fixture.Discovery.Publish();
            await absent;
            await DrainAsync();
            restore = fixture.Audio.HoldRestore();
            process.Emit(new { @event = "error", code = "peer_closed", channel = "control", retryable = false, message = "synthetic terminal fault" });
            await StateAsync(fixture, snapshot => snapshot.State == PlaybackState.Error
                && snapshot.Diagnostics?.LastFault?.Code == "engine_cleanup_failed");
            await restore.Entered.Task.WaitAsync(Limit);
            await DrainAsync();

            var row = fixture.App.Receivers.SingleOrDefault(item => item.Receiver.Id == receiver.Id);
            Check(fixture.App.Session.NeedsCleanup && row is { Active: false, NeedsDisconnect: true, Receiver.Online: false }
                && process.DisposalAttempts == 1 && !process.Disposed && fixture.Audio.HasSavedBit,
                "worker cleanup failure keeps the unavailable receiver's disconnect row while mute restoration is pending" + label, checks);
            var stop = UiSmoke.Descendants(panel).OfType<Button>().Single(button =>
                ReferenceEquals(button.DataContext, row) && ReferenceEquals(button.Command, fixture.App.StopCommand));
            var status = (TextBlock)panel.FindName("StatusDetailText");
            Check(stop.IsVisible && stop.IsEnabled && status.IsVisible
                && status.Text == L.Get("The audio engine could not stop. Try disconnecting again."),
                "the actual panel exposes Stop and the cleanup retry hint after a worker failure" + label, checks);

            ((IInvokeProvider)new ButtonAutomationPeer(stop).GetPattern(PatternInterface.Invoke)).Invoke();
            await DrainAsync();
            Check(fixture.App.IsStopping && process.DisposalAttempts == 1 && !process.Disposed
                && fixture.Audio.HasSavedBit && fixture.Engine.OpenCount == 1,
                "Stop waits for the old worker's mute cleanup before retrying engine disposal" + label, checks);

            restore.Release.TrySetResult();
            await process.DisposalCompleted.Task.WaitAsync(Limit);
            await StateAsync(fixture, PlaybackState.Idle);
            await DrainAsync();
            Check(restoredBeforeRetry && !fixture.Audio.HasSavedBit && fixture.Audio.MuteBit == originalMute
                && process.DisposalAttempts == 2 && fixture.Engine.OpenCount == 1 && !fixture.App.Session.NeedsCleanup
                && !fixture.App.StopCommand.CanExecute(null) && fixture.App.Receivers.Count == 0,
                "one visible Stop settles the original engine and mute bit, then removes the offline row" + label, checks);
        }
        finally
        {
            restore?.Release.TrySetResult();
            try
            {
                if (!process.Disposed)
                {
                    try { await fixture.App.StopAsync().WaitAsync(Limit); }
                    catch (EngineShutdownException) { await fixture.App.StopAsync().WaitAsync(Limit); }
                }
            }
            finally { panel.ShutdownPanel(); }
        }
    }

    private static async Task RetainOnInterfaceChangeAsync(List<string> checks)
    {
        var group = Pair();
        var settings = Settings();
        SeedAliases(settings, group);
        await using var fixture = await Fixture.CreateAsync(settings, [group]);
        fixture.Discovery.Items = [group];
        await fixture.App.ToggleAsync(group);
        await StateAsync(fixture, PlaybackState.Streaming);
        var owner = fixture.App.Session.Capture().Owner;
        var process = fixture.Engine.Connections.Single();
        var baseline = fixture.App.Settings.Clone(); var draft = baseline.Clone();
        draft.DiscoveryInterfaceId = "6e174d01-a625-4ee2-bb6e-262ed5371694";
        await fixture.App.ApplyAsync(baseline, draft);
        await CatalogAsync(fixture, () => fixture.App.AllReceivers.Count == 0);
        await DrainAsync();

        var active = fixture.App.Session.Capture();
        var row = fixture.App.Receivers.Single();
        Check(fixture.Discovery.SelectedInterface == draft.DiscoveryInterfaceId
            && fixture.Store.Saved.DiscoveryInterfaceId == draft.DiscoveryInterfaceId
            && active.Owner == owner && active.Snapshot is { State: PlaybackState.Streaming, Receiver.Members.Length: 2 }
            && fixture.Engine.OpenCount == 1 && !process.Disposed && !process.Commands.Any(command => command.Name == "stop")
            && row.Active && !row.Receiver.Online && ReferenceEquals(row.PlaybackCommand, fixture.App.StopCommand),
            "switching to an unavailable adapter retains the owned stream and exposes only its active Stop row", checks);
        row.PlaybackCommand.Execute(null);
        await StateAsync(fixture, PlaybackState.Idle);
        await process.DisposalCompleted.Task.WaitAsync(Limit);
        Check(fixture.App.Receivers.Count == 0 && fixture.Engine.OpenCount == 1,
            "Stop clears the retained row after an adapter change", checks);
    }
    private static async Task ResetDuringAutomaticSaveAsync(List<string> checks)
    {
        var receiver = Pod("reset-a"); var settings = Settings(true); SeedAliases(settings, receiver);
        await using var fixture = await Fixture.CreateAsync(settings, [receiver]);
        var save = fixture.Store.Hold(); fixture.App.MasterVolume = 37;
        var automatic = fixture.App.TryAutoConnectAsync(); Task reset = Task.CompletedTask;
        try
        {
            await save.Entered.Task.WaitAsync(Limit);
            var captured = fixture.App.Session.Capture(); reset = fixture.App.ResetAsync();
            Check(!reset.IsCompleted && fixture.App.Session.Capture().AutomaticIntent != captured.AutomaticIntent,
                "Reset invalidates delayed automatic intent synchronously before its settings gate wait", checks);
            save.Release.TrySetResult(); await Task.WhenAll(automatic, reset).WaitAsync(Limit); await DrainAsync();
            Check(fixture.Engine.OpenCount == 0 && fixture.Store.ResetCalls == 1 && fixture.Restart.Calls == 1 &&
                fixture.App.IsClosing && fixture.App.Session.Snapshot.State == PlaybackState.Idle &&
                fixture.Store.Saved.LastReceiverId is null && !fixture.Audio.HasSavedBit,
                "Reset rejects stale Start, clears the synthetic store and schedules only the fake restart", checks);
        }
        finally { save.Release.TrySetResult(); await Task.WhenAll(automatic, reset).WaitAsync(Limit); }
    }
    private static async Task ShellShutdownRetainsVisibleStopAsync(List<string> checks, bool holdApply)
    {
        var receiver = Pod("shell-owned-stop");
        var other = Pod("shell-new-play", "192.0.2.20");
        var settings = Settings();
        settings.MuteWhileStreaming = true;
        SeedAliases(settings, receiver, other);
        // Cover both original mute bits across the two shutdown paths.
        await using var fixture = await Fixture.CreateAsync(settings, [receiver, other], original: holdApply);
        fixture.Discovery.Items = [receiver, other];
        await fixture.App.ToggleAsync(receiver);
        await StateAsync(fixture, PlaybackState.Streaming);
        var process = fixture.Engine.Connections.Single();
        var owner = fixture.App.Session.Capture().Owner;
        var panel = new ControlPanel(fixture.App) { MenuOpen = true };
        using var editor = holdApply ? new SettingsViewModel(fixture.App) : null;
        var engineCleanup = new Gate();
        Gate? save = null;
        Task<bool> apply = Task.FromResult(true);
        Task shutdown = Task.CompletedTask;
        try
        {
            panel.ShowPanel();
            await DrainAsync();
            Check(fixture.App.Receivers.Any(row => row.Receiver.Id == other.Id),
                "opening the panel keeps the synthetic inactive receiver in the catalog", checks);
            Check(fixture.Audio.HasSavedBit && fixture.Audio.MuteBit, "playback owns the saved mute bit before shell Quit", checks);
            if (editor is not null)
            {
                editor.Draft.LatencyMode = "buffered";
                save = fixture.Store.Hold();
                apply = editor.ApplyAsync();
                await save.Entered.Task.WaitAsync(Limit);
                Check(editor.IsApplying && !apply.IsCompleted,
                    "shell Quit races an accepted signature-changing Settings Apply", checks);
            }
            shutdown = ShellShutdown.RunAsync(fixture.App.BeginShutdown,
                () => editor?.ApplicationCompleted ?? Task.CompletedTask,
                () => new ValueTask(engineCleanup.WaitAsync()), fixture.App.DisposeAsync);
            Check(fixture.App.IsClosing && !shutdown.IsCompleted
                && (save is null || !engineCleanup.Entered.Task.IsCompleted),
                "Quit closes new admission before waiting for settings and engine cleanup", checks);
            save?.Release.TrySetResult();
            Check(await apply.WaitAsync(Limit), "the accepted Apply can finish during Quit", checks);
            await engineCleanup.Entered.Task.WaitAsync(Limit);
            await fixture.App.ToggleAsync(other).WaitAsync(Limit);
            await fixture.App.PairAsync(other, (_, _) => throw new InvalidOperationException("Shutdown must not request a PIN.")).WaitAsync(Limit);
            await fixture.App.TryAutoConnectAsync().WaitAsync(Limit);
            await DrainAsync();

            var active = fixture.App.Receivers.Single(row => row.Receiver.Id == receiver.Id);
            var inactive = fixture.App.Receivers.Single(row => row.Receiver.Id == other.Id);
            var stop = BoundPlaybackButton(panel, active);
            var play = BoundPlaybackButton(panel, inactive);
            Check(fixture.Engine.OpenCount == 1 && !process.Disposed && fixture.App.Session.Capture().Owner == owner
                && fixture.App.Snapshot.State == PlaybackState.Streaming
                && stop.IsVisible && stop.IsEnabled && ReferenceEquals(stop.Command, fixture.App.StopCommand)
                && play.IsVisible && !play.IsEnabled && !inactive.ToggleCommand.CanExecute(null),
                "Quit keeps the visible owned Stop enabled while disabling new Play", checks);
            if (holdApply)
                Check(fixture.Store.Saved.LatencyMode == "buffered" && fixture.App.Settings.LatencyMode == "buffered"
                    && fixture.Store.Saved.LastReceiverId == receiver.Id,
                    "Quit saves the accepted latency edit without starting a replacement worker", checks);

            ((IInvokeProvider)new ButtonAutomationPeer(stop).GetPattern(PatternInterface.Invoke)).Invoke();
            await StateAsync(fixture, PlaybackState.Idle);
            await process.DisposalCompleted.Task.WaitAsync(Limit);
            await DrainAsync();
            Check(fixture.App.Snapshot.State == PlaybackState.Idle && !active.Active
                && !fixture.App.StopCommand.CanExecute(null) && !BoundPlaybackButton(panel, active).IsEnabled
                && process.Commands.Count(command => command.Name == "stop") == 1 && fixture.Engine.OpenCount == 1
                && !fixture.Audio.HasSavedBit && fixture.Audio.MuteBit == holdApply && !shutdown.IsCompleted,
                "visible Stop publishes Idle and restores the original mute bit while Quit still waits for engine cleanup", checks);
        }
        finally
        {
            save?.Release.TrySetResult();
            engineCleanup.Release.TrySetResult();
            try { await Task.WhenAll(apply, shutdown).WaitAsync(Limit); }
            finally { panel.ShutdownPanel(); }
        }

        static Button BoundPlaybackButton(ControlPanel panel, ReceiverViewModel row) =>
            UiSmoke.Descendants(panel).OfType<Button>().Single(button =>
                ReferenceEquals(button.DataContext, row) && ReferenceEquals(button.Command, row.PlaybackCommand));
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
        public int SaveCalls { get; private set; }
        public int ResetCalls { get; private set; }
        public Task ResetAsync() { ResetCalls++; _saved = new(); return Task.CompletedTask; }
        public void Save(AppSettings settings)
        {
            if (Fail) throw new IOException("synthetic save failure");
            SaveCalls++;
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
        public bool MuteBit { get; private set; } = original;
        public bool HasSavedBit => _saved is not null;
        private bool? _saved;
        private Gate? _restoreGate;
        public Gate HoldRestore() => _restoreGate = new();
        public void Release() => _restoreGate?.Release.TrySetResult();
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        private Gate? _defaultEndpointLookup;
        public string DefaultEndpoint { get; set; } = "synthetic-endpoint-a";
        public Gate HoldNextDefaultEndpointLookup() => _defaultEndpointLookup = new();
        public async Task<string?> GetDefaultEndpointIdAsync()
        {
            if (Interlocked.Exchange(ref _defaultEndpointLookup, null) is { } gate) await gate.WaitAsync();
            return DefaultEndpoint;
        }
        public Task MuteAsync(string? endpointId)
        {
            _saved ??= MuteBit; MuteBit = true; return Task.CompletedTask;
        }
        public async Task RestoreAsync()
        {
            if (_restoreGate is { } gate) await gate.WaitAsync();
            if (_saved is { } saved) { MuteBit = saved; _saved = null; }
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
        public Action<int>? BeforeDispose { get; set; }
        private int _disposalAttempts;
        public int DisposalAttempts => Volatile.Read(ref _disposalAttempts);
        public bool Disposed { get; private set; }
        public string Session { get; private set; } = "";
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue(new(session, command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            if (command == "hello") { Session = session; Emit(new { @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "member_volume", "receiver_password", "application_capture", "receiver_ownership_guard", "forced_latency" } }); }
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
            var attempt = Interlocked.Increment(ref _disposalAttempts);
            BeforeDispose?.Invoke(attempt);
            Disposed = true; _events.Writer.TryComplete(); DisposalCompleted.TrySetResult(); return ValueTask.CompletedTask;
        }
    }
    private sealed class Engine : IEngineFactory
    {
        private readonly ConcurrentQueue<ScriptedConnection> _next = new();
        public ConcurrentQueue<ScriptedConnection> Connections { get; } = new();
        public int OpenCount;
        public void Enqueue(ScriptedConnection connection) => _next.Enqueue(connection);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref OpenCount);
            var connection = _next.TryDequeue(out var scripted) ? scripted : new ScriptedConnection();
            Connections.Enqueue(connection); return connection;
        }
    }
    private sealed class Restart : IRestartService
    {
        public int Calls { get; private set; }
        public void Schedule() => Calls++;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public Store Store { get; }
        public Audio Audio { get; }
        public Engine Engine { get; } = new();
        public UiSmoke.MockDiscovery Discovery { get; } = new() { Items = [] };
        public Restart Restart { get; } = new();
        public AppViewModel App { get; }
        private Fixture(AppSettings settings, bool original)
        {
            Store = new(settings); Audio = new(original);
            App = new(Store, Discovery, new UiSmoke.MockAutostart(), Audio, Engine, Application.Current.Dispatcher, restart: Restart, isolatedVerification: true);
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
