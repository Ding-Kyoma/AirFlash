using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class SettingsTests
{
    [Fact]
    public void CopyFromRetainsBoundObjectsAndRoundTripsAllSettings()
    {
        var source = new AppSettings
        {
            StartAtLogin = true, MuteWhileStreaming = false, AutoConnectOnDiscover = false,
            ForceReconnect = false, MaxReconnectAttempts = 12, UiLanguage = "zh", Theme = "dark",
            StreamSampleRate = "48000",
            LatencyMode = "custom", CustomBufferMs = 650, StandbyEnabled = true,
            StandbySilenceSeconds = 22, CaptureMode = "endpoint", CaptureEndpoint = "b",
            MasterVolume = 41, LastReceiverId = "a",
            Extra = new() { ["future_option"] = JsonSerializer.SerializeToElement(new { enabled = true }) }
        };
        source.Options("a").Volume = 52; source.Options("a").Hidden = true;
        source.ManualReceivers.Add(new("a", "Speaker", "127.0.0.1", 7000));
        var target = new AppSettings(); var options = target.Options("a"); target.Options("removed");
        var manual = target.ManualReceivers;
        target.CopyFrom(source);
        Assert.Same(options, target.Options("a")); Assert.Same(manual, target.ManualReceivers);
        Assert.False(target.Receivers.ContainsKey("removed")); Assert.True(SettingsMerge.Equal(source, target));
        source.Options("a").Volume = 99; source.Extra.Clear();
        Assert.Equal(52, target.Options("a").Volume); Assert.True(target.Extra!.ContainsKey("future_option"));
    }
    [Fact]
    public async Task AsynchronousSaveKeepsAtomicMigrationAndUnknownFields()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "config.json");
        const string previous = "{\"schema_version\":1,\"extra_option\":true}";
        try
        {
            File.WriteAllText(path, previous);
            ISettingsStore store = new SettingsStore(path); var settings = store.Load();
            settings.MasterVolume = 21; await store.SaveAsync(settings);
            var saved = new SettingsStore(path).Load();
            Assert.Equal(21, saved.MasterVolume); Assert.True(saved.Extra!["extra_option"].GetBoolean());
            Assert.Equal(previous, File.ReadAllText(path + ".pre-wpf.bak"));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void RetiredLayoutSettingsAreNormalizedAndLanguagePersists()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"show_wider_volume":true,"show_streaming_modes":false,"show_master_control":false,"ui_language":"zh"}""", AppSettings.JsonOptions)!;
        Assert.False(settings.ShowWiderVolume);
        Assert.True(settings.ShowStreamingModes);
        Assert.True(settings.ShowMasterControl);
        Assert.Equal("zh", settings.Clone().UiLanguage);
        Assert.Null(settings.Validate());
        settings.UiLanguage = "invalid";
        Assert.NotNull(settings.Validate());
    }
    [Fact]
    public void FirstRunDefaultsMatchStartupPage()
    {
        var settings = new AppSettings();
        Assert.False(settings.StartAtLogin);
        Assert.False(settings.AutoConnectOnDiscover);
        Assert.False(settings.ForceReconnect);
        Assert.Equal(5, settings.MaxReconnectAttempts);
        Assert.Equal("system", settings.UiLanguage);
        Assert.Equal("system", settings.Theme);
        Assert.Null(settings.Validate());
    }
    [Fact]
    public void ThemeDefaultsToSystemAndValidatesSupportedModes()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"schema_version":2}""", AppSettings.JsonOptions)!;
        Assert.Equal("system", settings.Theme);
        Assert.Null(settings.Validate());
        settings.Theme = "dark"; Assert.Null(settings.Validate());
        settings.Theme = "light"; Assert.Null(settings.Validate());
        settings.Theme = "invalid"; Assert.Equal(L.Get("Select a valid theme."), settings.Validate());
    }
    [Fact]
    public void DraftMergePreservesConcurrentVolumeAndAppliesOnlyEditedFields()
    {
        var baseline = new AppSettings(); baseline.Options("a").Volume = 50;
        var draft = baseline.Clone(); draft.ForceReconnect = false; draft.Options("a").Hidden = true;
        var live = baseline.Clone(); live.MasterVolume = 30; live.Options("a").Volume = 80; live.LastReceiverId = "a";
        var merged = SettingsMerge.Merge(baseline, draft, live);
        Assert.Equal(30, merged.MasterVolume); Assert.Equal(80, merged.ReadOptions("a").Volume);
        Assert.True(merged.ReadOptions("a").Hidden); Assert.False(merged.ForceReconnect); Assert.Equal("a", merged.LastReceiverId);
    }
    [Fact]
    public void EditingOneNewlyDiscoveredCardDoesNotCreateNullOverridesForOtherCards()
    {
        var live = new AppSettings(); var baseline = live.Clone(); baseline.Options("a"); baseline.Options("b");
        var draft = baseline.Clone(); draft.Options("a").Hidden = true;
        var merged = SettingsMerge.Merge(baseline, draft, live);
        Assert.Null(merged.Validate()); Assert.True(merged.ReadOptions("a").Hidden); Assert.False(merged.Receivers.ContainsKey("b"));
    }
    [Fact]
    public void OldConfigMigrationPreservesSemanticsAndBacksUpBeforeSave()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "config.json");
        const string old = """{"capture_mode":"virtual","virtual_output_device":"Old device","custom_buffer_ms":4000,"receivers":{"one":{"auto_connect":false,"custom_buffer_ms":null},"two":{"auto_connect":true}},"show_equalizer":true}""";
        try
        {
            File.WriteAllText(path, old); var store = new SettingsStore(path); var settings = store.Load();
            Assert.Null(settings.ReadOptions("one").AutoConnect); Assert.True(settings.ReadOptions("two").AutoConnect);
            Assert.Equal("endpoint", settings.CaptureMode); Assert.Equal("Old device", settings.CaptureEndpoint); Assert.Equal(2000, settings.CustomBufferMs);
            Assert.Equal(old, File.ReadAllText(path)); store.Save(settings);
            Assert.Equal(old, File.ReadAllText(path + ".pre-wpf.bak")); Assert.Equal(2, new SettingsStore(path).Load().SchemaVersion);
            Assert.True(settings.Extra!["show_equalizer"].GetBoolean());
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void NewExplicitDisabledAutoconnectSurvivesReload()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "config.json");
        try { var s = new AppSettings(); s.Options("r").AutoConnect = false; new SettingsStore(path).Save(s); Assert.False(new SettingsStore(path).Load().ReadOptions("r").AutoConnect); }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public void ManualReceiversAndNullableOverridesRoundTrip()
    {
        var s = new AppSettings(); s.Options("id"); s.ManualReceivers.Add(new("id", "客厅", "homepod.local", 7000));
        var clone = s.Clone(); Assert.Null(clone.Validate()); Assert.Null(clone.ReadOptions("id").CustomBufferMs); Assert.Equal(s.ManualReceivers[0], clone.ManualReceivers[0]);
    }
    [Fact]
    public void GainUsesOnlyMasterVolume()
    {
        var settings = new AppSettings { MasterVolume = 40 }; settings.Options("a").Volume = 25;
        Assert.Equal(.4, settings.Gain("a")); Assert.Equal(.4, settings.Gain("b"));
    }
    [Theory]
    [InlineData("realtime", 120)]
    [InlineData("normal", 200)]
    [InlineData("buffered", 500)]
    [InlineData("custom", 750)]
    public void TargetLatencyUsesNativeNotLegacyBuffers(string mode, int expected)
    {
        var settings = new AppSettings { LatencyMode = mode, CustomBufferMs = 750 }; Assert.Equal(expected, settings.Latency("r"));
        settings.Options("r").LatencyMode = "custom"; settings.Options("r").CustomBufferMs = 900; Assert.Equal(900, settings.Latency("r"));
    }
    [Fact]
    public void CustomLatencyAllowsZero()
    {
        var settings = new AppSettings { LatencyMode = "custom", CustomBufferMs = 0 };
        settings.Options("r").LatencyMode = "custom"; settings.Options("r").CustomBufferMs = 0;
        Assert.Null(settings.Validate());
        Assert.Equal(0, settings.Latency("r"));
    }
    [Theory]
    [InlineData("44100", true)]
    [InlineData("48000", true)]
    [InlineData("96000", false)]
    [InlineData("auto", false)]
    public void StreamSampleRateAcceptsOnlySupportedRates(string rate, bool valid)
    {
        var settings = new AppSettings { StreamSampleRate = rate };
        Assert.Equal(valid, settings.Validate() is null);
    }
    [Fact]
    public void MissingStreamSampleRateDefaultsTo44100()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"schema_version":2}""", AppSettings.JsonOptions)!;
        Assert.Equal("44100", settings.StreamSampleRate);
        Assert.Null(settings.Validate());
    }
    [Fact]
    public void LoopbackDoesNotReuseStaleSelectedEndpoint()
    {
        var settings = new AppSettings { CaptureEndpoint = "old" }; Assert.Null(settings.EffectiveEndpoint);
        settings.CaptureMode = "endpoint"; Assert.Equal("old", settings.EffectiveEndpoint);
    }
    [Fact]
    public void JsonNullMetricIsUnavailableNotAStreamFailure()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""{"capture_to_send_p95_ms":null,"bad":"no"}""");
        Assert.Null(json.Number("capture_to_send_p95_ms")); Assert.Null(json.Number("bad"));
    }
    [Theory]
    [InlineData(1)]
    [InlineData(1.5)]
    [InlineData(2)]
    public void PopupFitsNegativeMonitorWorkAreaAndAvoidsTaskbar(double scale)
    {
        var work = new PixelRect(-1920, -100, 0, 940); var result = PopupPlacement.Place(work, scale, 520, 1200);
        Assert.True(result.Left >= work.Left); Assert.True(result.Bottom < work.Bottom); Assert.True(result.Height <= work.Height * .7);
        Assert.Equal(work.Right - (int)Math.Round(12 * scale), result.Right);
    }
    [Fact]
    public void FutureSchemaIsNotSilentlyDowngraded()
    {
        var directory = NewDirectory(); var path = Path.Combine(directory, "config.json");
        try
        {
            File.WriteAllText(path, "{\"schema_version\":99}");
            Assert.Throws<NotSupportedException>(() => new SettingsStore(path).Load());
            Assert.Equal("{\"schema_version\":99}", File.ReadAllText(path));
        }
        finally { Directory.Delete(directory, true); }
    }
    private static string NewDirectory() { var path = Path.Combine(Path.GetTempPath(), "airplay-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
}
