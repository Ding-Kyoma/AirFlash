using System;
namespace AirFlash.Core;

/// <summary>
/// How much audio AirFlash holds at the receiver, and how far it may be tightened.
/// </summary>
/// <remarks>
/// The lead is the one setting where a single number cannot serve every topology.
/// A solo HomePod with ALAC over PTP tolerates about 50 ms; a stereo pair needs the
/// two members' clocks to agree and starts at 120 ms. Rather than expose a slider
/// and hope, the policy derives a starting lead from the negotiated stream and a
/// ladder of candidates it may walk up (trouble) or down (a long clean window).
/// The floor is the value that was validated for that topology: going below it
/// trades an audible glitch for a few milliseconds nobody can measure.
/// </remarks>
public static class LatencyPolicy
{
    public const string Mode = "adaptive";
    public sealed record Plan(int Initial, int Floor, int[] Ladder, string Reason)
    {
        public int Ceiling => Ladder[^1];
    }
    public static Plan For(Receiver receiver, string? codec = null, bool ptp = true, int? customMs = null)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        if (receiver.IsSoftwareGroup) return new(120, 120, [120, 140, 160, 200], "software stereo");
        if (receiver.IsGroup) return new(120, 120, [120, 140, 160, 200], "homepod stereo");
        var alac = codec is not ("pcm");
        if (customMs is { } custom)
        {
            var clamped = Math.Clamp(custom, 0, 2000);
            return new(clamped, clamped, [clamped], "custom target");
        }
        return (alac, ptp) switch
        {
            // ALAC solo: validated at 50 ms; the ladder only moves upward from there.
            (true, true) => new(50, 50, [50, 60, 80, 120, 200], "homepod alac/ptp"),
            // PCM costs bandwidth but tolerates a shorter lead.
            (false, true) => new(60, 45, [45, 60, 80, 120, 200], "pcm/ptp"),
            // NTP timing is less precise than PTP, so the ladder starts higher.
            (true, false) => new(200, 120, [120, 160, 200, 300, 500], "alac/ntp"),
            (false, false) => new(160, 120, [120, 160, 200, 300, 500], "pcm/ntp"),
        };
    }
    /// <summary>Ladder step. <paramref name="trouble"/> means the stream needed
    /// recovery often enough that the current lead is too tight.</summary>
    public static int Next(int current, Plan plan, bool trouble)
    {
        var ladder = plan.Ladder;
        if (ladder.Length == 0) return current;
        if (trouble)
        {
            foreach (var candidate in ladder)
                if (candidate > current) return candidate;
            return plan.Ceiling;
        }
        for (var i = ladder.Length - 1; i >= 0; i--)
            if (ladder[i] < current && ladder[i] >= plan.Floor) return ladder[i];
        return Math.Max(current, plan.Floor);
    }
    /// <summary>Effective lead for a receiver given its negotiated profile.</summary>
    public static int Effective(AppSettings settings, Receiver receiver)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var options = settings.ReadOptions(receiver.Id);
        var mode = options.LatencyMode ?? settings.LatencyMode;
        if (mode != Mode) return settings.Latency(receiver.Id);
        var codec = options.Codec;
        var ptp = options.Timing is not ("ntp");
        return For(receiver, codec == "auto" ? null : codec, ptp).Initial;
    }
}
