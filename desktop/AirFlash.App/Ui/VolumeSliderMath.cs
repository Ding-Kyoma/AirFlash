namespace AirFlash.App.Ui;

internal static class VolumeSliderMath
{
    internal static double ValueFromPosition(double position, double trackWidth, double thumbWidth, double minimum, double maximum)
    {
        var travel = trackWidth - thumbWidth;
        if (travel <= 0 || maximum <= minimum) return minimum;
        var ratio = Math.Clamp((position - thumbWidth / 2) / travel, 0, 1);
        return Math.Clamp(Math.Round(minimum + ratio * (maximum - minimum), MidpointRounding.AwayFromZero), minimum, maximum);
    }
}
