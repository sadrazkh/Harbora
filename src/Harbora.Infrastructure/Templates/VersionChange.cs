using Harbora.Domain.Templates;

namespace Harbora.Infrastructure.Templates;

/// <summary>What moving a running service between versions involves.</summary>
/// <param name="Allowed">False when the move must not happen at all.</param>
/// <param name="Reason">Why it was refused.</param>
/// <param name="IsDowngrade">True when moving to an older release.</param>
/// <param name="Notes">Upgrade notes for every version being crossed, oldest first.</param>
/// <param name="Warnings">Migration warnings — the things that can lose data.</param>
public sealed record VersionChangePlan(
    bool Allowed,
    string? Reason,
    bool IsDowngrade,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Moving a deployed service from one version to another.
///
/// The rule that matters is the one about skipped versions. Upgrading from 14 to 17 crosses 15 and
/// 16, and their migration notes apply just as much as the destination's — a person who reads only
/// the last one has read the least important third of what will happen to their data.
/// </summary>
public static class VersionChange
{
    public static VersionChangePlan Plan(
        AppTemplateVersion from,
        AppTemplateVersion to,
        IEnumerable<AppTemplateVersion> allVersions,
        string? nodeArchitecture = null,
        bool preferPersian = false)
    {
        if (from.Id == to.Id)
            return new VersionChangePlan(false, "It is already on that version.", false, [], []);

        if (VersionSelection.Refuse(to, nodeArchitecture) is { } refusal)
            return new VersionChangePlan(false, refusal.Reason, false, [], []);

        var ordered = allVersions
            .OrderBy(v => v.ReleasedAt ?? DateTimeOffset.MinValue)
            .ToList();

        var fromIndex = ordered.FindIndex(v => v.Id == from.Id);
        var toIndex = ordered.FindIndex(v => v.Id == to.Id);

        // A version that is not in the list cannot be ordered against, so the direction is unknown.
        // Treated as an upgrade with no crossed notes rather than guessed at.
        if (fromIndex < 0 || toIndex < 0)
            return new VersionChangePlan(true, null, false, Notes(to, preferPersian), Warnings(to, preferPersian));

        var isDowngrade = toIndex < fromIndex;

        if (isDowngrade && !to.AllowsDowngrade)
            return new VersionChangePlan(false,
                "That version does not support going back to it. Restore from a backup instead.",
                true, [], []);

        // Every version stepped over, not just the destination.
        var crossed = isDowngrade
            ? ordered.Skip(toIndex).Take(fromIndex - toIndex).ToList()
            : ordered.Skip(fromIndex + 1).Take(toIndex - fromIndex).ToList();

        return new VersionChangePlan(
            true, null, isDowngrade,
            crossed.SelectMany(v => Notes(v, preferPersian)).ToList(),
            crossed.SelectMany(v => Warnings(v, preferPersian)).ToList());
    }

    private static IReadOnlyList<string> Notes(AppTemplateVersion v, bool fa)
    {
        var text = fa && !string.IsNullOrWhiteSpace(v.UpgradeNotesFa) ? v.UpgradeNotesFa : v.UpgradeNotes;
        return string.IsNullOrWhiteSpace(text) ? [] : [$"{v.Version}: {text}"];
    }

    private static IReadOnlyList<string> Warnings(AppTemplateVersion v, bool fa)
    {
        var text = fa && !string.IsNullOrWhiteSpace(v.MigrationWarningsFa) ? v.MigrationWarningsFa : v.MigrationWarnings;
        return string.IsNullOrWhiteSpace(text) ? [] : [$"{v.Version}: {text}"];
    }
}
