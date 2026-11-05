using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SoftwareStereoTests
{
    [Fact]
    public void LoadingNullGroupsAndSavingLegacyObjectsUsesSchemaFour()
    {
        var root = Path.Combine(Path.GetTempPath(), "airflash-v3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json");
            File.WriteAllText(path, """{"schema_version":3,"software_groups":[null]}""");
            var store = new SettingsStore(path); Assert.Empty(store.Load().SoftwareGroups);
            store.Save(new AppSettings { SchemaVersion = 2 });
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(5, json.RootElement.GetProperty("schema_version").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void CopyFromRetainsBoundSoftwareGroupAndUpdatesItsFields()
    {
        var settings = new AppSettings(); settings.SoftwareGroups.Add(new() { LeftId = "a", RightId = "b" });
        var bound = settings.SoftwareGroups[0]; var changed = settings.Clone(); changed.SoftwareGroups[0].Swap(); changed.SoftwareGroups[0].Name = "Office";
        settings.CopyFrom(changed);
        Assert.Same(bound, settings.SoftwareGroups[0]); Assert.Equal("b", bound.LeftId); Assert.Equal("Office", bound.Name);
    }
    private static Receiver Pod(string id, string ip, ulong? features = null, string sourceVersion = "") => new(id, id, ip) { DeviceId = id, Features = features, SourceVersion = sourceVersion };
    [Fact]
    public void MixedSavedGroupResolvesOfflineInsteadOfMixed()
    {
        var settings = new AppSettings(); settings.SoftwareGroups.Add(new() { LeftId = "a", RightId = "b" });
        var resolved = SoftwareStereo.Resolve([
            Pod("a", "127.0.0.1", AirPlayRouting.AirPlay2Feature, "366.0"),
            Pod("b", "127.0.0.2", 0UL, "220.68")], settings).Single();
        // Session restore goes through the same invariant: a legacy mixed pair
        // cannot come back as a mixed member list.
        Assert.False(resolved.Online);
        Assert.False(resolved.Complete);
        Assert.Empty(resolved.Members);
    }
    [Fact]
    public void HomogeneousSavedGroupStillResolvesWithBothMembers()
    {
        var settings = new AppSettings(); settings.SoftwareGroups.Add(new() { LeftId = "a", RightId = "b" });
        var resolved = SoftwareStereo.Resolve([
            Pod("a", "127.0.0.1", AirPlayRouting.AirPlay2Feature, "366.0"),
            Pod("b", "127.0.0.2", AirPlayRouting.AirPlay2Feature, "366.0")], settings).Single();
        Assert.True(resolved.Online);
        Assert.True(resolved.Complete);
        Assert.Equal(2, resolved.Members.Length);
    }
    [Fact]
    public async Task SchemaThreeMigrationPreservesDeviceOptionsAndBacksUpOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "airflash-v3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json"); const string old = """{"schema_version":2,"master_volume":37,"receivers":{"a":{"latency_mode":"custom","custom_buffer_ms":375,"volume":28,"auto_connect":false}}}""";
            await File.WriteAllTextAsync(path, old); var store = new SettingsStore(path); var settings = store.Load();
            Assert.Equal(5, settings.SchemaVersion); Assert.Equal(375, settings.Latency("a")); Assert.Equal(37, settings.MasterVolume); Assert.Equal(28, settings.Options("a").Volume); Assert.False(settings.Options("a").AutoConnect);
            settings.Options("a").Codec = "alac"; settings.Options("a").SampleRate = "48000";
            settings.SoftwareGroups.Add(new() { LeftId = "a", RightId = "b", LeftOffsetMs = -80, RightOffsetMs = 25 });
            await ((ISettingsStore)store).SaveAsync(settings); Assert.Equal(old, await File.ReadAllTextAsync(path + ".pre-v3.bak"));
            var loaded = store.Load(); Assert.Null(loaded.Validate()); Assert.Equal("48000", loaded.Options("a").SampleRate); Assert.Equal(settings.SoftwareGroups[0].Id, loaded.SoftwareGroups[0].Id);
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public void ResolveRoutesChannelsAndSwapRetainsPhysicalOffsetsAndStableIdentity()
    {
        var settings = new AppSettings(); var group = new SoftwareStereoGroup { LeftId = "a", RightId = "b", LeftOffsetMs = -80, RightOffsetMs = 25, SampleRate = "48000" }; settings.SoftwareGroups.Add(group);
        var catalog = new[] { Pod("a", "127.0.0.1"), Pod("b", "127.0.0.2") }; var resolved = SoftwareStereo.Resolve(catalog, settings).Single();
        Assert.True(resolved.Complete && resolved.Online); Assert.Equal("left", resolved.Members[0].Channel); Assert.Equal(-80, resolved.Members[0].SyncOffsetMs); Assert.Equal("48000", settings.SampleRate(resolved));
        group.Swap(); var swapped = SoftwareStereo.Resolve(catalog, settings).Single(); Assert.Equal(resolved.Id, swapped.Id); Assert.Equal("b", swapped.Members[0].Id); Assert.Equal(25, swapped.Members[0].SyncOffsetMs); Assert.NotEqual(resolved.TransportKey, swapped.TransportKey);
    }
    [Fact]
    public void RejectsNestedNativePairsDuplicatePhysicalMembersAndInvalidOffsets()
    {
        var settings = new AppSettings(); var group = new SoftwareStereoGroup { LeftId = "a", RightId = "a" }; settings.SoftwareGroups.Add(group); Assert.NotNull(settings.Validate());
        group.RightId = "b"; group.LeftOffsetMs = 501; Assert.NotNull(settings.Validate()); group.LeftOffsetMs = 0; group.LeftId = "stereo:pair"; Assert.NotNull(settings.Validate()); group.LeftId = "a"; Assert.Null(settings.Validate());
        Assert.False(SoftwareStereo.Resolve([Pod("a", "127.0.0.1"), Pod("b", "127.0.0.1")], settings).Single().Online);
        var native = Pod("stereo:pair", "127.0.0.1") with { Members = [Pod("a", "127.0.0.1") with { StereoId = "pair" }, Pod("b", "127.0.0.2") with { StereoId = "pair" }] };
        Assert.False(SoftwareStereo.Resolve([native], settings).Single().Complete); Assert.Empty(SoftwareStereo.Candidates([native]));
    }
    [Fact]
    public void IdentityMigrationUpdatesGroupMembersAndProfiles()
    {
        var settings = new AppSettings(); settings.SoftwareGroups.Add(new() { LeftId = "old", RightId = "b" }); settings.Options("old").Codec = "alac"; settings.Options("old").SampleRate = "48000"; settings.Options("old").Timing = "ntp";
        ReceiverCatalog.ApplyMigrations(settings, new Dictionary<string,string> { ["old"] = "new" }); Assert.Equal("new", settings.SoftwareGroups[0].LeftId); Assert.Equal("alac", settings.Options("new").Codec); Assert.Equal("48000", settings.Options("new").SampleRate); Assert.Equal("ntp", settings.Options("new").Timing);
    }
    [Fact]
    public void CapabilityHandshakeRejectsAnOlderEngineBeforeSendingNewParameters()
    {
        Assert.Throws<IOException>(() => EngineHandshake.RequireStreamingCapabilities(System.Text.Json.JsonSerializer.SerializeToElement(new { version = 1, @event = "hello", capabilities = new[] { "continuity" } })));
        Assert.Equal((1ul << 41) | 1, ReceiverAggregator.ParseFeatures("0x1,0x200"));
    }
    [Fact]
    public void MultiRoomGroupResolvesAllMembersWithStereoChannelsAndCorrectDetail()
    {
        var settings = new AppSettings();
        var group = new SoftwareStereoGroup
        {
            Id = "software:multi",
            Name = "Whole Flat",
            GroupMode = "multi_room",
            Members = [
                new() { ReceiverId = "a", OffsetMs = 0 },
                new() { ReceiverId = "b", OffsetMs = -25 },
                new() { ReceiverId = "c", OffsetMs = 50 },
                new() { ReceiverId = "d", OffsetMs = 10 }
            ],
            SampleRate = "48000"
        };
        settings.SoftwareGroups.Add(group);
        var catalog = new[]
        {
            Pod("a", "192.168.1.10"),
            Pod("b", "192.168.1.11"),
            Pod("c", "192.168.1.12"),
            Pod("d", "192.168.1.13")
        };
        var resolved = SoftwareStereo.Resolve(catalog, settings).Single();
        Assert.True(resolved.Complete);
        Assert.True(resolved.Online);
        Assert.Equal("multi_room", resolved.GroupMode);
        Assert.Equal(4, resolved.Members.Length);
        Assert.Equal("Multi-room · 4 speakers", resolved.Detail);
        Assert.All(resolved.Members, m => Assert.Equal("stereo", m.Channel));
        Assert.Equal(-25, resolved.Members[1].SyncOffsetMs);
        Assert.Equal(50, resolved.Members[2].SyncOffsetMs);

        var withOffline = SoftwareStereo.Resolve([catalog[0], catalog[1], catalog[2]], settings).Single();
        Assert.False(withOffline.Complete);
    }
    [Fact]
    public void MultiRoomValidationEnforcesMinimumMembersDistinctReceiversAndOffsets()
    {
        var settings = new AppSettings();
        var group = new SoftwareStereoGroup
        {
            Id = "software:multi",
            Name = "Living + Kitchen",
            GroupMode = "multi_room",
            Members = [
                new() { ReceiverId = "a", OffsetMs = 0 },
                new() { ReceiverId = "b", OffsetMs = 0 }
            ]
        };
        settings.SoftwareGroups.Add(group);
        Assert.Null(settings.Validate());

        group.Members.RemoveAt(1);
        Assert.NotNull(settings.Validate());

        group.Members.Add(new() { ReceiverId = "a", OffsetMs = 0 });
        Assert.NotNull(settings.Validate());

        group.Members[1] = new() { ReceiverId = "b", OffsetMs = 501 };
        Assert.NotNull(settings.Validate());
    }
    [Fact]
    public void MultiRoomGroupMembershipRefusesAirPlay1And2Mix()
    {
        var settings = new AppSettings();
        var group = new SoftwareStereoGroup
        {
            Id = "software:multi",
            Name = "Mixed Group",
            GroupMode = "multi_room",
            Members = [
                new() { ReceiverId = "ap2_1", OffsetMs = 0 },
                new() { ReceiverId = "ap2_2", OffsetMs = 0 },
                new() { ReceiverId = "classic", OffsetMs = 0 }
            ]
        };
        settings.SoftwareGroups.Add(group);
        var catalog = new[]
        {
            Pod("ap2_1", "192.168.1.10", AirPlayRouting.AirPlay2Feature, "366.0"),
            Pod("ap2_2", "192.168.1.11", AirPlayRouting.AirPlay2Feature, "366.0"),
            Pod("classic", "192.168.1.12", 0UL, "220.68")
        };
        var resolved = SoftwareStereo.Resolve(catalog, settings).Single();
        Assert.False(resolved.Online);
        Assert.False(resolved.Complete);
        Assert.Empty(resolved.Members);
    }
}
