using System.Text.Json;
using AirFlash.Core;
using Xunit;
namespace AirFlash.Tests;

public sealed class LocalizationTests
{
    [Theory]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-TW", "zh")]
    [InlineData("zh-HK", "zh")]
    [InlineData("ZH-hans", "zh")]
    [InlineData("en-GB", "en")]
    [InlineData("ja-JP", "en")]
    [InlineData("", "en")]
    public void SelectsSupportedLanguageOrEnglishFallback(string preference, string expected)
        => Assert.Equal(expected, L.SelectLanguage([preference]));

    [Fact]
    public void RespectsUserPreferenceOrder()
    {
        Assert.Equal("en", L.SelectLanguage(["en-US", "zh-CN"]));
        Assert.Equal("zh", L.SelectLanguage(["fr-FR", "zh-TW", "en-US"]));
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
}
