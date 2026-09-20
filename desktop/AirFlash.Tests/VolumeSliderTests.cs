using AirFlash.App.Ui;
using Xunit;

namespace AirFlash.Tests;

public class VolumeSliderTests
{
    [Theory]
    [InlineData(-10, 0)]
    [InlineData(0, 0)]
    [InlineData(10, 0)]
    [InlineData(60, 25)]
    [InlineData(110, 50)]
    [InlineData(160, 75)]
    [InlineData(210, 100)]
    [InlineData(220, 100)]
    [InlineData(230, 100)]
    [InlineData(111, 51)]
    public void ClickMapsToThumbTravelAndWholePercent(double position, double expected)
        => Assert.Equal(expected, VolumeSliderMath.ValueFromPosition(position, 220, 20, 0, 100));

    [Theory]
    [InlineData(0, 20)]
    [InlineData(20, 20)]
    public void UnlaidOutTrackReturnsMinimum(double width, double thumbWidth)
        => Assert.Equal(0, VolumeSliderMath.ValueFromPosition(10, width, thumbWidth, 0, 100));
}
