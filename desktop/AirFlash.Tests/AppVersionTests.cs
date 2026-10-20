using AirFlash.Core;
using Xunit;

namespace AirFlash.Tests;

public class AppVersionTests
{
    private static readonly Version Assembly = new(0, 4, 0, 0);

    [Theory]
    [InlineData("0.4.0", "AirFlash 0.4.0 · WPF", false, false)]
    [InlineData("0.4.0-rc.1", "AirFlash 0.4.0-rc.1 · Preview · WPF", true, false)]
    [InlineData("0.4.0-dev.3f2a9c1", "AirFlash 0.4.0-dev.3f2a9c1 · WPF", false, true)]
    [InlineData("0.4.0-dev.3f2a9c1.dirty+abcdef0", "AirFlash 0.4.0-dev.3f2a9c1.dirty · WPF", false, true)]
    public void FormatsReleasePreviewAndDevelopmentVersions(
        string informational, string label, bool preview, bool development)
    {
        Assert.Equal(label, AppVersion.Label(informational, Assembly));
        Assert.Equal(preview, AppVersion.IsPreview(informational, Assembly));
        Assert.Equal(development, AppVersion.IsDevelopment(informational, Assembly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingInformationalVersionFallsBackToNumericAssemblyVersion(string? informational)
    {
        Assert.Equal("0.4.0", AppVersion.Normalize(informational, Assembly));
    }
}
