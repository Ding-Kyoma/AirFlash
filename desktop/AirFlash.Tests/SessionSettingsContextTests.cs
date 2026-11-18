using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public sealed class SessionSettingsContextTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(5);
    private static readonly SessionTiming Timing = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.Zero);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Receiver Legacy(string address = "192.0.2.10", int port = 7009)
        => new(ReceiverIdentity.Endpoint(address, port), "Synthetic legacy receiver", address, port);
    private static Receiver Canonical(Receiver receiver, string id = "canonical-receiver")
        => receiver with { Id = id, DeviceId = id };
    private static AppSettings Settings(Receiver receiver)
    {
        var settings = new AppSettings { ForceReconnect = false, MuteWhileStreaming = false, MasterVolume = 10 };
        settings.Options(receiver.Id).LatencyMode = "custom";
        settings.Options(receiver.Id).CustomBufferMs = 333;
        settings.Options(receiver.Id).SampleRate = "48000";
        settings.Options(receiver.Id).Codec = "pcm";
        return settings;
    }
    private static Dictionary<string, string> Migration(Receiver old, Receiver current) => new(StringComparer.Ordinal) { [old.Id] = current.Id };
    private static AppSettings Migrated(AppSettings settings, Receiver old, Receiver current)
    {
        var migrated = settings.Clone(); ReceiverCatalog.ApplyMigrations(migrated, Migration(old, current));
        return migrated;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalSettingsFlushPreservesOwnedTransportAndOptionsDuringStreamingOrOpenPin(bool pairing)
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection { RequestPin = pairing }; var playback = pairing ? new Connection() : first;
        var factory = pairing ? new Factory(first, playback) : new Factory(first);
        var pin = new PendingPin();
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        try
        {
            if (pairing)
            {
                await controller.PairAsync(receiver, settings, pin.RequestAsync);
                await pin.Entered.Task.WaitAsync(Limit);
            }
            else await StartAsync(controller, first, receiver, settings);
            var owner = controller.Capture();
            var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 37;
            var serialized = JsonSerializer.Serialize(incoming);
            var context = new SessionSettingsContext([canonical], Migration(receiver, canonical));

            Assert.True(await controller.TryUpdateSettingsAsync(incoming, owner, context));

            Assert.Equal(owner.Owner, controller.Capture().Owner);
            Assert.Equal(receiver, controller.Snapshot.Receiver);
            Assert.Equal(pairing ? PlaybackState.Pairing : PlaybackState.Streaming, controller.Snapshot.State);
            Assert.False(first.Disposed);
            Assert.DoesNotContain(first.Commands, command => command.Name == "stop");
            Assert.True(controller.UsesDeviceLatency(incoming.LatencyMode));
            if (pairing)
            {
                Assert.DoesNotContain(first.Commands, command => command.Name == "pair_pin");
                Assert.False(playback.Started.Task.IsCompleted);
                pin.Release.TrySetResult("1234");
                await playback.Started.Task.WaitAsync(Limit);
                await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
                Assert.Equal("1234", Assert.Single(first.Commands, command => command.Name == "pair_pin").Parameters.Text("pin"));
            }
            await PublishMetricsAsync(controller, playback);
            AssertStart(playback, receiver, 333, 48000, "pcm");
            Assert.Equal(.37, pairing ? Start(playback).Number("gain") : playback.Commands.Last(command => command.Name == "set_gain").Parameters.Number("gain"));
            Assert.Equal(333, controller.Snapshot.TargetLatency);
            Assert.Equal(48000, controller.Snapshot.StreamRate);
            Assert.Equal(pairing ? 2 : 1, factory.OpenCount);
            Assert.Equal(serialized, JsonSerializer.Serialize(incoming));
            Assert.False(incoming.Receivers.ContainsKey(receiver.Id));
            Assert.False(incoming.ReceiverAliases.ContainsKey(receiver.Id));
        }
        finally { pin.Release.TrySetResult(null); await controller.StopAsync(); }
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("address changed")]
    [InlineData("port changed")]
    [InlineData("ambiguous")]
    [InlineData("manual")]
    public async Task UnprovedCanonicalBindingCannotReplaceOrClearOwnedOverrides(string condition)
    {
        var receiver = Legacy() with { IsManual = condition == "manual" };
        var canonical = Canonical(receiver) with { IsManual = false };
        var settings = Settings(receiver);
        if (receiver.IsManual) settings.ManualReceivers.Add(new(receiver.Id, receiver.Name, receiver.Address, receiver.Port));
        var first = new Connection(); var factory = new Factory(first);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = settings.Clone(); incoming.Receivers.Remove(receiver.Id); incoming.MasterVolume = 37;
        incoming.Options(canonical.Id).LatencyMode = "custom"; incoming.Options(canonical.Id).CustomBufferMs = 555;
        incoming.Options(canonical.Id).SampleRate = "44100"; incoming.Options(canonical.Id).Codec = "alac";
        Receiver[] catalog = condition switch
        {
            "absent" => [],
            "address changed" => [canonical with { Address = "192.0.2.99" }],
            "port changed" => [canonical with { Port = 7010 }],
            "ambiguous" => [canonical, canonical with { Id = "another-canonical", DeviceId = "another-canonical" }],
            _ => [canonical]
        };
        var context = new SessionSettingsContext(catalog, Migration(receiver, canonical), [canonical.Id]);

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), context));
        await PublishMetricsAsync(controller, first);

        Assert.Equal(1, factory.OpenCount); Assert.False(first.Disposed);
        Assert.DoesNotContain(first.Commands, command => command.Name == "stop");
        Assert.Equal(receiver, controller.Snapshot.Receiver);
        Assert.Equal(333, controller.Snapshot.TargetLatency); Assert.Equal(48000, controller.Snapshot.StreamRate);
        Assert.Equal(.37, first.Commands.Last(command => command.Name == "set_gain").Parameters.Number("gain"));
        Assert.Equal(555, incoming.ReadOptions(canonical.Id).CustomBufferMs);
        Assert.False(incoming.Receivers.ContainsKey(receiver.Id));
    }

    [Fact]
    public async Task MatchingOfflineCatalogRetainsWorkingOwnedTransportWithoutMakingItOnline()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver) with { Online = false };
        var settings = Settings(receiver); var first = new Connection(); var factory = new Factory(first);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 37;

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([canonical], Migration(receiver, canonical))));
        await PublishMetricsAsync(controller, first);

        Assert.False(canonical.Online); Assert.Equal(receiver, controller.Snapshot.Receiver);
        Assert.Equal(1, factory.OpenCount); Assert.False(first.Disposed);
        Assert.Equal(333, controller.Snapshot.TargetLatency); Assert.Equal(48000, controller.Snapshot.StreamRate);
    }

    [Theory]
    [InlineData("raw clear")]
    [InlineData("canonical clear")]
    [InlineData("raw defaults")]
    [InlineData("canonical defaults")]
    public async Task ExplicitOverrideRemovalOrPresentDefaultsStillRestartWithInheritedOptions(string change)
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = Migrated(settings, receiver, canonical);
        incoming.Receivers.Clear(); incoming.MasterVolume = 37;
        var key = change.StartsWith("raw", StringComparison.Ordinal) ? receiver.Id : canonical.Id;
        if (change.EndsWith("defaults", StringComparison.Ordinal)) incoming.Options(key);
        string[] clear = change.EndsWith("clear", StringComparison.Ordinal) ? [key] : [];
        var context = new SessionSettingsContext([canonical], Migration(receiver, canonical), clear);

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), context));
        await second.Started.Task.WaitAsync(Limit);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);

        Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed);
        AssertStart(second, receiver, 200, 44100, "auto");
        Assert.Equal(.37, Start(second).Number("gain"));
        Assert.False(controller.UsesDeviceLatency("normal"));
        Assert.Equal(receiver.Address, controller.Snapshot.Receiver!.Address); Assert.Equal(receiver.Port, controller.Snapshot.Receiver.Port);
    }

    [Fact]
    public async Task ConfirmedCanonicalEditsAreAppliedAndContextDoesNotMutateSavedSettings()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = Migrated(settings, receiver, canonical);
        incoming.Options(canonical.Id).CustomBufferMs = 555; incoming.Options(canonical.Id).SampleRate = "44100"; incoming.Options(canonical.Id).Codec = "alac";
        var serialized = JsonSerializer.Serialize(incoming);

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([canonical], Migration(receiver, canonical))));
        await second.Started.Task.WaitAsync(Limit);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);

        AssertStart(second, receiver, 555, 44100, "alac");
        Assert.Equal(serialized, JsonSerializer.Serialize(incoming));
        Assert.False(incoming.Receivers.ContainsKey(receiver.Id));
    }

    [Fact]
    public async Task ContextFreezesCatalogMigrationsAndClearIntentBeforeAQueuedUpdate()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var catalog = new List<Receiver> { canonical }; var migrations = Migration(receiver, canonical); var clears = new List<string>();
        var context = new SessionSettingsContext(catalog, migrations, clears);
        var gate = audio.HoldNextRestore(); Task<bool>? holding = null; Task<bool>? update = null;
        try
        {
            holding = controller.TryUpdateSettingsAsync(settings, controller.Capture());
            await gate.Entered.Task.WaitAsync(Limit);
            var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 37;
            incoming.Options(canonical.Id).CustomBufferMs = 555;
            update = controller.TryUpdateSettingsAsync(incoming, controller.Capture(), context);
            Assert.False(update.IsCompleted);
            catalog.Clear(); migrations.Clear(); clears.Add(receiver.Id);
            gate.Release.TrySetResult();
            Assert.True(await holding.WaitAsync(Limit)); Assert.True(await update.WaitAsync(Limit));
            await second.Started.Task.WaitAsync(Limit);
            await PublishMetricsAsync(controller, second);
            Assert.Equal(2, factory.OpenCount); Assert.True(first.Disposed);
            AssertStart(second, receiver, 555, 48000, "pcm");
            Assert.Equal(555, controller.Snapshot.TargetLatency); Assert.Equal(48000, controller.Snapshot.StreamRate);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (holding is not null) await holding.WaitAsync(Limit);
            if (update is not null) await update.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task QueuedContextUpdateRechecksIntentBeforeApplyingProjection()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var factory = new Factory(first); var audio = new Audio();
        await using var controller = TestSessionController.Create(factory, audio, timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var captured = controller.Capture(); var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 0;
        var gate = audio.HoldNextRestore(); Task<bool>? holding = null; Task<bool>? queued = null;
        try
        {
            holding = controller.TryUpdateSettingsAsync(settings, captured);
            await gate.Entered.Task.WaitAsync(Limit);
            queued = controller.TryUpdateSettingsAsync(incoming, captured, new([canonical], Migration(receiver, canonical), [receiver.Id]));
            Assert.False(queued.IsCompleted);
            controller.BeginExplicitIntent(); gate.Release.TrySetResult();
            Assert.True(await holding.WaitAsync(Limit)); Assert.False(await queued.WaitAsync(Limit));
            await PublishMetricsAsync(controller, first);
            Assert.Equal(captured.Owner, controller.Capture().Owner);
            Assert.Equal(333, controller.Snapshot.TargetLatency); Assert.Equal(48000, controller.Snapshot.StreamRate);
            Assert.Equal(.1, first.Commands.Last(command => command.Name == "set_gain").Parameters.Number("gain"));
            Assert.Equal(1, factory.OpenCount);
        }
        finally
        {
            gate.Release.TrySetResult();
            if (holding is not null) await holding.WaitAsync(Limit);
            if (queued is not null) await queued.WaitAsync(Limit);
        }
    }

    [Fact]
    public async Task OldOwnerContextCannotAlterNewSameIdTransport()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var stale = controller.Capture(); var replacement = receiver with { Address = "192.0.2.99", Port = 7011 };
        var replacementSettings = settings.Clone(); replacementSettings.Options(receiver.Id).CustomBufferMs = 777;
        await StartAsync(controller, second, replacement, replacementSettings);
        var owned = controller.Capture(); var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 0;

        Assert.False(await controller.TryUpdateSettingsAsync(incoming, stale, new([canonical], Migration(receiver, canonical), [receiver.Id])));
        await PublishMetricsAsync(controller, second);

        Assert.Equal(owned.Owner, controller.Capture().Owner); Assert.Equal(replacement, controller.Snapshot.Receiver);
        Assert.Equal(777, controller.Snapshot.TargetLatency); Assert.Equal(48000, controller.Snapshot.StreamRate);
        Assert.Equal(2, factory.OpenCount); Assert.False(second.Disposed);
        Assert.DoesNotContain(second.Commands, command => command.Name == "stop");
    }

    [Fact]
    public async Task InactiveUpdateDoesNotResurrectPreviousOwnedOptions()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings); await controller.StopAsync();
        var incoming = Migrated(settings, receiver, canonical); incoming.Options(canonical.Id).CustomBufferMs = 555;

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([], Migration(receiver, canonical))));
        Assert.Equal(PlaybackState.Idle, controller.Snapshot.State); Assert.Equal(1, factory.OpenCount);
        Assert.False(controller.UsesDeviceLatency("normal"));
        await StartAsync(controller, second, canonical, incoming);
        AssertStart(second, canonical, 555, 48000, "pcm");
    }

    [Fact]
    public async Task NoContextRetainsAuthoritativeSettingsReplacementSemantics()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);

        Assert.True(await controller.TryUpdateSettingsAsync(Migrated(settings, receiver, canonical), controller.Capture()));
        await second.Started.Task.WaitAsync(Limit);

        AssertStart(second, receiver, 200, 44100, "auto"); Assert.True(first.Disposed); Assert.Equal(2, factory.OpenCount);
    }

    [Fact]
    public async Task EndpointRestartUsesTheSameProjectionWithoutRetargetingTransport()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var captured = controller.Capture(); var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 37;

        Assert.True(await controller.TryRestartForEndpointAsync(incoming, captured.Owner, captured.AutomaticIntent, new([canonical], Migration(receiver, canonical))));
        await second.Started.Task.WaitAsync(Limit);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);

        AssertStart(second, receiver, 333, 48000, "pcm"); Assert.Equal(.37, Start(second).Number("gain"));
        Assert.True(first.Disposed); Assert.Equal(2, factory.OpenCount); Assert.Equal(receiver, controller.Snapshot.Receiver);
        Assert.False(incoming.Receivers.ContainsKey(receiver.Id));
    }

    [Theory]
    [InlineData(false, false, "present")]
    [InlineData(true, false, "present")]
    [InlineData(false, true, "present")]
    [InlineData(true, true, "present")]
    [InlineData(false, false, "absent")]
    [InlineData(true, false, "absent")]
    [InlineData(false, true, "absent")]
    [InlineData(true, true, "absent")]
    [InlineData(false, false, "absent no migrations")]
    [InlineData(true, false, "absent no migrations")]
    [InlineData(false, true, "absent no migrations")]
    [InlineData(true, true, "absent no migrations")]
    [InlineData(false, false, "changed transport")]
    [InlineData(true, false, "changed transport")]
    [InlineData(false, true, "changed transport")]
    [InlineData(true, true, "changed transport")]
    public async Task SoftwareMemberIdentityProjectionPreservesTransportProfilesAndAppliesExplicitOffsets(bool editOffsets, bool broadcastAliases, string availability)
    {
        var left = Legacy(); var right = Legacy("192.0.2.11", 7010);
        if (broadcastAliases) { left = left with { Id = "synthetic-old-left" }; right = right with { Id = "synthetic-old-right" }; }
        var canonicalLeft = Canonical(left, "canonical-left"); var canonicalRight = Canonical(right, "canonical-right");
        var settings = Settings(left); settings.Options(right.Id).Codec = "alac"; settings.Options(right.Id).LatencyMode = "buffered";
        settings.SoftwareGroups.Add(new() { Id = "software:fixture", LeftId = left.Id, RightId = right.Id, SampleRate = "48000", LeftOffsetMs = -80, RightOffsetMs = 25 });
        var receiver = SoftwareStereo.Resolve([left, right], settings).Single();
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var migrations = Migration(left, canonicalLeft); migrations[right.Id] = canonicalRight.Id;
        var incoming = settings.Clone(); ReceiverCatalog.ApplyMigrations(incoming, migrations); incoming.MasterVolume = 37;
        if (broadcastAliases) { incoming.ReceiverAliases[left.Id] = canonicalLeft.Id; incoming.ReceiverAliases[right.Id] = canonicalRight.Id; }
        if (editOffsets) { incoming.SoftwareGroups[0].LeftOffsetMs = -40; incoming.SoftwareGroups[0].RightOffsetMs = 50; }
        var serialized = JsonSerializer.Serialize(incoming);
        Receiver[] catalog = availability switch
        {
            "absent" or "absent no migrations" => [],
            "changed transport" => [canonicalLeft with { Address = "192.0.2.98" }, canonicalRight with { Address = "192.0.2.99" }],
            _ => [canonicalLeft, canonicalRight]
        };

        var context = new SessionSettingsContext(catalog, availability == "absent no migrations" ? new Dictionary<string, string>() : migrations);
        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), context));
        var updated = controller.Snapshot;
        Assert.True(updated.State is PlaybackState.Connecting or PlaybackState.Streaming && updated.Receiver is { Complete: true },
            $"An unchanged owned software transport must remain active after its settings update; observed state={updated.State}, receiver={updated.Receiver?.Id ?? "none"}, message={updated.Message}.");
        var updatedReceiver = Assert.IsType<Receiver>(updated.Receiver);
        Assert.Equal(new[] { left.Id, right.Id }, updatedReceiver.Members.Select(member => member.Id));
        Assert.Equal(new[] { left.Address, right.Address }, updatedReceiver.Members.Select(member => member.Address));
        var current = editOffsets ? second : first;
        if (editOffsets) await second.Started.Task.WaitAsync(Limit);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
        await PublishMetricsAsync(controller, current);

        Assert.Equal(editOffsets ? 2 : 1, factory.OpenCount); Assert.Equal(editOffsets, first.Disposed);
        Assert.Equal(new[] { left.Id, right.Id }, controller.Snapshot.Receiver!.Members.Select(member => member.Id));
        Assert.Equal(48000, controller.Snapshot.StreamRate);
        var peers = Start(current).GetProperty("peers");
        Assert.Equal(left.Address, peers[0].Text("host")); Assert.Equal(left.Port, peers[0].Integer("port"));
        Assert.Equal(right.Address, peers[1].Text("host")); Assert.Equal(right.Port, peers[1].Integer("port"));
        Assert.Equal("pcm", peers[0].GetProperty("profile").Text("codec"));
        Assert.Equal("alac", peers[1].GetProperty("profile").Text("codec"));
        Assert.Equal("left", peers[0].GetProperty("profile").Text("channel"));
        Assert.Equal("right", peers[1].GetProperty("profile").Text("channel"));
        Assert.Equal(editOffsets ? -40 : -80, peers[0].GetProperty("profile").Integer("sync_offset_ms"));
        Assert.Equal(editOffsets ? 50 : 25, peers[1].GetProperty("profile").Integer("sync_offset_ms"));
        Assert.Equal(333, peers[0].GetProperty("profile").Integer("latency_ms"));
        Assert.Equal(500, peers[1].GetProperty("profile").Integer("latency_ms"));
        Assert.Equal(serialized, JsonSerializer.Serialize(incoming));
        Assert.Equal(canonicalLeft.Id, incoming.SoftwareGroups[0].LeftId); Assert.Equal(canonicalRight.Id, incoming.SoftwareGroups[0].RightId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionDoesNotUndoExplicitSoftwareMembershipChangeOrRemoval(bool remove)
    {
        var left = Legacy(); var right = Legacy("192.0.2.11"); var settings = Settings(left);
        settings.SoftwareGroups.Add(new() { Id = "software:fixture", LeftId = left.Id, RightId = right.Id });
        var receiver = SoftwareStereo.Resolve([left, right], settings).Single();
        var first = new Connection(); var factory = new Factory(first);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = settings.Clone();
        if (remove) incoming.SoftwareGroups.Clear(); else incoming.SoftwareGroups[0].RightId = "explicit-unavailable-member";

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([left, right], new Dictionary<string, string>(), changedMemberships: [receiver.Id])));

        Assert.Equal(remove ? PlaybackState.Idle : PlaybackState.Error, controller.Snapshot.State);
        Assert.Null(controller.Snapshot.Receiver); Assert.True(first.Disposed); Assert.Equal(1, factory.OpenCount);
        Assert.Contains(first.Commands, command => command.Name == "stop");
    }

    [Fact]
    public async Task ExplicitSoftwareSwapChangesChannelsAndFrozenMembershipIntentCannotBecomeAnOrdinaryFlush()
    {
        var left = Legacy(); var right = Legacy("192.0.2.11", 7010); var settings = Settings(left);
        settings.Options(right.Id).Codec = "alac";
        settings.SoftwareGroups.Add(new() { Id = "software:fixture", LeftId = left.Id, RightId = right.Id, SampleRate = "48000", LeftOffsetMs = -80, RightOffsetMs = 25 });
        var receiver = SoftwareStereo.Resolve([left, right], settings).Single();
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = settings.Clone(); incoming.SoftwareGroups[0].LeftId = right.Id; incoming.SoftwareGroups[0].RightId = left.Id;
        var changed = new List<string> { receiver.Id };
        var context = new SessionSettingsContext([left, right], new Dictionary<string, string>(), changedMemberships: changed);
        changed.Clear();

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), context));
        Assert.True(first.Disposed, "An explicit channel swap must restart the owned stereo transport.");
        await second.Started.Task.WaitAsync(Limit); await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);

        Assert.Equal(2, factory.OpenCount);
        var peers = Start(second).GetProperty("peers");
        Assert.Equal(right.Address, peers[0].Text("host")); Assert.Equal("left", peers[0].GetProperty("profile").Text("channel"));
        Assert.Equal(left.Address, peers[1].Text("host")); Assert.Equal("right", peers[1].GetProperty("profile").Text("channel"));
        Assert.Equal("alac", peers[0].GetProperty("profile").Text("codec")); Assert.Equal("pcm", peers[1].GetProperty("profile").Text("codec"));
        Assert.Equal(-80, peers[0].GetProperty("profile").Integer("sync_offset_ms")); Assert.Equal(25, peers[1].GetProperty("profile").Integer("sync_offset_ms"));
        Assert.Equal(right.Id, incoming.SoftwareGroups[0].LeftId); Assert.Equal(left.Id, incoming.SoftwareGroups[0].RightId);
    }

    [Fact]
    public void MultiRoomProjectionKeepsAllOwnedMembersAndAppliesSavedOffsets()
    {
        var first = Legacy();
        var second = Legacy("192.0.2.11");
        var third = Legacy("192.0.2.12");
        var settings = Settings(first);
        settings.SoftwareGroups.Add(new()
        {
            Id = "software:fixture", GroupMode = "multi_room",
            Members = [new() { ReceiverId = first.Id }, new() { ReceiverId = second.Id }, new() { ReceiverId = third.Id }]
        });
        var owned = SoftwareStereo.Resolve([first, second, third], settings).Single();
        var canonicalThird = Canonical(third, "canonical-third");
        var incoming = Migrated(settings, third, canonicalThird);
        incoming.SoftwareGroups[0].Members[2].OffsetMs = 17;

        var projected = new SessionSettingsContext([], Migration(third, canonicalThird)).Project(incoming, settings, owned);
        var resolved = SoftwareStereo.Resolve([first, second, third], projected).Single();

        Assert.True(resolved.IsMultiRoom);
        Assert.True(resolved.Online);
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, resolved.Peers.Select(member => member.Id));
        Assert.Equal(17, resolved.Members[2].SyncOffsetMs);
        Assert.Equal(canonicalThird.Id, incoming.SoftwareGroups[0].Members[2].ReceiverId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiRoomProjectionMatchesOffsetsToUniqueSpeakerIdentity(bool ambiguous)
    {
        var first = Legacy();
        var second = Legacy("192.0.2.11");
        var third = Legacy("192.0.2.12");
        var canonicalThird = Canonical(third, "canonical-third");
        var settings = Settings(first);
        settings.SoftwareGroups.Add(new()
        {
            Id = "software:fixture", GroupMode = "multi_room",
            Members = [new() { ReceiverId = first.Id, OffsetMs = 10 }, new() { ReceiverId = second.Id, OffsetMs = 20 },
                new() { ReceiverId = third.Id, OffsetMs = 30 }]
        });
        var receiver = SoftwareStereo.Resolve([first, second, third], settings).Single();
        var incoming = settings.Clone();
        incoming.ReceiverAliases["new-third-alias"] = canonicalThird.Id;
        incoming.SoftwareGroups[0].Members =
        [
            new() { ReceiverId = "new-third-alias", OffsetMs = 300 },
            new() { ReceiverId = second.Id, OffsetMs = 200 },
            new() { ReceiverId = ambiguous ? canonicalThird.Id : "unrelated", OffsetMs = 400 }
        ];

        var projected = new SessionSettingsContext([canonicalThird], Migration(third, canonicalThird)).Project(incoming, settings, receiver);
        var members = Assert.Single(projected.SoftwareGroups).Members;

        Assert.Equal(new[] { first.Id, second.Id, third.Id }, members.Select(member => member.ReceiverId));
        Assert.Equal(new[] { 10, 200, ambiguous ? 30 : 300 }, members.Select(member => member.OffsetMs));
    }

    [Fact]
    public async Task ExplicitAvailableManualMemberReplacementAdoptsNewTransportWithoutRestoringTheOldMember()
    {
        var left = Legacy(); var right = Legacy("192.0.2.11", 7010); var settings = Settings(left);
        settings.SoftwareGroups.Add(new() { Id = "software:fixture", LeftId = left.Id, RightId = right.Id, SampleRate = "48000" });
        var receiver = SoftwareStereo.Resolve([left, right], settings).Single();
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var replacement = new Receiver("manual-replacement", "Synthetic replacement", "192.0.2.12", 7020) { IsManual = true };
        var incoming = settings.Clone(); incoming.SoftwareGroups[0].RightId = replacement.Id;
        incoming.ManualReceivers.Add(new(replacement.Id, replacement.Name, replacement.Address, replacement.Port));
        incoming.Options(replacement.Id).Codec = "alac";

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(),
            new([left, right, replacement], new Dictionary<string, string>(), changedMemberships: [receiver.Id])));
        Assert.True(first.Disposed, "An explicit available member replacement must restart the stereo transport.");
        await second.Started.Task.WaitAsync(Limit); await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);

        var peers = Start(second).GetProperty("peers");
        Assert.Equal(2, factory.OpenCount); Assert.Equal(2, peers.GetArrayLength());
        Assert.Equal(left.Address, peers[0].Text("host")); Assert.Equal(replacement.Address, peers[1].Text("host"));
        Assert.Equal(replacement.Port, peers[1].Integer("port")); Assert.Equal("right", peers[1].GetProperty("profile").Text("channel"));
        Assert.Equal("alac", peers[1].GetProperty("profile").Text("codec"));
        Assert.DoesNotContain(controller.Snapshot.Receiver!.Members, member => member.Id == right.Id);
        Assert.Equal(replacement.Id, incoming.SoftwareGroups[0].RightId);
    }

    [Fact]
    public async Task EffectiveLatencyQueryUsesCandidateGlobalModeAndOwnedReceiverOverrides()
    {
        var receiver = Legacy(); var canonical = Canonical(receiver); var settings = Settings(receiver);
        settings.Options(receiver.Id).LatencyMode = null;
        var first = new Connection(); var second = new Connection(); var factory = new Factory(first, second);
        await using var controller = TestSessionController.Create(factory, new Audio(), timing: Timing);
        await StartAsync(controller, first, receiver, settings);
        var incoming = Migrated(settings, receiver, canonical); incoming.MasterVolume = 37;

        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([canonical], Migration(receiver, canonical))));
        Assert.False(controller.UsesDeviceLatency("normal")); Assert.False(controller.UsesDeviceLatency("buffered"));
        Assert.True(controller.UsesDeviceLatency("custom"));
        Assert.Equal(1, factory.OpenCount);
        incoming.Options(canonical.Id).CustomBufferMs = null;
        Assert.True(await controller.TryUpdateSettingsAsync(incoming, controller.Capture(), new([canonical], Migration(receiver, canonical))));
        Assert.False(controller.UsesDeviceLatency("custom"));
        await controller.StopAsync(); Assert.False(controller.UsesDeviceLatency("custom"));
    }

    private static JsonElement Start(Connection connection) => Assert.Single(connection.Commands, command => command.Name == "start").Parameters;
    private static void AssertStart(Connection connection, Receiver receiver, int latency, int rate, string codec)
    {
        var start = Start(connection); var peer = Assert.Single(start.GetProperty("peers").EnumerateArray());
        Assert.Equal(receiver.Address, peer.Text("host")); Assert.Equal(receiver.Port, peer.Integer("port"));
        Assert.Equal(latency, start.Integer("latency_ms")); Assert.Equal(rate, start.Integer("sample_rate"));
        Assert.Equal(codec, peer.GetProperty("profile").Text("codec"));
    }
    private static async Task StartAsync(SessionController controller, Connection connection, Receiver receiver, AppSettings settings)
    {
        await controller.StartAsync(receiver, settings); await connection.Started.Task.WaitAsync(Limit);
        await SnapshotAsync(controller, snapshot => snapshot.State == PlaybackState.Streaming);
    }
    private static async Task PublishMetricsAsync(SessionController controller, Connection connection)
    {
        var marker = Interlocked.Increment(ref _metricMarker);
        connection.Emit(new { @event = "capture_metrics", metrics = new { underrun_packets = marker } });
        await SnapshotAsync(controller, snapshot => snapshot.Metrics?.Underruns == marker);
    }
    private static int _metricMarker;
    private static async Task<SessionSnapshot> SnapshotAsync(SessionController controller, Func<SessionSnapshot, bool> predicate)
    {
        var observed = new TaskCompletionSource<SessionSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(SessionSnapshot snapshot) { if (predicate(snapshot)) observed.TrySetResult(snapshot); }
        controller.Changed += Changed;
        try
        {
            if (predicate(controller.Snapshot)) observed.TrySetResult(controller.Snapshot);
            return await observed.Task.WaitAsync(Limit);
        }
        finally { controller.Changed -= Changed; }
    }
    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
    }
    private sealed class PendingPin
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource<string?> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string?> RequestAsync(Receiver _, CancellationToken cancellation)
        {
            Entered.TrySetResult(); return Release.Task.WaitAsync(cancellation);
        }
    }
    private sealed record Command(string Name, JsonElement Parameters);
    private sealed class Connection : IEngineConnection
    {
        private readonly Channel<JsonElement> _events = Channel.CreateUnbounded<JsonElement>();
        private string _session = "";
        public bool RequestPin { get; init; }
        public bool Disposed { get; private set; }
        public ConcurrentQueue<Command> Commands { get; } = new();
        public TaskCompletionSource Started { get; } = Signal();
        public Task SendAsync(string session, string command, object? parameters, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            Commands.Enqueue(new(command, JsonSerializer.SerializeToElement(parameters ?? new { })));
            switch (command)
            {
                case "hello":
                    _session = session;
                    Emit(new { @event = "hello", capabilities = new[] { "receiver_ownership_guard", "receiver_profiles", "software_stereo", "continuity", "buffered_audio", "standby_release", "member_volume", "receiver_password", "application_capture" } });
                    break;
                case "start": _session = session; Started.TrySetResult(); Emit(new { @event = "streaming" }); break;
                case "pair" when RequestPin: _session = session; Emit(new { @event = "pin_required" }); break;
                case "pair_pin": Emit(new { @event = "paired" }); break;
                case "stop": _events.Writer.TryComplete(); break;
            }
            return Task.CompletedTask;
        }
        public void Emit(object payload)
        {
            var envelope = JsonSerializer.SerializeToNode(payload)!.AsObject();
            envelope["version"] = 1; envelope["session_id"] = _session;
            Assert.True(_events.Writer.TryWrite(JsonSerializer.SerializeToElement(envelope)));
        }
        public async ValueTask<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            try { return await _events.Reader.ReadAsync(cancellation); }
            catch (ChannelClosedException) { return null; }
        }
        public ValueTask DisposeAsync() { Disposed = true; _events.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
    private sealed class Factory(params Connection[] connections) : IEngineFactory
    {
        private readonly ConcurrentQueue<Connection> _next = new(connections);
        private int _openCount;
        public int OpenCount => Volatile.Read(ref _openCount);
        public IEngineConnection Open()
        {
            Interlocked.Increment(ref _openCount);
            return _next.TryDequeue(out var connection) ? connection : throw new IOException("Unexpected synthetic engine start.");
        }
    }
    private sealed class Audio : IAudioService
    {
        private readonly ConcurrentQueue<Gate> _restoreGates = new();
        public event Action? EndpointsChanged { add { } remove { } }
        public Task<IReadOnlyList<AudioEndpoint>> GetEndpointsAsync() => Task.FromResult<IReadOnlyList<AudioEndpoint>>([]);
        public Task<string?> GetDefaultEndpointIdAsync() => Task.FromResult<string?>("synthetic-endpoint");
        public Task MuteAsync(string? endpointId) => Task.CompletedTask;
        public Gate HoldNextRestore() { var gate = new Gate(); _restoreGates.Enqueue(gate); return gate; }
        public async Task RestoreAsync()
        {
            if (!_restoreGates.TryDequeue(out var gate)) return;
            gate.Entered.TrySetResult(); await gate.Release.Task;
        }
    }
}
