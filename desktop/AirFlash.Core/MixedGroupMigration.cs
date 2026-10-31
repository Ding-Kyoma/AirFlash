namespace AirFlash.Core;

/// <summary>Legacy report shape retained for callers; upgrades no longer split pairs.</summary>
public sealed record MixedGroupSplit(string GroupId, string GroupName, string Left, string Right);

/// <summary>
/// Outcome of one migration pass. <see cref="Changed"/> reports updated version
/// markers that the caller should persist.
/// </summary>
public sealed record MixedGroupMigrationReport(IReadOnlyList<MixedGroupSplit> Splits, bool Changed)
{
    public static MixedGroupMigrationReport None { get; } = new([], false);
}

/// <summary>
/// Preserve user-authored groups while acknowledging the current schema.
/// Resolution validates known timing incompatibilities and offers a manual split;
/// discovery evidence must never cause an upgrade to delete a saved pair.
/// </summary>
public static class MixedGroupMigration
{

    /// <summary>
    /// True when every saved pair has its current schema marker.
    /// </summary>
    public static bool IsSettled(AppSettings settings)
        => settings.SoftwareGroups.All(group => group.MixedInvariantVersion >= AppSettings.CurrentSchemaVersion);

    public static MixedGroupMigrationReport Apply(AppSettings settings, IEnumerable<Receiver> catalog, Action<string>? log = null)
    {
        // Upgrades acknowledge the invariant without deleting user-authored pairs.
        var changed = false;
        foreach (var group in settings.SoftwareGroups)
        {
            if (group.MixedInvariantVersion >= AppSettings.CurrentSchemaVersion) continue;
            group.MixedInvariantVersion = AppSettings.CurrentSchemaVersion;
            changed = true;
        }
        return new([], changed);
    }
}
