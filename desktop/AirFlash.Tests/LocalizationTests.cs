using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh-TW")]
    [InlineData("zh-HK", "zh-TW")]
    [InlineData("zh-MO", "zh-TW")]
    [InlineData("ZH-hans", "zh")]
    [InlineData("de-DE", "de")]
    [InlineData("de-AT", "de")]
    [InlineData("de-CH", "de")]
    [InlineData("DE", "de")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("", "en")]
    public void SelectsSupportedLanguageOrEnglishFallback(string preference, string expected)
        => Assert.Equal(expected, L.SelectLanguage([preference]));

    [Fact]
    public void RespectsUserPreferenceOrder()
    {
        Assert.Equal("en", L.SelectLanguage(["en-US", "zh-CN"]));
        Assert.Equal("zh-TW", L.SelectLanguage(["fr-FR", "zh-TW", "en-US"]));
        Assert.Equal("zh", L.SelectLanguage(["fr-FR", "zh-CN", "en-US"]));
        Assert.Equal("de", L.SelectLanguage(["fr-FR", "de-CH", "en-US"]));
        Assert.Equal("en", L.SelectLanguage(["en-US", "de-DE"]));
        Assert.Equal("en", L.SelectLanguage([]));
    }

    [Fact]
    public void EnglishWorksWithoutInitializationAndUnknownMessagesRemainIntact()
    {
        Assert.Equal("Settings…", L.Get("Settings…"));
        Assert.Equal("Unknown engine detail", L.Get("Unknown engine detail"));
        Assert.Equal("HomePod stereo · 2/2", L.Format("HomePod stereo · {0}/2", 2));
    }

    [Fact]
    public void EmbeddedChineseCatalogPreservesFormatArguments()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Assert.Equal("设置…", translations["Settings…"]);
        foreach (var (english, chinese) in translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(chinese));
            Assert.Equal(System.Text.CompositeFormat.Parse(english).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(chinese).MinimumArgumentCount);
        }
    }

    [Fact]
    public void EmbeddedTraditionalChineseCatalogPreservesFormatArguments()
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh-TW.json")!;
        var translations = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
        Assert.Equal("設定…", translations["Settings…"]);
        foreach (var (english, traditional) in translations)
        {
            Assert.False(string.IsNullOrWhiteSpace(traditional));
            Assert.Equal(System.Text.CompositeFormat.Parse(english).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(traditional).MinimumArgumentCount);
        }
    }

    [Fact]
    public void GermanCatalogMatchesEnglishCatalogAndPreservesFormatArguments()
    {
        using var englishStream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.zh.json")!;
        using var germanStream = typeof(L).Assembly.GetManifestResourceStream("AirFlash.Core.Strings.de.json")!;
        var baseline = JsonSerializer.Deserialize<Dictionary<string, string>>(englishStream)!;
        var german = JsonSerializer.Deserialize<Dictionary<string, string>>(germanStream)!;
        Assert.Equal(baseline.Keys.Order(), german.Keys.Order());
        Assert.Equal("Einstellungen…", german["Settings…"]);
        foreach (var (key, translated) in german)
        {
            Assert.False(string.IsNullOrWhiteSpace(translated));
            Assert.Equal(System.Text.CompositeFormat.Parse(key).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(translated).MinimumArgumentCount);
        }
    }

    [Fact]
    public void GermanIsUsedByTheRuntime()
    {
        try
        {
            L.Initialize(["de-DE"]);
            Assert.Equal("Einstellungen…", L.Get("Settings…"));
            Assert.Equal("HomePod · Ziel 2 ms", L.Format("{0} · target {1} ms", "HomePod", 2));
        }
        finally { L.Initialize(["en-US"]); }
    }
}
