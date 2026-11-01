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
    private static IEnumerable<Receiver> Targets(Receiver receiver) => receiver.IsSoftwareGroup ? receiver.Peers : [receiver];
    public static bool Forced(AppSettings settings, Receiver receiver) => Targets(receiver).Any(r => settings.ForcesLatency(r.Id));
    public static string? ForceConflict(AppSettings settings, Receiver receiver)
    {
        if (!receiver.IsSoftwareGroup || !Forced(settings, receiver)) return null;
        var members = receiver.Peers.ToArray();
        return members.All(r => settings.ForcesLatency(r.Id)) && members.Select(r => settings.Latency(r.Id)).Distinct().Count() == 1
            ? null : L.Get("Forced latency requires every stereo member to use forced mode with the same target. Update the receiver settings.");
    }
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

/// <summary>
/// Watches the engine's p99 send lateness for a chronic problem the recovery count
/// cannot see: a stream that never trips the re-anchor budget but is late on almost
/// every packet still sounds rough, and "three recoveries in a minute" never fires.
/// </summary>
/// <remarks>
/// The engine reports a percentile every metrics tick, so this counts consecutive
/// ticks above the threshold: one spike is a hiccup, ten in a row is a lead that is
/// too tight for this link.
/// </remarks>
public sealed class LatenessWatch(double thresholdMs = 3.0, int samples = 10)
{
    private int _streak;
    /// <summary>Feed one sample. Returns true at most once per streak.</summary>
    public bool Observe(double? p99Ms)
    {
        if (p99Ms is not { } value || !double.IsFinite(value) || value < thresholdMs)
        {
            _streak = 0;
            return false;
        }
        if (++_streak < samples) return false;
        // Keep the streak pending so a lead change that does not help triggers again
        // after another full window, instead of firing on every tick.
        _streak = 0;
        return true;
    }
    public void Reset() => _streak = 0;
    public int Streak => _streak;
}
