using System.Globalization;
namespace AirFlash.Core;

/// <summary>
/// What the desktop actually knows about a receiver's AirPlay 2 support from its
/// mDNS TXT record. This is a labelling answer, not a routing answer: it exists
/// so "does not support AirPlay 2" and "claims AirPlay 2 but the firmware is too
/// old" never look identical to the user, even though both route down the classic
/// path. Routing is <see cref="AirPlayRouting.SupportsAirPlay2"/>.
/// </summary>
public enum AirPlayCapability
{
    /// <summary>No feature bitmap, or bit 40 set without a parsable <c>srcvers</c>: no evidence either way.</summary>
    Unknown,
    /// <summary>Bit 40 is clear: positive evidence that the device is not AirPlay 2.</summary>
    NotAirPlay2,
    /// <summary>Bit 40 is set but <c>srcvers</c> is below 354.54.6: the device claims AirPlay 2 and needs newer firmware.</summary>
    ClaimsAirPlay2Outdated,
    /// <summary>Bit 40 is set and <c>srcvers</c> is at least 354.54.6.</summary>
    AirPlay2
}

/// <summary>
/// The routing question (QUESTIONS Q36): can this receiver take the AirPlay 2
/// path? This is exactly WinAirCast's strict boolean and must stay conservative;
/// <see cref="AirPlayCapability.ClaimsAirPlay2Outdated"/> and
/// <see cref="AirPlayCapability.NotAirPlay2"/> both correctly return false.
/// </summary>
public static class AirPlayRouting
{
    /// <summary>Feature bit 40: supports AirPlay 2, including buffered audio.</summary>
    public const ulong AirPlay2Feature = 1UL << 40;
    public static readonly Version MinimumVersion = new(354, 54, 6);

    /// <summary>WAC's predicate: bit 40 set and <c>srcvers</c> at least 354.54.6.</summary>
    public static bool SupportsAirPlay2(ulong? features, string? sourceVersion)
        => features is { } flags && (flags & AirPlay2Feature) != 0 && AirPlayVersion.AtLeast(sourceVersion, MinimumVersion);
}

/// <summary>
/// The labelling question (QUESTIONS Q36): what does the desktop know? This is
/// deliberately a different decision from <see cref="AirPlayRouting"/>: a strict
/// routing boolean conflates "not supported" with "claims support, firmware too
/// old", and those have completely different remedies (replace the device versus
/// update its firmware).
/// </summary>
public static class AirPlayCapabilityClassifier
{
    public static AirPlayCapability Classify(ulong? features, string? sourceVersion)
    {
        if (features is not { } flags) return AirPlayCapability.Unknown;
        if ((flags & AirPlayRouting.AirPlay2Feature) == 0) return AirPlayCapability.NotAirPlay2;
        if (!AirPlayVersion.TryParse(sourceVersion, out var version)) return AirPlayCapability.Unknown;
        return version >= AirPlayRouting.MinimumVersion
            ? AirPlayCapability.AirPlay2
            : AirPlayCapability.ClaimsAirPlay2Outdated;
    }
}

/// <summary><c>srcvers</c> parsing shared by routing and labelling.</summary>
public static class AirPlayVersion
{
    public static bool AtLeast(string? text, Version minimum)
        => TryParse(text, out var version) && version >= minimum;

    /// <summary>
    /// Parses the dotted numeric <c>srcvers</c> form. Two to four components follow
    /// <see cref="Version"/>; components beyond the fourth are tolerated and
    /// ignored, whitespace around components is tolerated, and anything else is
    /// not a version. A single component is rejected because the TXT value is a
    /// version, not an integer.
    /// </summary>
    public static bool TryParse(string? text, out Version version)
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
