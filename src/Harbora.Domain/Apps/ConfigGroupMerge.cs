namespace Harbora.Domain.Apps;

/// <summary>Where an effective environment row's value actually came from.</summary>
public enum ConfigSource
{
    /// <summary>The app's own <see cref="EnvironmentVariable"/> row.</summary>
    App,

    /// <summary>A <see cref="ConfigGroup"/> attached to the app.</summary>
    Group
}

/// <summary>
/// One row of an app's effective environment — a merged value whose origin is always attached,
/// because a merge that hides where a value came from is a debugging trap (Sub-project 9,
/// 2026-08-20 platform-options plan).
/// </summary>
public readonly record struct EffectiveEnvironmentEntry(
    string Key, string Value, bool IsSecret, ConfigSource Source, Guid? SourceGroupId, string? SourceGroupName);

/// <summary>
/// One group's contribution to a merge: its attachment order for the app in question, its identity
/// (for provenance), and its current entries.
/// </summary>
public readonly record struct AttachedGroupEntries(
    int AttachOrder, Guid GroupId, string GroupName, IReadOnlyList<ConfigGroupEntry> Entries);

/// <summary>
/// The single place app-over-group precedence is decided: <b>the deploy pipeline's env assembly
/// point</b> (<c>DeploymentPipeline.BuildEnv</c>) calls this to build what a container actually
/// receives, and the app's env page calls the exact same method to render what it will receive —
/// one merge, never two, so the container and the page can never disagree about which value won.
///
/// <para>
/// Precedence: the app's own <see cref="EnvironmentVariable"/> always wins over any group; among
/// groups, the one with the higher <see cref="AttachedGroupEntries.AttachOrder"/> (attached later)
/// wins on a shared key. Values are passed through unchanged — ciphertext stays ciphertext — so a
/// caller decides for itself whether and when to decrypt.
/// </para>
/// </summary>
public static class ConfigGroupMerge
{
    public static IReadOnlyList<EffectiveEnvironmentEntry> Merge(
        IEnumerable<EnvironmentVariable> ownVariables,
        IEnumerable<AttachedGroupEntries> attachedGroups)
    {
        var byKey = new Dictionary<string, EffectiveEnvironmentEntry>(StringComparer.Ordinal);

        // Lowest precedence first, so a later write in this loop is the one that survives: groups in
        // attachment order, then the app's own rows last, unconditionally on top.
        foreach (var group in attachedGroups.OrderBy(g => g.AttachOrder))
            foreach (var entry in group.Entries)
                byKey[entry.Key] = new EffectiveEnvironmentEntry(
                    entry.Key, entry.Value, entry.IsSecret, ConfigSource.Group, group.GroupId, group.GroupName);

        foreach (var v in ownVariables)
            byKey[v.Key] = new EffectiveEnvironmentEntry(v.Key, v.Value, v.IsSecret, ConfigSource.App, null, null);

        return byKey.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
}
