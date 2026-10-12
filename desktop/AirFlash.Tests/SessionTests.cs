using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SessionTests
{
    private static Receiver Pod(string id = "a") => new(id, id, "127.0.0.1");
    private static AppSettings Settings() => new() { AutoConnectOnDiscover = false, ForceReconnect = false, MasterVolume = 10 };
    private static readonly SessionTiming Fast = new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(10));
    private static EqualizerSettings Sound(double gain) { var eq = new EqualizerSettings { Enabled = true }; eq.SetBand(0, gain); return eq; }
    private static double EqGain(EngineCommand command) => command.Parameters.GetProperty("equalizer").GetProperty("band_gains_db")[0].GetDouble();
    [Fact]
    public async Task EqualizerStartsFromSavedSettingsAndDebouncesWithoutRestarting()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.Equalizer = Sound(2);
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(2, EqGain(process.Commands.Single(c => c.Command == "start")));
        var count = process.Commands.Count(c => c.Command == "set_equalizer"); var owner = Guid.NewGuid();
        var edits = Enumerable.Range(1, 10).Select(i => controller.PreviewEqualizerAsync(owner, Sound(i))).ToArray();
        await Task.WhenAll(edits);
        Assert.Equal(count + 1, process.Commands.Count(c => c.Command == "set_equalizer"));
        Assert.Equal(10, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
        Assert.Equal(2, settings.Equalizer.BandGainsDb[0]); Assert.Equal(1, factory.OpenCount);
        settings.MasterVolume = 45; await controller.UpdateSettingsAsync(settings);
        Assert.Equal(10, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
        await controller.ClearEqualizerPreviewAsync(owner);
        Assert.Equal(2, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
    }
    [Fact]
    public async Task EqualizerRollbackUsesLastApplyAndOldOwnerCannotClearNewPreview()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var oldOwner = Guid.NewGuid(); var newOwner = Guid.NewGuid();
        await controller.PreviewEqualizerAsync(oldOwner, Sound(4)); settings.Equalizer = Sound(4);
        await controller.UpdateSettingsAsync(settings); await controller.ClearEqualizerPreviewAsync(oldOwner);
        var pending = controller.PreviewEqualizerAsync(oldOwner, Sound(7));
        await controller.ClearEqualizerPreviewAsync(oldOwner); await pending;
        Assert.Equal(4, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
        await controller.PreviewEqualizerAsync(newOwner, Sound(9));
        await controller.ClearEqualizerPreviewAsync(oldOwner);
        Assert.Equal(9, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
        await controller.ClearEqualizerPreviewAsync(newOwner);
        Assert.Equal(4, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
    }
    [Fact]
    public async Task ContinuousEqualizerDraggingSendsWhileInputIsStillChanging()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var count = process.Commands.Count(c => c.Command == "set_equalizer"); var owner = Guid.NewGuid();
        var edits = new List<Task>();
        for (var i = 1; i <= 10; i++) { edits.Add(controller.PreviewEqualizerAsync(owner, Sound(i))); await Task.Delay(20); }
        Assert.True(process.Commands.Count(c => c.Command == "set_equalizer") >= count + 2);
        await Task.WhenAll(edits); Assert.Equal(10, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
    }
    [Fact]
    public async Task EqualizerPreviewSurvivesReconnectAndReceiverSwitch()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var third = new FakeConnection();
        await using var controller = new SessionController(new FakeFactory(first, second, third), new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.PreviewEqualizerAsync(Guid.NewGuid(), Sound(6));
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(6, EqGain(first.Commands.Single(c => c.Command == "start")));
        first.Emit("error", message: "network lost"); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(6, EqGain(second.Commands.Single(c => c.Command == "start")));
        await controller.StartAsync(Pod("other"), settings); await third.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(6, EqGain(third.Commands.Single(c => c.Command == "start")));
    }
    [Fact]
    public async Task EqualizerErrorsStayNonfatalAndIgnoreObsoleteSequences()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var feedback = new ConcurrentQueue<string?>(); controller.EqualizerFeedback += feedback.Enqueue;
        await controller.PreviewEqualizerAsync(Guid.NewGuid(), Sound(4));
        var sequence = process.Commands.Last(c => c.Command == "set_equalizer").Parameters.Integer("sequence");
        process.EmitData(new { @event = "equalizer_error", sequence = sequence - 1, message = "obsolete" });
        process.EmitData(new { @event = "equalizer_error", sequence, message = "invalid equalizer" });
        await Until(() => feedback.Contains("invalid equalizer"));
        Assert.DoesNotContain("obsolete", feedback); Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
        process.EmitData(new { @event = "equalizer_changed", sequence }); await Until(() => feedback.Contains(null));
    }
    [Fact]
    public async Task IdentityUpgradeRetainsStreamAndUsesMigratedStandbySetting()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true; settings.Options("old").StandbySeconds = 5; settings.Options("old").LatencyMode = "realtime";
        await controller.StartAsync(Pod("old"), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        connection.EmitData(new { @event = "negotiated", receiver_id = "old", host = "127.0.0.1", port = 7000, codec = "alac", timing = "ptp", clock_role = "follow" });
        connection.EmitData(new { @event = "capture_metrics", metrics = new { input_rate = 48000 } });
        await Until(() => controller.Snapshot.Diagnostics?.Negotiated?.Count == 1);
        ReceiverCatalog.ApplyMigrations(settings, new Dictionary<string, string> { ["old"] = "new" });
        await controller.UpdateReceiverAsync(Pod("new"), settings);
        Assert.Equal(1, factory.OpenCount); Assert.Equal("new", controller.Snapshot.Receiver!.Id); Assert.Equal(120, controller.Snapshot.TargetLatency);
        var negotiated = Assert.Single(controller.Snapshot.Diagnostics!.Negotiated!);
        Assert.Equal("new", negotiated.Text("receiver_id")); Assert.Equal("follow", negotiated.Text("clock_role"));
        Assert.Equal(5, connection.Commands.Last(c => c.Command == "set_standby").Parameters.Number("silence_seconds"));
        connection.EmitData(new { @event = "standby", reason = "silence" });
        await Until(() => controller.Snapshot.State == PlaybackState.Standby);
        Assert.Equal(1, factory.OpenCount); Assert.False(connection.Disposed);
    }
    [Fact]
    public async Task IdentityUpgradeWithChangedLatencyRestartsOnlyOnce()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod("old"), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.Options("new").LatencyMode = "realtime";
        await controller.UpdateReceiverAsync(Pod("new"), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(2, factory.OpenCount); Assert.Equal(120, second.Commands.Single(c => c.Command == "start").Parameters.Number("latency_ms"));
    }
    [Fact]
    public async Task ReorderingRenamedStereoMembersDoesNotRestartTransport()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var pair = Pod("stereo:pair") with { Members = [Pod("old-a"), Pod("old-b") with { Address = "127.0.0.2" }] };
        await controller.StartAsync(pair, Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var updated = pair with { Members = [pair.Members[1] with { Id = "new-a" }, pair.Members[0] with { Id = "new-b" }] };
        await controller.UpdateReceiverAsync(updated, Settings());
        Assert.Equal(1, factory.OpenCount); Assert.Equal("new-a", controller.Snapshot.Receiver!.Members[0].Id);
    }
    [Fact]
    public async Task PassiveStereoRefreshRetainsTransportAcrossLeaderAndIdentityChanges()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection); var logs = new ConcurrentQueue<string>();
        await using var controller = new SessionController(factory, new FakeAudio(), logs.Enqueue, Fast);
        var left = Pod("left") with { IsLeader = true }; var right = Pod("right") with { Address = "127.0.0.2" };
        var pair = Pod("stereo:pair") with { StereoId = "pair", Members = [left, right] };
        await controller.StartAsync(pair, Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var commands = connection.Commands.Count;
        var migrations = new Dictionary<string, string> { ["left"] = "new-left", ["right"] = "new-right" };
        for (var i = 0; i < 20; i++)
        {
            var next = pair with { Name = "Renamed pair", Members = [right with { Id = "new-right", IsLeader = i % 2 == 0 }, left with { Id = "new-left", IsLeader = i % 2 != 0, Features = 42 }] };
            await controller.RefreshReceiverAsync(next, controller.Snapshot, migrations);
        }
        Assert.Equal(1, factory.OpenCount); Assert.False(connection.Disposed); Assert.Equal(commands, connection.Commands.Count);
        Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State); Assert.Equal(0, controller.Snapshot.Diagnostics!.ReconnectCount);
        Assert.Equal("Renamed pair", controller.Snapshot.Receiver!.Name);
        Assert.Equal("new-left", controller.Snapshot.Receiver.Members[0].Id); Assert.True(controller.Snapshot.Receiver.Members[0].IsLeader);
        Assert.Null(controller.Snapshot.Receiver.Members[0].Features);
        Assert.Contains(logs, message => message.Contains("Discovery refresh retained transport"));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PassiveAddressRefreshIsUsedOnlyOnPermittedRetry(bool returnToOriginal)
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var changed = Pod() with { Address = "127.0.0.2" };
        await controller.RefreshReceiverAsync(changed, controller.Snapshot, new Dictionary<string, string>());
        if (returnToOriginal) await controller.RefreshReceiverAsync(Pod(), controller.Snapshot, new Dictionary<string, string>());
        // A subsequent incomplete snapshot must not erase the last complete target.
        await controller.RefreshReceiverAsync(changed with { Online = false }, controller.Snapshot, new Dictionary<string, string>());
        Assert.Equal(1, factory.OpenCount); Assert.False(first.Disposed); Assert.Equal("127.0.0.1", controller.Snapshot.Receiver!.Address);
        first.EmitData(new { @event = "error", code = "peer_closed", retryable = true, message = "lost transport" });
        await Until(() => factory.OpenCount == 2 && controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(returnToOriginal ? "127.0.0.1" : "127.0.0.2", second.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers")[0].Text("host"));
        Assert.Equal(1, controller.Snapshot.Diagnostics!.ReconnectCount);
    }
    [Fact]
    public async Task PassiveRefreshDoesNotEnableOrdinaryRetryOrRestartAfterStop()
    {
        var first = new FakeConnection(); var factory = new FakeFactory(first);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var expected = controller.Snapshot; var next = Pod() with { Address = "127.0.0.2" };
        await controller.RefreshReceiverAsync(next, expected, new Dictionary<string, string>());
        first.EmitData(new { @event = "error", code = "peer_closed", retryable = true, message = "lost transport" });
        await Until(() => first.Disposed && controller.Snapshot.State == PlaybackState.Error);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, controller.Snapshot.Diagnostics!.ReconnectCount);
        await controller.StopAsync(); await controller.RefreshReceiverAsync(next, expected, new Dictionary<string, string>());
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Null(controller.Snapshot.Receiver);
    }
    [Fact]
    public async Task DelayedDiscoveryCannotModifyNewPlaybackOfTheSameReceiver()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var obsolete = controller.Snapshot;
        await controller.StartAsync(Pod(), Settings()); await Until(() => second.Started.Task.IsCompleted && controller.Snapshot.State == PlaybackState.Streaming);
        await controller.RefreshReceiverAsync(Pod() with { Name = "Stale", Address = "127.0.0.2" }, obsolete, new Dictionary<string, string>());
        Assert.False(await controller.StopForDiscoveryConflictAsync(obsolete));
        Assert.Equal("a", controller.Snapshot.Receiver!.Name); Assert.Equal(2, factory.OpenCount); Assert.False(second.Disposed);
    }
    [Fact]
    public async Task PassiveRefreshCannotModifyPairing()
    {
        var pair = new FakeConnection(); var factory = new FakeFactory(pair); var pin = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.PairAsync(Pod(), Settings(), (_, token) => pin.Task.WaitAsync(token));
        await Until(() => pair.Commands.Any(c => c.Command == "pair"));
        await controller.RefreshReceiverAsync(Pod() with { Name = "Stale" }, controller.Snapshot, new Dictionary<string, string>());
        Assert.False(await controller.StopForDiscoveryConflictAsync(controller.Snapshot));
        Assert.Equal(PlaybackState.Pairing, controller.Snapshot.State); Assert.Equal("a", controller.Snapshot.Receiver!.Name);
        await controller.StopAsync();
    }
    [Fact]
    public async Task QueuedDiscoveryChecksOwnershipAfterAcquiringLifecycleLock()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var obsolete = controller.Snapshot;
        first.GainGate = new(TaskCreationOptions.RunContinuationsAsynchronously); first.GainEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var gain = controller.SetMutedAsync(true); await first.GainEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var start = controller.StartAsync(Pod(), Settings());
            var refresh = controller.RefreshReceiverAsync(Pod() with { Name = "Stale", Address = "127.0.0.2" }, obsolete, new Dictionary<string, string>());
            first.GainGate.TrySetResult(); await Task.WhenAll(gain, start, refresh);
            await Until(() => second.Started.Task.IsCompleted && controller.Snapshot.State == PlaybackState.Streaming);
            Assert.Equal("a", controller.Snapshot.Receiver!.Name); Assert.Equal("127.0.0.1", controller.Snapshot.Receiver.Address);
            Assert.Equal(2, factory.OpenCount); Assert.False(second.Disposed);
        }
        finally { first.GainGate.TrySetResult(); }
    }
    [Fact]
    public async Task PassiveSoftwareIdentityRefreshRetainsLiveMemberVolumeRouting()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection); var (receiver, settings) = SoftwareGroup();
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        connection.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 28, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?.ContainsKey("a") == true);
        var next = receiver with { Members = [receiver.Members[1], receiver.Members[0] with { Id = "new-a" }] };
        await controller.RefreshReceiverAsync(next, controller.Snapshot, new Dictionary<string, string> { ["a"] = "new-a" });
        Assert.Equal(28, controller.Snapshot.MemberVolumes!["new-a"].Actual); Assert.False(controller.Snapshot.MemberVolumes.ContainsKey("a"));
        var edit = controller.SetDeviceVolumeAsync("new-a", 55); await Until(() => connection.Commands.Any(c => c.Command == "set_device_volume"));
        Assert.Equal("127.0.0.1", connection.Commands.Single(c => c.Command == "set_device_volume").Parameters.Text("host"));
        Assert.Equal(1, factory.OpenCount); await controller.StopAsync(); await edit;
    }
    [Fact]
    public async Task PassiveIdentityMigrationPreservesEffectiveSettingsAndLiveUpdatesStayHot()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true; settings.Options("old").StandbySeconds = 5; settings.Options("old").LatencyMode = "realtime";
        await controller.StartAsync(Pod("old"), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var count = connection.Commands.Count(c => c.Command == "set_standby");
        var migrations = new Dictionary<string, string> { ["old"] = "new" };
        connection.EmitData(new { @event = "negotiated", receiver_id = "old", host = "127.0.0.1", port = 7000, codec = "alac", timing = "ptp", clock_role = "follow" });
        connection.EmitData(new { @event = "capture_metrics", metrics = new { input_rate = 48000 } });
        await Until(() => controller.Snapshot.Diagnostics?.Negotiated?.Count == 1);
        await controller.RefreshReceiverAsync(Pod("new"), controller.Snapshot, migrations);
        Assert.Equal("new", Assert.Single(controller.Snapshot.Diagnostics!.Negotiated!).Text("receiver_id"));
        ReceiverCatalog.ApplyMigrations(settings, migrations); settings.MasterVolume = 25;
        await controller.UpdateSettingsAsync(settings); await controller.UpdateSettingsAsync(settings);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(120, controller.Snapshot.TargetLatency);
        Assert.Equal(count, connection.Commands.Count(c => c.Command == "set_standby"));
        Assert.Equal(.25, connection.Commands.Last(c => c.Command == "set_gain").Parameters.Number("gain"));
        settings.Options("new").StandbySeconds = 20; await controller.UpdateSettingsAsync(settings);
        Assert.Equal(count + 1, connection.Commands.Count(c => c.Command == "set_standby")); Assert.Equal(1, factory.OpenCount);
    }
    [Fact]
    public async Task IgnoredNativeMemberProfilesCannotRebuildTheGroup()
    {
        var connection = new FakeConnection(); var factory = new FakeFactory(connection);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var pair = Pod("stereo:pair") with { Members = [Pod("left"), Pod("right") with { Address = "127.0.0.2" }] };
        var settings = Settings(); settings.Options(pair.Id).LatencyMode = "realtime";
        await controller.StartAsync(pair, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.Options("left").LatencyMode = "buffered"; settings.Options("right").Codec = "pcm"; settings.Options("left").Timing = "ntp";
        await controller.UpdateSettingsAsync(settings);
        Assert.Equal(1, factory.OpenCount); Assert.False(connection.Disposed);
        Assert.All(connection.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers").EnumerateArray(), peer => Assert.Equal(120, peer.GetProperty("profile").Number("latency_ms")));
    }
    [Theory]
    [InlineData("capture")]
    [InlineData("latency")]
    [InlineData("sample_rate")]
    [InlineData("codec")]
    [InlineData("transport")]
    [InlineData("timing")]
    public async Task ActualSettingsChangeRebuildsOnceWithLatestDiscoveredAddress(string field)
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); var logs = new ConcurrentQueue<string>();
        await using var controller = new SessionController(factory, new FakeAudio(), logs.Enqueue, Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        await controller.RefreshReceiverAsync(Pod() with { Address = "127.0.0.2" }, controller.Snapshot, new Dictionary<string, string>());
        switch (field)
        {
            case "capture": settings.CaptureMode = "endpoint"; settings.CaptureEndpoint = "endpoint"; break;
            case "latency": settings.LatencyMode = "buffered"; break;
            case "sample_rate": settings.Options("a").SampleRate = "48000"; break;
            case "codec": settings.Options("a").Codec = "pcm"; break;
            case "transport": settings.Options("a").Transport = "realtime"; break;
            case "timing": settings.Options("a").Timing = "ntp"; break;
        }
        await controller.UpdateSettingsAsync(settings); await Until(() => second.Started.Task.IsCompleted && controller.Snapshot.State == PlaybackState.Streaming);
        await controller.UpdateSettingsAsync(settings);
        Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed); Assert.False(second.Disposed);
        Assert.Equal("127.0.0.2", second.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers")[0].Text("host"));
        Assert.Contains(logs, message => message.Contains("reason=settings_change") && message.Contains("changed_fields="));
    }
    [Fact]
    public async Task StartIsNonblockingAndStopCancelsPendingConnection()
    {
        var process = new FakeConnection(false); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()).WaitAsync(TimeSpan.FromSeconds(1)); await process.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(PlaybackState.Connecting, controller.Snapshot.State); await controller.StopAsync(); Assert.True(process.Disposed); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }
    [Fact]
    public async Task GainPrecedesAudioAndIgnoresLegacyReceiverVolume()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.Options("a").Volume = 25; await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var command = process.Commands.Single(c => c.Command == "start"); Assert.Equal(.1, command.Parameters.Number("gain")); Assert.Equal(200, command.Parameters.Number("latency_ms"));
        Assert.Equal(44100, command.Parameters.Number("sample_rate"));
        Assert.Equal(JsonValueKind.Array, command.Parameters.GetProperty("peers")[0].GetProperty("codecs").ValueKind);
    }
    [Fact]
    public async Task ChangingStreamRateRestartsSessionWithNewRate()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.Options("a").SampleRate = "48000";
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(48000, first.Commands.Single(c => c.Command == "start").Parameters.Number("sample_rate"));
        Assert.Equal(48000, controller.Snapshot.StreamRate);
        settings.Options("a").SampleRate = "44100"; await controller.UpdateSettingsAsync(settings); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, factory.OpenCount); Assert.Equal(44100, second.Commands.Single(c => c.Command == "start").Parameters.Number("sample_rate"));
    }
    [Fact]
    public async Task AutomaticRateUsesReceiverNegotiationBeforeStartingCapture()
    {
        var connection = new FakeConnection { ReceiverCapabilityResponse = new { @event = "receiver_capabilities", members = new[] { new { receiver_id = "a", host = "127.0.0.1", port = 7000, known = true, profiles = new[] { new { codec = "auto", transport = "auto", timing = "auto", sample_rate = 48000, selected_codec = "alac", selected_transport = "realtime", selected_timing = "ptp" } } } } } };
        await using var controller = new SessionController(new FakeFactory(connection), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var commands = connection.Commands.ToArray();
        Assert.True(Array.FindIndex(commands, c => c.Command == "get_receiver_capabilities") < Array.FindIndex(commands, c => c.Command == "start"));
        Assert.Equal(48000, commands.Single(c => c.Command == "start").Parameters.Number("sample_rate")); Assert.Equal(48000, controller.Snapshot.StreamRate);
        Assert.Equal("a", commands.Single(c => c.Command == "start").Parameters.GetProperty("peers")[0].Text("receiver_id"));
    }
    [Fact]
    public async Task SwitchingWaitsForPreviousCleanupAndRejectsStaleEvents()
    {
        var first = new FakeConnection(); var second = new FakeConnection(false); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        await controller.StartAsync(Pod("b"), Settings()); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(first.Disposed);
        second.Emit("error", session: first.Session, message: "stale"); second.Emit("streaming"); await Until(() => controller.Snapshot.State == PlaybackState.Streaming); Assert.Equal("b", controller.Snapshot.Receiver!.Id);
    }
    [Fact]
    public async Task SwitchDuringFailureWaitsForDispose()
    {
        var first = new FakeConnection { BlockDispose = new(TaskCreationOptions.RunContinuationsAsynchronously) }; var second = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        first.Emit("error", message: "peer disconnected"); await first.Disposing.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var change = controller.StartAsync(Pod("b"), Settings()); Assert.False(second.Started.Task.IsCompleted); first.BlockDispose.SetResult(); await change; await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1)); Assert.True(first.Disposed);
    }
    [Fact]
    public async Task ReconnectCleansOldProcessAndUsesFreshSession()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(first, second), audio, timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true; settings.MaxReconnectAttempts = 1;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming); first.Emit("error", message: "network lost");
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.True(first.Disposed); Assert.NotEqual(first.Session, second.Session);
    }
    [Fact]
    public async Task AuthenticationFailureDoesNotReconnect()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true; await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.Emit("error", message: "authentication signature verification failed"); await Until(() => process.Disposed && controller.Snapshot.State == PlaybackState.Error); Assert.Equal(1, factory.OpenCount); Assert.Equal(PlaybackState.Error, controller.Snapshot.State);
    }
    [Fact]
    public async Task ConnectionTimeoutBecomesActionableError()
    {
        var process = new FakeConnection(false); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast with { Connect = TimeSpan.FromMilliseconds(60) });
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Error); Assert.Contains("timed out", controller.Snapshot.Message); await Until(() => process.Disposed);
    }
    [Fact]
    public async Task ApplyingEndpointAndLatencyRestartsOnceVolumeDoesNotRestart()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.CaptureMode = "endpoint"; settings.CaptureEndpoint = "device-id"; settings.LatencyMode = "buffered"; await controller.UpdateSettingsAsync(settings); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, factory.OpenCount); Assert.Equal("device-id", second.Commands.Single(c => c.Command == "start").Parameters.Text("capture_endpoint"));
        settings.MasterVolume = 20; await controller.UpdateSettingsAsync(settings); Assert.Equal(2, factory.OpenCount); Assert.Contains(second.Commands, c => c.Command == "set_gain" && c.Parameters.Number("gain") == .2);
    }
    [Fact]
    public async Task InactiveApplicationSelectionDoesNotRestartEndpointCapture()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.CaptureMode = "endpoint"; settings.CaptureEndpoint = "device-id";
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.CaptureApplicationPath = @"C:\Music.exe";
        settings.CaptureApplicationInstance = new(settings.CaptureApplicationPath, "Music", 41, 100);
        await controller.UpdateSettingsAsync(settings);
        Assert.Equal(1, factory.OpenCount); Assert.False(process.Disposed);
    }
    [Fact]
    public async Task StopAndFailedSessionRestoreMute()
    {
        var process = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(process), audio, timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => audio.Muted); process.Emit("error", message: "peer closed"); await Until(() => process.Disposed && controller.Snapshot.State == PlaybackState.Error); Assert.False(audio.Muted);
    }
    [Fact]
    public async Task TurningOffMuteWhileStreamingRestoresImmediately()
    {
        var process = new FakeConnection(); var audio = new FakeAudio(); await using var controller = new SessionController(new FakeFactory(process), audio, timing: Fast);
        var settings = Settings(); await controller.StartAsync(Pod(), settings); await Until(() => audio.Muted); settings.MuteWhileStreaming = false; await controller.UpdateSettingsAsync(settings); Assert.False(audio.Muted);
    }
    [Fact]
    public async Task SettingsAppliedDuringStandbyCleanupCannotMuteLocalOutputAgain()
    {
        var process = new FakeConnection(); var audio = new FakeAudio();
        await using var controller = new SessionController(new FakeFactory(process), audio, timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var calls = audio.MuteCalls;
        audio.RestoreEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        audio.RestoreGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            process.EmitData(new { @event = "standby", reason = "silence" });
            await audio.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            // Cleanup has changed the audio state but has not yet published Standby.
            Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
            settings.MasterVolume = 25;
            var edit = controller.UpdateSettingsAsync(settings);
            Assert.Equal(calls, audio.MuteCalls);
            audio.RestoreGate.TrySetResult(); await edit;
            await Until(() => controller.Snapshot.State == PlaybackState.Standby);
            Assert.False(audio.Muted); Assert.Equal(calls, audio.MuteCalls);
        }
        finally { audio.RestoreGate.TrySetResult(); }
    }
    [Fact]
    public async Task StandbyEditsApplyToResumedSessionAndFailureRestoresLocalMute()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var audio = new FakeAudio();
        await using var controller = new SessionController(factory, audio, timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true;
        await controller.StartAsync(Pod(), settings); await Until(() => audio.Muted);
        process.EmitData(new { @event = "standby", reason = "silence" });
        await Until(() => controller.Snapshot.State == PlaybackState.Standby);
        Assert.False(audio.Muted);
        settings.MasterVolume = 0; settings.Equalizer = Sound(4); settings.StandbySilenceSeconds = 20;
        await controller.UpdateSettingsAsync(settings);
        Assert.Equal(1, factory.OpenCount); Assert.False(audio.Muted);
        Assert.Equal(0, process.Commands.Last(c => c.Command == "set_gain").Parameters.Number("gain"));
        Assert.Equal(4, EqGain(process.Commands.Last(c => c.Command == "set_equalizer")));
        Assert.Equal(20, process.Commands.Last(c => c.Command == "set_standby").Parameters.Number("silence_seconds"));
        process.Emit("resuming"); process.Emit("streaming");
        await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.True(audio.Muted); Assert.Equal(1, factory.OpenCount);
        process.Emit("error", message: "resume connection failed");
        await Until(() => process.Disposed && controller.Snapshot.State == PlaybackState.Error);
        Assert.False(audio.Muted);
    }
    [Fact]
    public async Task EngineStandbyReleasesMuteAndMetricsCannotOverrideState()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.StandbyEnabled = true; settings.StandbySilenceSeconds = 5;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.EmitData(new { @event = "standby", reason = "silence" });
        process.Emit("capture_metrics", metrics: new { last_audio_qpc_ns = 1, capture_to_send_p95_ms = (double?)null, underrun_packets = 2 });
        await Until(() => controller.Snapshot.State == PlaybackState.Standby); Assert.Equal(1, factory.OpenCount); Assert.Null(controller.Snapshot.Metrics!.CaptureToSendP95);
    }
    [Fact]
    public async Task ApplicationCaptureNeverMutesEndpointAndLiveStandbyEditsDoNotRestart()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var audio = new FakeAudio();
        await using var controller = new SessionController(factory, audio, timing: Fast);
        var settings = Settings(); settings.CaptureMode = "application"; settings.CaptureApplicationPath = @"C:\Player.exe";
        settings.CaptureApplicationInstance = new(settings.CaptureApplicationPath, "Player", 42, 123);
        settings.StandbyEnabled = true; settings.Options("a").StandbySeconds = 7;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.False(audio.Muted);
        var start = process.Commands.Single(c => c.Command == "start").Parameters;
        Assert.Equal("application", start.Text("source")); Assert.Equal(42, start.Integer("capture_process_id"));
        Assert.Equal(123, start.Integer("capture_process_started_at")); Assert.Equal(JsonValueKind.Null, start.GetProperty("capture_endpoint").ValueKind);
        Assert.Equal(7, start.GetProperty("standby").Number("silence_seconds"));
        process.EmitData(new { @event = "standby", reason = "silence" }); await Until(() => controller.Snapshot.State == PlaybackState.Standby);
        settings.Options("a").StandbySeconds = 20; await controller.UpdateSettingsAsync(settings);
        Assert.Equal(20, process.Commands.Last(c => c.Command == "set_standby").Parameters.Number("silence_seconds"));
        Assert.Equal(1, factory.OpenCount); Assert.False(audio.Muted);
        process.Emit("resuming"); await Until(() => controller.Snapshot.State == PlaybackState.Connecting);
        process.Emit("streaming"); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.False(audio.Muted); Assert.Equal(1, factory.OpenCount);
        await controller.StopAsync(); process.Emit("streaming"); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State);
    }
    [Fact]
    public async Task ApplicationCanWaitBeforeFirstConnectionWithoutFallbackAndStopCancelsWaiting()
    {
        var process = new FakeConnection(false); var factory = new FakeFactory(process); var audio = new FakeAudio();
        await using var controller = new SessionController(factory, audio, timing: Fast);
        var settings = Settings(); settings.CaptureMode = "application"; settings.CaptureApplicationPath = @"C:\Missing.exe";
        await controller.StartAsync(Pod(), settings); await process.Started.Task;
        process.EmitData(new { @event = "standby", reason = "application_unavailable" });
        await Until(() => controller.Snapshot.State == PlaybackState.Standby);
        process.Emit("capture_metrics", metrics: new { last_audio_qpc_ns = long.MaxValue });
        await Until(() => controller.Snapshot.Metrics is not null);
        Assert.Equal(PlaybackState.Standby, controller.Snapshot.State); Assert.False(audio.Muted);
        await controller.StopAsync(); Assert.True(process.Disposed); Assert.Equal(1, factory.OpenCount);
    }
    [Fact]
    public async Task StandbyEditsDuringHelloWaitForStartAndKeepLatestPolicy()
    {
        var process = new FakeConnection { RespondHello = false }; var settings = Settings();
        await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), settings);
        await Until(() => process.Commands.Any(c => c.Command == "hello"));
        settings.StandbyEnabled = true; settings.StandbySilenceSeconds = 20;
        await controller.UpdateSettingsAsync(settings);
        Assert.DoesNotContain(process.Commands, c => c.Command == "set_standby");
        process.EmitHello(); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var commands = process.Commands.ToArray();
        Assert.True(Array.FindIndex(commands, c => c.Command == "start") < Array.FindIndex(commands, c => c.Command == "set_standby"));
        var policy = commands.Last(c => c.Command == "set_standby").Parameters;
        Assert.True(policy.Boolean("enabled")); Assert.Equal(20, policy.Number("silence_seconds"));
    }
    [Fact]
    public async Task IncompleteStereoGroupNeverStartsEngine()
    {
        var factory = new FakeFactory(); await using var controller = new SessionController(factory, new FakeAudio());
        await Assert.ThrowsAsync<InvalidOperationException>(() => controller.StartAsync(Pod() with { StereoId = "pair", Members = [Pod()] }, Settings())); Assert.Equal(0, factory.OpenCount);
    }
    [Fact]
    public async Task PairCancellationDoesNotStartPlayback()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.PairAsync(Pod(), Settings(), (_, _) => Task.FromResult<string?>(null)); await Until(() => process.Disposed);
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.DoesNotContain(process.Commands, c => c.Command == "start");
    }
    [Fact]
    public async Task PairBothMembersThenStartOneStereoSession()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var stream = new FakeConnection(); var factory = new FakeFactory(first, second, stream); await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var group = Pod() with { StereoId = "pair", Members = [Pod(), Pod("b") with { Address = "127.0.0.2" }] };
        await controller.PairAsync(group, Settings(), (_, _) => Task.FromResult<string?>("1234")); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(3, factory.OpenCount); Assert.True(first.Disposed); Assert.True(second.Disposed); Assert.Equal(2, stream.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers").GetArrayLength());
    }
    [Fact]
    public async Task RecoverableWarningsAndTransportMetricsDoNotRestartOrUnmute()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var audio = new FakeAudio();
        await using var controller = new SessionController(factory, audio, timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.Emit("capture_metrics", metrics: new { underrun_packets = 2, input_rate = 48000 });
        process.EmitData(new { @event = "warning", code = "feedback_delayed", host = "a", channel = "feedback", message = "slow", recovered = false });
        process.EmitData(new { @event = "warning", code = "media_send_delayed", host = "b", channel = "media", message = "busy", recovered = false });
        process.EmitData(new { @event = "transport_metrics", session_uptime_ms = 1234, sender_late_recoveries = 3, members = new object[] { new { host = "a", media = new { retransmits_sent = 4 }, health = new { feedback_delayed = true, feedback_rtt_ms = 42 } }, new { host = "b" } } });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null);
        Assert.Equal(1, factory.OpenCount); Assert.True(audio.Muted); Assert.Equal(PlaybackState.Streaming, controller.Snapshot.State);
        Assert.Equal(2, controller.Snapshot.Metrics!.Underruns); Assert.Null(controller.Snapshot.Metrics.DroppedFrames);
        Assert.Equal(2, controller.Snapshot.Diagnostics!.Warnings!.Count);
        var transport = controller.Snapshot.Diagnostics.Transport!; Assert.Equal(3, transport.SenderLateRecoveries); Assert.Equal(4, transport.Members[0].RetransmitsSent); Assert.Null(transport.Members[1].RetransmitsSent);
        process.EmitData(new { @event = "warning", code = "feedback_recovered", host = "a", channel = "feedback", message = "ok", recovered = true });
        await Until(() => controller.Snapshot.Diagnostics?.Warnings?.Count == 1);
        Assert.Equal("b", controller.Snapshot.Diagnostics!.Warnings![0].Host); Assert.True(audio.Muted);
    }
    [Fact]
    public async Task ReconnectRetainsFailureAndMetricsUntilANewPlayback()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var third = new FakeConnection(); var factory = new FakeFactory(first, second, third);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        first.Emit("capture_metrics", metrics: new { underrun_packets = 7, dropped_frames = 11 });
        first.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 2, members = new[] { new { host = "a", media = new { retransmits_sent = 5 } } } });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null);
        first.EmitData(new { @event = "error", code = "peer_closed", host = "a", channel = "events", message = "connection ended", retryable = true });
        await Until(() => factory.OpenCount == 2 && controller.Snapshot.State == PlaybackState.Streaming);
        var diagnostics = controller.Snapshot.Diagnostics!;
        Assert.Equal(1, diagnostics.ReconnectCount); Assert.Equal("peer_closed", diagnostics.LastFault!.Code);
        Assert.Equal(7, diagnostics.CaptureBeforeFault!.Underruns); Assert.Equal(2, diagnostics.TransportBeforeFault!.SenderLateRecoveries);
        Assert.Null(controller.Snapshot.Metrics); Assert.Null(diagnostics.Transport);
        second.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 0, members = Array.Empty<object>() });
        await Until(() => controller.Snapshot.Diagnostics?.Transport is not null); Assert.Equal(2, controller.Snapshot.Diagnostics!.TransportBeforeFault!.SenderLateRecoveries);
        await controller.StartAsync(Pod("new"), settings); await Until(() => factory.OpenCount == 3 && controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal(0, controller.Snapshot.Diagnostics!.ReconnectCount); Assert.Null(controller.Snapshot.Diagnostics.LastFault);
    }
    [Fact]
    public async Task StructuredNonretryableFaultDoesNotDependOnMessageText()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process);
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        var settings = Settings(); settings.ForceReconnect = true;
        await controller.StartAsync(Pod(), settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.EmitData(new { @event = "error", code = "protocol_error", host = "a", channel = "feedback", message = "invalid reply", retryable = false });
        await Until(() => process.Disposed && controller.Snapshot.State == PlaybackState.Error);
        Assert.Equal(1, factory.OpenCount); Assert.Equal("feedback", controller.Snapshot.Diagnostics!.LastFault!.Channel);
    }
    [Fact]
    public async Task StaleTransportAndWarningsCannotModifyNewPlayback()
    {
        var first = new FakeConnection(); var second = new FakeConnection();
        await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        await controller.StartAsync(Pod("new"), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        second.EmitData(new { @event = "warning", host = "old", channel = "events", message = "stale" }, first.Session);
        second.EmitData(new { @event = "transport_metrics", sender_late_recoveries = 99 }, first.Session);
        second.Emit("capture_metrics", metrics: new { underrun_packets = 1 });
        await Until(() => controller.Snapshot.Metrics?.Underruns == 1);
        Assert.Null(controller.Snapshot.Diagnostics!.Transport); Assert.Empty(controller.Snapshot.Diagnostics.Warnings ?? []);
    }
    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!predicate()) await Task.Delay(5, timeout.Token);
    }
    [Fact]
    public async Task DeviceVolumeReadsWithoutWritingAndDebouncesLatestTarget()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: SessionTiming.Default);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Null(controller.Snapshot.DeviceVolume!.Actual);
        await controller.SetDeviceVolumeAsync("a", 90);
        Assert.DoesNotContain(process.Commands, c => c.Command == "set_device_volume");
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 28, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.Actual == 28);
        Assert.DoesNotContain(process.Commands, c => c.Command == "set_device_volume");
        var first = controller.SetDeviceVolumeAsync("a", 40);
        var last = controller.SetDeviceVolumeAsync("a", 55);
        await Until(() => process.Commands.Any(c => c.Command == "set_device_volume"));
        var command = Assert.Single(process.Commands, c => c.Command == "set_device_volume");
        Assert.Equal(55, command.Parameters.Number("volume"));
        var sequence = command.Parameters.Integer("sequence");
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 28, sequence, status = "pending", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.ReadAfterWrite == true);
        Assert.Equal(55, controller.Snapshot.DeviceVolume!.Display);
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 55, sequence, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.Target is null);
        Assert.Equal(55, controller.Snapshot.DeviceVolume!.Actual);
        await controller.StopAsync(); await Task.WhenAll(first, last);
        Assert.Null(controller.Snapshot.DeviceVolume!.Actual);
    }
    [Fact]
    public async Task StereoLeaderIsFirstAndOnlyLeaderReadoutIsAccepted()
    {
        var process = new FakeConnection(); await using var controller = new SessionController(new FakeFactory(process), new FakeAudio(), timing: Fast);
        var leader = new Receiver("leader", "leader", "127.0.0.2") { IsLeader = true };
        var group = Pod("stereo:pair") with { Members = [Pod(), leader] };
        await controller.StartAsync(group, Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        Assert.Equal("127.0.0.2", process.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers")[0].Text("host"));
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 90, sequence = 0, status = "confirmed", available = true });
        process.EmitData(new { @event = "device_volume", host = "127.0.0.2", volume = 30, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.Actual == 30);
    }
    [Fact]
    public async Task ReconnectDiscardsOldVolumeAndPendingEdits()
    {
        var first = new FakeConnection(); var second = new FakeConnection();
        await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(Pod(), Settings()); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        first.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 30, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.Actual == 30);
        var edit = controller.SetDeviceVolumeAsync("a", 80);
        await controller.StartAsync(Pod(), Settings()); await Until(() => second.Session.Length > 0 && controller.Snapshot.State == PlaybackState.Streaming);
        await edit; Assert.Null(controller.Snapshot.DeviceVolume!.Actual);
        second.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 80, sequence = 0, status = "confirmed", available = true }, first.Session);
        second.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 32, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.DeviceVolume?.Actual == 32);
        Assert.DoesNotContain(second.Commands, c => c.Command == "set_device_volume");
    }
    private static (Receiver Receiver, AppSettings Settings) SoftwareGroup()
    {
        var settings = Settings(); settings.SoftwareGroups.Add(new() { Id = "software:test", LeftId = "a", RightId = "b", LeftOffsetMs = -80, RightOffsetMs = 25 });
        settings.Options("a").Codec = "alac"; settings.Options("a").Transport = "buffered"; settings.Options("b").Codec = "pcm"; settings.Options("b").Timing = "ntp";
        return (SoftwareStereo.Resolve([Pod("a"), Pod("b") with { Address = "127.0.0.2" }], settings).Single(), settings);
    }
    [Fact]
    public async Task ApplicationStandbyKeepsSoftwareGroupProfilesAndMemberVolumeRouting()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var audio = new FakeAudio();
        var (receiver, settings) = SoftwareGroup();
        settings.CaptureMode = "application"; settings.CaptureApplicationPath = @"C:\Player.exe";
        settings.SoftwareGroups[0].SampleRate = "48000"; settings.StandbyEnabled = true;
        await using var controller = new SessionController(factory, audio, timing: Fast);
        await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var start = process.Commands.Single(c => c.Command == "start").Parameters;
        Assert.Equal("application", start.Text("source")); Assert.Equal("software_stereo", start.Text("group_mode"));
        Assert.Equal(48000, start.Number("sample_rate")); Assert.Equal(2, start.GetProperty("peers").GetArrayLength());
        Assert.Equal("buffered", start.GetProperty("peers")[0].GetProperty("profile").Text("transport"));
        Assert.Equal("right", start.GetProperty("peers")[1].GetProperty("profile").Text("channel"));
        process.EmitData(new { @event = "device_volume", host = "127.0.0.2", volume = 30, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?.ContainsKey("b") == true);
        process.EmitData(new { @event = "standby", reason = "silence" }); await Until(() => controller.Snapshot.State == PlaybackState.Standby);
        var edit = controller.SetDeviceVolumeAsync("b", 45);
        await Until(() => process.Commands.Any(c => c.Command == "set_device_volume"));
        var volume = process.Commands.Single(c => c.Command == "set_device_volume").Parameters;
        Assert.Equal("127.0.0.2", volume.Text("host"));
        settings.MasterVolume = 0; settings.StandbySilenceSeconds = 20;
        await controller.UpdateSettingsAsync(settings);
        process.Emit("resuming"); process.Emit("streaming"); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.EmitData(new { @event = "device_volume", host = "127.0.0.2", volume = 45, sequence = volume.Integer("sequence"), status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?["b"].Actual == 45);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(0, audio.MuteCalls);
        await controller.StopAsync(); await edit;
    }
    [Fact]
    public async Task SoftwareGroupSerializesMemberProfilesAndReconnectsTogetherWithoutGlobalForceReconnect()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); var (receiver, settings) = SoftwareGroup();
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast); await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        var start = first.Commands.Single(c => c.Command == "start").Parameters; Assert.Equal("software_stereo", start.Text("group_mode")); Assert.Equal(2, start.GetProperty("peers").GetArrayLength());
        Assert.Equal("left", start.GetProperty("peers")[0].GetProperty("profile").Text("channel")); Assert.Equal("buffered", start.GetProperty("peers")[0].GetProperty("profile").Text("transport"));
        Assert.Equal("right", start.GetProperty("peers")[1].GetProperty("profile").Text("channel")); Assert.Equal("pcm", start.GetProperty("peers")[1].GetProperty("profile").Text("codec"));
        first.EmitData(new { @event = "error", host = "127.0.0.2", channel = "events", code = "peer_closed", retryable = true, message = "member disconnected" }); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.Disposed); Assert.Equal(2, second.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers").GetArrayLength()); Assert.DoesNotContain(first.Commands, c => c.Command == "set_device_volume");
    }
    [Fact]
    public async Task SoftwareGroupMemberVolumeTargetsOnlyItsHostAndGroupRemovalStopsPlayback()
    {
        var connection = new FakeConnection(); var (receiver, settings) = SoftwareGroup(); await using var controller = new SessionController(new FakeFactory(connection), new FakeAudio(), timing: Fast);
        await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        connection.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 28, sequence = 0, status = "confirmed", available = true });
        connection.EmitData(new { @event = "device_volume", host = "127.0.0.2", volume = 42, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?.Count == 2); Assert.False(controller.Snapshot.DeviceVolume!.Available);
        var update = controller.SetDeviceVolumeAsync("b", 55); await Until(() => connection.Commands.Any(c => c.Command == "set_device_volume"));
        var command = connection.Commands.Single(c => c.Command == "set_device_volume"); Assert.Equal("127.0.0.2", command.Parameters.Text("host")); Assert.Equal(55, command.Parameters.Number("volume"));
        connection.EmitData(new { @event = "device_volume", host = "127.0.0.2", volume = 55, sequence = command.Parameters.Integer("sequence"), status = "confirmed", available = true });
        await update;
        Assert.Equal("confirmed", controller.Snapshot.MemberVolumes!["b"].Status);
        Assert.Equal(28, controller.Snapshot.MemberVolumes!["a"].Actual);
        settings.SoftwareGroups.Clear(); await controller.UpdateSettingsAsync(settings); Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.True(connection.Disposed);
    }
    [Fact]
    public async Task SoftwareGroupSwapRebuildsBothMembersAndKeepsCommonRate()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var factory = new FakeFactory(first, second); var (receiver, settings) = SoftwareGroup();
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast); await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.SoftwareGroups[0].Swap(); settings.SoftwareGroups[0].SampleRate = "48000"; var next = SoftwareStereo.Resolve(receiver.Members, settings).Single();
        await controller.UpdateReceiverAsync(next, settings); await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(first.Disposed); Assert.Equal(2, factory.OpenCount); var start = second.Commands.Single(c => c.Command == "start").Parameters; Assert.Equal(48000, start.Number("sample_rate")); Assert.Equal("127.0.0.2", start.GetProperty("peers")[0].Text("host"));
        Assert.Equal("left", start.GetProperty("peers")[0].GetProperty("profile").Text("channel"));
    }
    [Fact]
    public async Task SettingsOnlySoftwareSwapUpdatesRoutingAndUnknownReplacementStops()
    {
        var first = new FakeConnection(); var second = new FakeConnection(); var (receiver, settings) = SoftwareGroup();
        await using var controller = new SessionController(new FakeFactory(first, second), new FakeAudio(), timing: Fast);
        await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        settings.SoftwareGroups[0].Swap(); await controller.UpdateSettingsAsync(settings);
        await second.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("127.0.0.2", second.Commands.Single(c => c.Command == "start").Parameters.GetProperty("peers")[0].Text("host"));
        settings.SoftwareGroups[0].RightId = "undiscovered"; await controller.UpdateSettingsAsync(settings);
        Assert.Equal(PlaybackState.Error, controller.Snapshot.State); Assert.True(second.Disposed);
    }
    [Fact]
    public async Task SoftwareIdentityMigrationKeepsHostRoutingAndVolumeWithoutRestart()
    {
        var process = new FakeConnection(); var factory = new FakeFactory(process); var (receiver, settings) = SoftwareGroup();
        await using var controller = new SessionController(factory, new FakeAudio(), timing: Fast);
        await controller.StartAsync(receiver, settings); await Until(() => controller.Snapshot.State == PlaybackState.Streaming);
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 28, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?.ContainsKey("a") == true);
        ReceiverCatalog.ApplyMigrations(settings, new Dictionary<string, string> { ["a"] = "new-a" });
        var next = receiver with { Members = [receiver.Members[0] with { Id = "new-a" }, receiver.Members[1]] };
        await controller.UpdateReceiverAsync(next, settings);
        Assert.Equal(1, factory.OpenCount); Assert.Equal(28, controller.Snapshot.MemberVolumes!["new-a"].Actual); Assert.False(controller.Snapshot.MemberVolumes.ContainsKey("a"));
        process.EmitData(new { @event = "device_volume", host = "127.0.0.1", volume = 32, sequence = 0, status = "confirmed", available = true });
        await Until(() => controller.Snapshot.MemberVolumes?["new-a"].Actual == 32);
        var edit = controller.SetDeviceVolumeAsync("new-a", 55); await Until(() => process.Commands.Any(c => c.Command == "set_device_volume"));
        Assert.Equal("127.0.0.1", process.Commands.Single(c => c.Command == "set_device_volume").Parameters.Text("host"));
        await controller.StopAsync(); await edit;
    }
    private sealed class FakeFactory(params FakeConnection[] processes) : IEngineFactory
    {
        private readonly ConcurrentQueue<FakeConnection> _processes = new(processes);
        public int OpenCount;
        public IEngineConnection Open() { Interlocked.Increment(ref OpenCount); return _processes.TryDequeue(out var p) ? p : throw new IOException("unexpected engine spawn"); }
    }
    private sealed record EngineCommand(string Session, string CommandName, JsonElement Parameters) { public string Command => CommandName; }
    private sealed class FakeConnection(bool autoReady = true) : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        public ConcurrentQueue<EngineCommand> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Disposing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? BlockDispose { get; init; }
        public string Session { get; private set; } = "";
        public bool Disposed { get; private set; }
        public bool RespondHello { get; init; } = true;
        public object? ReceiverCapabilityResponse { get; init; }
        public TaskCompletionSource? GainEntered, GainGate;
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            Commands.Enqueue(new(session, command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            switch (command)
            {
                case "hello": Session = session; if (RespondHello) EmitHello(); break;
                case "get_receiver_capabilities": EmitData(ReceiverCapabilityResponse!); break;
                case "start": Session = session; Started.TrySetResult(); if (autoReady) Emit("streaming"); break;
                case "pair": Session = session; Emit("pin_required"); break;
                case "pair_pin": Emit("paired"); break;
                case "stop": _events.Writer.TryComplete(); break;
                case "set_gain": GainEntered?.TrySetResult(); if (GainGate is not null) return GainGate.Task.WaitAsync(cancellation); break;
            }
            return Task.CompletedTask;
        }
        public void EmitHello() => EmitData(new { @event = "hello", capabilities = new[] { "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "member_volume", "receiver_password", "standby_release", "application_capture" }.Concat(ReceiverCapabilityResponse is null ? [] : new[] { "receiver_capabilities" }).ToArray() });
        public void EmitData(object payload, string? session = null)
        {
            var data = JsonSerializer.SerializeToNode(payload)!.AsObject(); data["version"] = 1; data["session_id"] = session ?? Session;
            _events.Writer.TryWrite(JsonSerializer.SerializeToElement(data));
        }
        public void Emit(string kind, string? session = null, string? message = null, object? metrics = null) => _events.Writer.TryWrite(JsonSerializer.SerializeToElement(new { version = 1, session_id = session ?? Session, @event = kind, message, metrics }));
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation) { try { return await _events.Reader.ReadAsync(cancellation); } catch (ChannelClosedException) { return null; } }
        public async ValueTask DisposeAsync() { Disposing.TrySetResult(); if (BlockDispose is not null) await BlockDispose.Task; Disposed = true; _events.Writer.TryComplete(); }
    }
    private sealed class FakeAudio : IAudioService
    {
        public bool Muted;
        public int MuteCalls;
        public TaskCompletionSource? RestoreEntered, RestoreGate;
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("default");
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task MuteAsync(string? endpointId) { ++MuteCalls; Muted = true; return Task.CompletedTask; }
        public async Task RestoreAsync() { Muted = false; RestoreEntered?.TrySetResult(); if (RestoreGate is not null) await RestoreGate.Task; }
    }
}
