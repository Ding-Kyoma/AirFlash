using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class ForcedLatencySettingsTests
{
    [Fact]
    public async Task FreshAndResetSettingsUseNormalWithoutForcing()
    {
        using var fixture = new Fixture();
        var store = new SettingsStore(fixture.Path);
        Assert.Equal("normal", store.Load().LatencyMode);
        var settings = new AppSettings { LatencyMode = "custom", CustomBufferMs = 650, ForceLatency = true };
        store.Save(settings);
        await store.ResetAsync();
        var reset = store.Load();
        Assert.Equal("normal", reset.LatencyMode);
        Assert.False(reset.ForceLatency);
    }

    [Theory]
    [InlineData("{\"schema_version\":4}", "normal")]
    [InlineData("{\"schema_version\":5,\"LATENCY_MODE\":\"custom\",\"custom_buffer_ms\":650}", "custom")]
    [InlineData("{\"schema_version\":4,\"latency_mode\":\"buffered\"}", "buffered")]
    [InlineData("{\"schema_version\":5,\"latency_mode\":\"adaptive\"}", "normal")]
    [InlineData("{\"schema_version\":5,\"LATENCY_MODE\":\"ADAPTIVE\"}", "normal")]
    [InlineData("{\"schema_version\":5,\"latency_mode\":\"custom\",\"custom_buffer_ms\":650,\"receivers\":{\"a\":{\"latency_mode\":\"adaptive\"},\"b\":{\"latency_mode\":\"buffered\"}}}", "custom")]
    public void ExistingFilesKeepTheReleaseModeAndRecoveryAcrossRepeatedSaves(string json, string expected)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, json);
        for (var pass = 0; pass < 3; pass++)
        {
            var store = new SettingsStore(fixture.Path);
            var settings = store.Load();
            Assert.Equal(expected, settings.LatencyMode);
            Assert.False(settings.ForceLatency);
            if (expected == "custom") Assert.Equal(650, settings.CustomBufferMs);
            if (json.Contains("\"receivers\""))
            {
                Assert.Equal("normal", settings.ReadOptions("a").LatencyMode);
                Assert.Equal("buffered", settings.ReadOptions("b").LatencyMode);
            }
            store.Save(settings);
        }
    }

    [Fact]
    public void SavingAProgrammaticAdaptiveValueNormalizesIt()
    {
        using var fixture = new Fixture();
        var settings = new AppSettings();
        settings.LatencyMode = "adaptive";
        settings.Options("a").LatencyMode = "adaptive";
        var store = new SettingsStore(fixture.Path);
        store.Save(settings);
        var loaded = store.Load();
        Assert.Equal("normal", loaded.LatencyMode);
        Assert.Equal("normal", loaded.ReadOptions("a").LatencyMode);
    }

    [Fact]
    public void ForcedChoicesSurviveCopiesMergesReloadsAndModeSwitches()
    {
        using var fixture = new Fixture();
        var baseline = new AppSettings { LatencyMode = "custom", CustomBufferMs = 675 };
        var draft = baseline.Clone(); draft.ForceLatency = true;
        draft.Options("a").ForceLatency = false; draft.Options("b").ForceLatency = true;
        var current = baseline.Clone(); current.MasterVolume = 37;
        var merged = SettingsMerge.Merge(baseline, draft, current);
        var copy = new AppSettings(); copy.CopyFrom(merged);
        Assert.Equal(37, copy.MasterVolume);
        var store = new SettingsStore(fixture.Path);
        for (var pass = 0; pass < 3; pass++)
        {
            store.Save(copy);
            copy = new SettingsStore(fixture.Path).Load();
            Assert.True(copy.ForcesLatency("c"));
            Assert.False(copy.ForcesLatency("a"));
            Assert.True(copy.ForcesLatency("b"));
            Assert.Equal(675, copy.CustomBufferMs);
        }
        copy.LatencyMode = "normal";
        Assert.True(copy.ForcesLatency("b"));
        Assert.True(copy.ForceLatency);
        copy.LatencyMode = "custom";
        Assert.True(copy.ForcesLatency("b"));
        Assert.False(copy.ForcesLatency("a"));
        Assert.True(copy.UsesDeviceLatency(new("a", "A", "127.0.0.1")));
        ReceiverCatalog.ApplyMigrations(copy, new Dictionary<string, string> { ["a"] = "new-a", ["b"] = "new-b" });
        store.Save(copy);
        var migrated = store.Load();
        Assert.True(migrated.ReadOptions("new-b").ForceLatency);
        Assert.False(migrated.ReadOptions("new-a").ForceLatency);
        Assert.True(migrated.ForceLatency);
        Assert.Equal(675, migrated.CustomBufferMs);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airflash-forced-tests", Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(_directory, "settings.json");
        public Fixture() => Directory.CreateDirectory(_directory);
        public void Dispose() => Directory.Delete(_directory, true);
    }
}
