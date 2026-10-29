using System.Text.Encodings.Web;
using System.Text.Json;
namespace AirFlash.Core;

/// <summary>One saved pair split by the Q45 one-time migration.</summary>
public sealed record MixedGroupSplit(string GroupId, string GroupName, string Left, string Right);

/// <summary>
/// Outcome of one migration pass. <see cref="Changed"/> is true when a group was
/// split or a homogeneous legacy pair was discharged, so the caller knows it has
/// progress to persist.
/// </summary>
public sealed record MixedGroupMigrationReport(IReadOnlyList<MixedGroupSplit> Splits, bool Changed)
{
    public static MixedGroupMigrationReport None { get; } = new([], false);
}

/// <summary>
/// One-time migration of configurations written before the mixed-group invariant
/// (QUESTIONS Q45). The two situations that look alike are handled oppositely:
///
/// <list type="bullet">
/// <item>A group saved by an older build was legal under rules that no longer
/// exist. This is a <em>migration</em>: it may be split automatically, once,
/// deterministically, and is logged as <c>mixed_group_migrated</c>. The schema
/// version gates it, and each group records the invariant version it was
/// discharged against, so a second pass is a no-op even if the save was lost.</item>
/// <item>A group saved under the current rules whose device TXT capability
/// changed later is <em>runtime drift</em>. This method never touches it: the
/// group keeps its saved shape and is only marked unusable at resolution, with
/// an explicit user action to split it.</item>
/// </list>
///
/// Evidence only exists once discovery has run, which is why the caller runs
/// this on the catalog rather than inside <see cref="SettingsStore"/>. A member
/// without TXT (offline, manual, unparsable <c>srcvers</c>) is
/// <see cref="Ap2Evidence.Unknown"/>; the group is left untouched and retried
/// when evidence arrives. Absence of evidence is never a split reason.
/// </summary>
public static class MixedGroupMigration
{
    private static readonly JsonSerializerOptions LogJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// True when every saved pair has been checked (or split) against the
    /// invariant. Only then may the store persist the current schema version;
    /// until then the file stays one version back so a crash cannot lose the
    /// migration.
    /// </summary>
    public static bool IsSettled(AppSettings settings)
        => settings.SoftwareGroups.All(group => group.MixedInvariantVersion >= AppSettings.CurrentSchemaVersion);

    public static MixedGroupMigrationReport Apply(AppSettings settings, IEnumerable<Receiver> catalog, Action<string>? log = null)
    {
        var evidence = (catalog as IReadOnlyCollection<Receiver> ?? catalog.ToArray()).ToList();
        // Manual receivers never advertise TXT, so their capability is Unknown
        // forever. Include them so a legacy pair containing one can be discharged
        // instead of keeping the file at the pre-migration version for good.
        evidence.AddRange(settings.ManualReceivers.Select(receiver => new Receiver(receiver.Id, receiver.Name, receiver.Host, receiver.Port) { IsManual = true }));
        var splits = new List<MixedGroupSplit>();
        var changed = false;
        foreach (var group in settings.SoftwareGroups.ToArray())
        {
            if (group.MixedInvariantVersion >= AppSettings.CurrentSchemaVersion) continue;
            var left = SoftwareStereo.Member(group.LeftId, evidence, settings);
            var right = SoftwareStereo.Member(group.RightId, evidence, settings);
            // A manual member can never supply the evidence that would prove a
            // contradiction; no split can ever be justified, so the pair is
            // representable under the new invariant and the check is discharged.
            if (left.IsManual || right.IsManual)
            {
                group.MixedInvariantVersion = AppSettings.CurrentSchemaVersion;
                changed = true;
                continue;
            }
            // No evidence (offline / no TXT / no srcvers) is not a contradiction.
            // Leave the marker at 0 so a later discovery pass can still migrate it.
            if (!left.Online || !right.Online) continue;
            var leftEvidence = AirPlayEvidence.Of(left);
            var rightEvidence = AirPlayEvidence.Of(right);
            if (leftEvidence is Ap2Evidence.Unknown || rightEvidence is Ap2Evidence.Unknown) continue;
            if (!AirPlayEvidence.Contradict(leftEvidence, rightEvidence))
            {
                // Proven homogeneous under the old rules: discharge the check so a
                // later TXT change is drift, not migration, and can never be split
                // automatically.
                group.MixedInvariantVersion = AppSettings.CurrentSchemaVersion;
                changed = true;
                continue;
            }
            var groupName = group.Name;
            if (!SoftwareStereo.Split(settings, group.Id)) continue;
            if (settings.LastReceiverId == group.Id) settings.LastReceiverId = null;
            var split = new MixedGroupSplit(group.Id, groupName, left.Name, right.Name);
            splits.Add(split);
            changed = true;
            log?.Invoke($"mixed_group_migrated: {Json(left.Name, right.Name)} → {Json(left.Name)}, {Json(right.Name)} (group {Quote(groupName)})");
        }
        return new(splits, changed);
    }

    private static string Json(params string[] values) => JsonSerializer.Serialize(values, LogJson);
    private static string Quote(string value) => JsonSerializer.Serialize(value, LogJson);
}
