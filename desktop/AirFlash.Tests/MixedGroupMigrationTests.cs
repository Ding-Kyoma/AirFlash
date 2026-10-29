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

    [Fact]
    public void LegacyMixedGroupIsSplitOnceAndDeviceSettingsArePreserved()
    {
        using var fixture = new Fixture(LegacyConfig);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        Assert.True(store.MixedGroupMigrationPending);
        var log = new List<string>();

        var report = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], log.Add);

        var split = Assert.Single(report.Splits);
        Assert.True(report.Changed);
        Assert.Equal("software:g", split.GroupId);
        Assert.Empty(settings.SoftwareGroups);
        Assert.Null(settings.LastReceiverId);
        Assert.Equal(28, settings.Options("a").Volume);
        Assert.Equal(375, settings.Latency("a"));
        Assert.Equal(61, settings.Options("b").Volume);
        Assert.Equal("alac", settings.Options("b").Codec);
        Assert.Equal("48000", settings.Options("b").SampleRate);
        Assert.Equal("mixed_group_migrated: [\"HomePod mini\",\"AirPort Express\"] → [\"HomePod mini\"], [\"AirPort Express\"] (group \"Living room\")", Assert.Single(log));
        store.CompleteMixedGroupMigration(settings);
        Assert.False(store.MixedGroupMigrationPending);
        using (var json = JsonDocument.Parse(File.ReadAllText(fixture.ConfigPath)))
        {
            Assert.Equal(5, json.RootElement.GetProperty("schema_version").GetInt32());
            Assert.Equal(0, json.RootElement.GetProperty("software_groups").GetArrayLength());
        }
        Assert.True(File.Exists(fixture.ConfigPath + ".pre-v5.bak"));

        // Idempotent and never runs twice: the reloaded schema-5 config has no
        // pending migration, and a second pass has nothing to split or log.
        var reloaded = new SettingsStore(fixture.ConfigPath);
        var second = reloaded.Load();
        Assert.False(reloaded.MixedGroupMigrationPending);
        Assert.Empty(second.SoftwareGroups);
        Assert.Equal(28, second.Options("a").Volume);
        Assert.Equal(375, second.Latency("a"));
        Assert.Equal(61, second.Options("b").Volume);
        var again = new List<string>();
        var secondReport = MixedGroupMigration.Apply(second, [AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], again.Add);
        Assert.Empty(secondReport.Splits);
        Assert.False(secondReport.Changed);
        Assert.Empty(again);
    }

    [Fact]
    public void LegacyHomogeneousGroupIsKeptAndDischargedWithoutLogging()
    {
        using var fixture = new Fixture(LegacyConfig);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        var log = new List<string>();

        var report = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "Left HomePod"), AirPlay2Pod("b", "Right HomePod")], log.Add);

        Assert.Empty(report.Splits);
        Assert.True(report.Changed);
        Assert.Empty(log);
        Assert.True(MixedGroupMigration.IsSettled(settings));
        var group = Assert.Single(settings.SoftwareGroups);
        Assert.Equal(AppSettings.CurrentSchemaVersion, group.MixedInvariantVersion);
        Assert.Equal("a", group.LeftId);
        Assert.Equal(-80, group.LeftOffsetMs);

        store.CompleteMixedGroupMigration(settings);
        var reloaded = new SettingsStore(fixture.ConfigPath);
        var second = reloaded.Load();
        Assert.False(reloaded.MixedGroupMigrationPending);
        Assert.Equal(AppSettings.CurrentSchemaVersion, Assert.Single(second.SoftwareGroups).MixedInvariantVersion);
        // A discharged pair is drift territory now: a later contradiction is
        // never migrated, even if this pass is invoked again.
        var again = new List<string>();
        Assert.Empty(MixedGroupMigration.Apply(second, [AirPlay2Pod("a", "Left HomePod"), ClassicPod("b", "Right HomePod")], again.Add).Splits);
        Assert.Empty(again);
        Assert.Single(second.SoftwareGroups);
    }

    [Fact]
    public void MissingEvidenceDefersTheSplitUntilTxtArrives()
    {
        using var fixture = new Fixture(LegacyConfig);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        var log = new List<string>();

        // The right member is not discovered: no TXT, so no proof either way.
        var first = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "HomePod mini")], log.Add);
        Assert.Empty(first.Splits);
        Assert.False(first.Changed);
        Assert.Empty(log);
        Assert.False(MixedGroupMigration.IsSettled(settings));
        Assert.Equal(0, Assert.Single(settings.SoftwareGroups).MixedInvariantVersion);

        // Evidence arrives and proves the contradiction; the legacy config is
        // split on this pass rather than being left as permanent drift.
        var second = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "HomePod mini"), ClassicPod("b", "AirPort Express")], log.Add);
        Assert.Single(second.Splits);
        Assert.Single(log);
        Assert.Empty(settings.SoftwareGroups);
    }

    [Fact]
    public void LegacyGroupWithAManualMemberSettlesWithoutSplitting()
    {
        const string config = """
            {"schema_version":4,
             "manual_receivers":[{"id":"manual","name":"Kitchen","host":"speaker.local","port":7000}],
             "receivers":{"manual":{"volume":42}},
             "software_groups":[{"id":"software:g","name":"Mixed place","left_id":"a","right_id":"manual","left_offset_ms":0,"right_offset_ms":0,"sample_rate":"44100"}]}
            """;
        using var fixture = new Fixture(config);
        var store = new SettingsStore(fixture.ConfigPath);
        var settings = store.Load();
        Assert.True(store.MixedGroupMigrationPending);
        var log = new List<string>();

        // A manual receiver never advertises TXT, so its capability is Unknown
        // forever and the pair can never be proven mixed. The check is discharged
        // instead of leaving the config one version back indefinitely.
        var report = MixedGroupMigration.Apply(settings, [AirPlay2Pod("a", "HomePod mini")], log.Add);

        Assert.Empty(report.Splits);
        Assert.True(report.Changed);
        Assert.Empty(log);
        Assert.True(MixedGroupMigration.IsSettled(settings));
        Assert.Equal(AppSettings.CurrentSchemaVersion, Assert.Single(settings.SoftwareGroups).MixedInvariantVersion);
        store.CompleteMixedGroupMigration(settings);
        var reloaded = new SettingsStore(fixture.ConfigPath);
        var second = reloaded.Load();
        Assert.False(reloaded.MixedGroupMigrationPending);
        Assert.Single(second.SoftwareGroups);
        Assert.Equal(42, second.Options("manual").Volume);
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
