using System.Globalization;
namespace AirFlash.Core;

/// <summary>How much AirPlay 2 evidence a receiver's mDNS TXT record carries.</summary>
public enum AirPlaySupport
{
    /// <summary>No usable feature bitmap: never present this as classic.</summary>
    Unknown,
    /// <summary>The feature bitmap is present but AirPlay 2 is not advertised at a supported version.</summary>
    ClassicOnly,
    /// <summary>Bit 40 is set and <c>srcvers</c> is at least 354.54.6.</summary>
    AirPlay2
}

/// <summary>
/// The TXT half of WinAirCast's two-stage capability model (QUESTIONS Q18/Q29).
/// AirPlay 2 is decided by mDNS TXT alone; model/name decides PTP vs NTP in the
/// engine and must not influence this classification.
/// </summary>
public static class AirPlayCapability
{
    /// <summary>Feature bit 40: supports AirPlay 2, including buffered audio.</summary>
    public const ulong AirPlay2Feature = 1UL << 40;
    public static readonly Version MinimumVersion = new(354, 54, 6);

    /// <summary>WAC's predicate: bit 40 set and <c>srcvers</c> at least 354.54.6.</summary>
    public static bool SupportsAirPlay2(ulong? features, string? sourceVersion)
        => features is { } flags && (flags & AirPlay2Feature) != 0 && VersionAtLeast(sourceVersion, MinimumVersion);

    /// <summary>
    /// Three-way view for the receiver list. A missing feature bitmap is unknown,
    /// not classic. A device that sets bit 40 but omits an unparsable <c>srcvers</c>
    /// cannot be confirmed as AirPlay 2 either, so it stays unknown rather than
    /// being asserted classic.
    /// </summary>
    public static AirPlaySupport Classify(ulong? features, string? sourceVersion)
    {
        if (features is not { } flags) return AirPlaySupport.Unknown;
        if ((flags & AirPlay2Feature) == 0) return AirPlaySupport.ClassicOnly;
        if (VersionAtLeast(sourceVersion, MinimumVersion)) return AirPlaySupport.AirPlay2;
        return TryParseVersion(sourceVersion, out _) ? AirPlaySupport.ClassicOnly : AirPlaySupport.Unknown;
    }

    public static bool VersionAtLeast(string? text, Version minimum)
        => TryParseVersion(text, out var version) && version >= minimum;

    /// <summary>
    /// Parses the dotted numeric <c>srcvers</c> form. Two to four components follow
    /// <see cref="Version"/>; components beyond the fourth are tolerated and
    /// ignored, whitespace around components is tolerated, and anything else is
    /// not a version. A single component is rejected because the TXT value is a
    /// version, not an integer.
    /// </summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Trim().Split('.');
        if (parts.Length < 2) return false;
        var numbers = new int[Math.Min(parts.Length, 4)];
        for (var i = 0; i < numbers.Length; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        version = numbers.Length switch
        {
            2 => new Version(numbers[0], numbers[1]),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3])
        };
        return true;
    }
}
