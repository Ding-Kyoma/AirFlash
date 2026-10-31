using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class MixedGroupMigrationTests
{
    private const ulong Bit40 = AirPlayRouting.AirPlay2Feature;
    private const string LegacyConfig = """
        {"schema_version":4,"last_receiver_id":"software:g",
         "receivers":{"a":{"volume":28,"latency_mode":"custom","custom_buffer_ms":375},"b":{"volume":61,"codec":"alac","sample_rate":"48000"}},
         "software_groups":[{"id":"software:g","name":"Living room","left_id":"a","right_id":"b","left_offset_ms":-80,"right_offset_ms":25,"sample_rate":"48000"}]}
        """;

    private static Receiver Pod(string id, string name, ulong? features, string sourceVersion = "", string address = "")
        => new(id, name, address.Length > 0 ? address : "127.0.0." + (id == "a" ? "1" : "2")) { DeviceId = id, Features = features, SourceVersion = sourceVersion };
    private static Receiver AirPlay2Pod(string id, string name) => Pod(id, name, Bit40, "366.0");
    private static Receiver ClassicPod(string id, string name) => Pod(id, name, 0UL, "220.68");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UpgradePreservesLegacyGroupsRegardlessOfCapabilityEvidence(bool mixed)
    {
        using var fixture = new Fixture(LegacyConfig);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        settings.Options("software:g").Volume = 35;
        Assert.True(store.MixedGroupMigrationPending);
        var log = new List<string>();
        var catalog = new[] { AirPlay2Pod("a", "Left"), mixed ? ClassicPod("b", "Right") : AirPlay2Pod("b", "Right") };
        var report = MixedGroupMigration.Apply(settings, catalog, log.Add);
        Assert.Empty(report.Splits);
        Assert.Empty(log);
        Assert.True(report.Changed);
        store.CompleteMixedGroupMigration(settings);
        Assert.True(File.Exists(fixture.ConfigPath + ".pre-v5.bak"));
        var reloaded = new SettingsStore(fixture.ConfigPath);
        var second = reloaded.Load();
        Assert.False(reloaded.MixedGroupMigrationPending);
        var group = Assert.Single(second.SoftwareGroups);
        Assert.Equal("a", group.LeftId);
        Assert.Equal("b", group.RightId);
        Assert.Equal(-80, group.LeftOffsetMs);
        Assert.Equal(25, group.RightOffsetMs);
        Assert.Equal("48000", group.SampleRate);
        Assert.Equal("software:g", second.LastReceiverId);
        Assert.Equal(35, second.Options("software:g").Volume);
        Assert.Equal(375, second.Latency("a"));
        Assert.Equal(61, second.Options("b").Volume);
        Assert.False(MixedGroupMigration.Apply(second, catalog).Changed);
    }
    [Fact]
    public void OfflineGroupsAreAcknowledgedWithoutDeletingTheirConfiguration()
    {
        using var fixture = new Fixture(LegacyConfig);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        Assert.Empty(MixedGroupMigration.Apply(settings, []).Splits);
        Assert.True(MixedGroupMigration.IsSettled(settings));
        store.CompleteMixedGroupMigration(settings);
        Assert.Single(new SettingsStore(fixture.ConfigPath).Load().SoftwareGroups);
    }

    [Fact]
    public void RuntimeDriftIsNotModifiedAndTheRepairActionSplitsIt()
    {
        var settings = new AppSettings();
        settings.Options("a").Volume = 28;
        settings.Options("b").Volume = 61;
        settings.SoftwareGroups.Add(new() { Id = "software:g", Name = "Office", LeftId = "a", RightId = "b", MixedInvariantVersion = AppSettings.CurrentSchemaVersion });
        var log = new List<string>();

        var report = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], log.Add);

        // Drift: the config stays exactly as the user saved it.
        Assert.Empty(report.Splits);
        Assert.False(report.Changed);
        Assert.Empty(log);
        Assert.Single(settings.SoftwareGroups);
        Assert.Equal("a", settings.SoftwareGroups[0].LeftId);

        // ... but it resolves as unusable with the two-level message surface.
        var resolved = Assert.Single(SoftwareStereo.Resolve([AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], settings));
        Assert.False(resolved.Online);
        Assert.False(resolved.Complete);
        Assert.Empty(resolved.Members);
        Assert.Contains(settings.SoftwareGroups, group => group.Id == resolved.Id);

        // The explicit repair action dissolves only the group record.
        Assert.True(SoftwareStereo.Split(settings, "software:g"));
        Assert.Empty(settings.SoftwareGroups);
        Assert.Equal(28, settings.Options("a").Volume);
        Assert.Equal(61, settings.Options("b").Volume);
        Assert.Empty(SoftwareStereo.Resolve([AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], settings));
        Assert.False(SoftwareStereo.Split(settings, "software:g"));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "airflash-q45-" + Guid.NewGuid().ToString("N"));
        public string ConfigPath { get; }
        public Fixture(string config)
        {
            Directory.CreateDirectory(_directory);
            ConfigPath = System.IO.Path.Combine(_directory, "config.json");
            File.WriteAllText(ConfigPath, config);
        }
        public void Dispose() { try { Directory.Delete(_directory, true); } catch (IOException) { } }
    }
}
