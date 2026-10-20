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
            Assert.Equal(4, json.RootElement.GetProperty("schema_version").GetInt32());
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
    private static Receiver Pod(string id, string ip) => new(id, id, ip) { DeviceId = id };
    [Fact]
    public async Task SchemaThreeMigrationPreservesDeviceOptionsAndBacksUpOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "airflash-v3-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "config.json"); const string old = """{"schema_version":2,"master_volume":37,"receivers":{"a":{"latency_mode":"custom","custom_buffer_ms":375,"volume":28,"auto_connect":false}}}""";
            await File.WriteAllTextAsync(path, old); var store = new SettingsStore(path); var settings = store.Load();
            Assert.Equal(4, settings.SchemaVersion); Assert.Equal(375, settings.Latency("a")); Assert.Equal(37, settings.MasterVolume); Assert.Equal(28, settings.Options("a").Volume); Assert.False(settings.Options("a").AutoConnect);
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
}
