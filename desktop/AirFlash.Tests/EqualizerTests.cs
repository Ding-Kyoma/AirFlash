using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class EqualizerTests
{
    [Fact]
    public void DefaultsAndLegacyConfigurationRemainFlatAndDisabled()
    {
        foreach (var json in new[] { "{}", "{\"schema_version\":2}", "{\"equalizer\":null}" })
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
            Assert.False(settings.Equalizer.Enabled); Assert.Equal(0, settings.Equalizer.PreampDb);
            Assert.Equal(new double[10], settings.Equalizer.BandGainsDb); Assert.Null(settings.Validate());
        }
    }
    [Fact]
    public void EqualizerRoundTripsAndCopyPreservesBoundObjectAndUnknownFields()
    {
        var source = JsonSerializer.Deserialize<AppSettings>("""{"equalizer":{"enabled":true,"preamp_db":-2.5,"band_gains_db":[6,4,2,0,0,0,0,0,0,0],"future":42},"future_option":true}""", AppSettings.JsonOptions)!;
        var target = new AppSettings(); var bound = target.Equalizer;
        target.CopyFrom(source.Clone());
        Assert.Same(bound, target.Equalizer); Assert.True(SettingsMerge.Equal(source, target));
        source.Equalizer.SetBand(0, 12); source.Equalizer.Extra!.Clear();
        Assert.Equal(6, bound.BandGainsDb[0]); Assert.Equal(42, bound.Extra!["future"].GetInt32());
        Assert.True(target.Extra!["future_option"].GetBoolean());
        var external = bound.BandGainsDb; external[0] = 99; Assert.Equal(6, bound.BandGainsDb[0]);
    }
    [Fact]
    public void EqualizerMergePreservesConcurrentPanelAndUneditedEqualizerFields()
    {
        var baseline = new AppSettings(); var draft = baseline.Clone(); var current = baseline.Clone();
        draft.Equalizer.Enabled = true; draft.Equalizer.SetBand(0, 6);
        current.MasterVolume = 23; current.Equalizer.PreampDb = -3;
        var result = SettingsMerge.Merge(baseline, draft, current);
        Assert.Equal(23, result.MasterVolume); Assert.Equal(-3, result.Equalizer.PreampDb);
        Assert.True(result.Equalizer.Enabled); Assert.Equal(6, result.Equalizer.BandGainsDb[0]);
    }
    [Fact]
    public void InvalidGainsAndWrongBandCountAreRejected()
    {
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, -12.5, 12.5 })
        {
            var eq = new EqualizerSettings { PreampDb = value }; Assert.NotNull(eq.Validate());
            eq.PreampDb = 0; eq.SetBand(5, value); Assert.NotNull(eq.Validate());
        }
        foreach (var length in new[] { 0, 9, 11 }) Assert.NotNull(new EqualizerSettings { BandGainsDb = new double[length] }.Validate());
        Assert.Null(new EqualizerSettings { PreampDb = -12, BandGainsDb = Enumerable.Repeat(12d, 10).ToArray() }.Validate());
    }
    [Theory]
    [InlineData(44100)]
    [InlineData(48000)]
    public void HeadroomMatchesNativeReferenceAndDisabledRemainsNeutral(int rate)
    {
        var settings = new EqualizerSettings { Enabled = true, PreampDb = 3 };
        settings.SetBand(5, 6);
        var result = EqualizerResponse.Headroom(settings, rate);
        Assert.Equal(-9, result.AutoAttenuation, 7); Assert.Equal(-6, result.EffectivePreamp, 7);
        settings.Enabled = false; Assert.Equal((0d, 0d), EqualizerResponse.Headroom(settings, rate));
        settings = new() { Enabled = true }; Assert.Equal((0d, 0d), EqualizerResponse.Headroom(settings, rate));
    }
    [Fact]
    public async Task SavedEqualizerPersistsAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), "airflash-equalizer-tests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "config.json");
        try
        {
            var settings = new AppSettings(); settings.Equalizer.Enabled = true; settings.Equalizer.SetBand(9, -4.5);
            ISettingsStore store = new SettingsStore(path); await store.SaveAsync(settings);
            Assert.True(SettingsMerge.Equal(settings, new SettingsStore(path).Load()));
            Assert.Empty(Directory.GetFiles(directory, "*.tmp"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
