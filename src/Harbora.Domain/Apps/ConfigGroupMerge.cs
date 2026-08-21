namespace Harbora.Domain.Apps;

/// <summary>Where an effective environment row's value actually came from.</summary>
public enum ConfigSource
{
    /// <summary>The app's own <see cref="EnvironmentVariable"/> row.</summary>
    App,

    /// <summary>A <see cref="ConfigGroup"/> attached to the app.</summary>
    Group,

    /// <summary>A <see cref="Harbora.Domain.Storage.StorageBucket"/> attached to the app (F5,
    /// 2026-08-21 functions-and-services plan).</summary>
    Bucket,

    /// <summary>A <see cref="Harbora.Domain.Email.EmailProvider"/> attached to the app (F6,
    /// 2026-08-21 functions-and-services plan).</summary>
    EmailProvider
}

/// <summary>
/// One row of an app's effective environment — a merged value whose origin is always attached,
/// because a merge that hides where a value came from is a debugging trap (Sub-project 9,
/// 2026-08-20 platform-options plan). <paramref name="SourceBucketId"/>/<paramref name="SourceBucketName"/>
/// are set only when <see cref="Source"/> is <see cref="ConfigSource.Bucket"/>, mirroring how
/// <paramref name="SourceGroupId"/>/<paramref name="SourceGroupName"/> are set only for
/// <see cref="ConfigSource.Group"/>; likewise <paramref name="SourceEmailProviderId"/>/
/// <paramref name="SourceEmailProviderName"/> only for <see cref="ConfigSource.EmailProvider"/>.
/// </summary>
public readonly record struct EffectiveEnvironmentEntry(
    string Key, string Value, bool IsSecret, ConfigSource Source,
    Guid? SourceGroupId, string? SourceGroupName,
    Guid? SourceBucketId = null, string? SourceBucketName = null,
    Guid? SourceEmailProviderId = null, string? SourceEmailProviderName = null);

/// <summary>
/// One group's contribution to a merge: its attachment order for the app in question, its identity
/// (for provenance), and its current entries.
/// </summary>
public readonly record struct AttachedGroupEntries(
    int AttachOrder, Guid GroupId, string GroupName, IReadOnlyList<ConfigGroupEntry> Entries);

/// <summary>One env var a bucket contributes — shaped like <see cref="ConfigGroupEntry"/>, but a
/// bucket's entries are computed (<see cref="Harbora.Domain.Storage.BucketEnvKeys.EntriesFor"/>)
/// rather than stored rows, so this carries plain values rather than an EF entity.</summary>
public readonly record struct BucketEnvEntry(string Key, string Value, bool IsSecret);

/// <summary>
/// One bucket's contribution to a merge: its attachment order for the app in question (F5,
/// 2026-08-21 functions-and-services plan — the same "second one wins" rule
/// <see cref="AttachedGroupEntries"/> already gives groups, since a bucket's env var names are
/// fixed and a second attach would otherwise silently overwrite the first's values), its identity
/// (for provenance), and its current entries.
/// </summary>
public readonly record struct AttachedBucketEnv(
    int AttachOrder, Guid BucketId, string BucketName, IReadOnlyList<BucketEnvEntry> Entries);

/// <summary>One env var an email provider contributes — the email-side mirror of
/// <see cref="BucketEnvEntry"/> (F6, 2026-08-21 functions-and-services plan).</summary>
public readonly record struct EmailProviderEnvEntry(string Key, string Value, bool IsSecret);

/// <summary>
/// One email provider's contribution to a merge: its attachment order for the app in question, its
/// identity (for provenance), and its current entries — the email-side mirror of
/// <see cref="AttachedBucketEnv"/> (F6, 2026-08-21 functions-and-services plan). Same "second one
/// wins" reasoning: a provider's env var names are fixed (<c>SMTP_*</c>), so a second attach would
/// otherwise silently overwrite the first one's values.
/// </summary>
public readonly record struct AttachedEmailProviderEnv(
    int AttachOrder, Guid EmailProviderId, string EmailProviderName, IReadOnlyList<EmailProviderEnvEntry> Entries);

/// <summary>
/// The single place app-over-group-over-attachment precedence is decided: <b>the deploy pipeline's
/// env assembly point</b> (<c>DeploymentPipeline.BuildEnv</c>) calls this to build what a container
/// actually receives, and the app's env page calls the exact same method to render what it will
/// receive — one merge, never two, so the container and the page can never disagree about which
/// value won.
///
/// <para>
/// Precedence: the app's own <see cref="EnvironmentVariable"/> always wins over any group, bucket or
/// email provider; among groups, the one with the higher <see cref="AttachedGroupEntries.AttachOrder"/>
/// (attached later) wins on a shared key; attached buckets and email providers are lower precedence
/// than every group — they exist to hand an app default credentials, not to override a value
/// somebody deliberately set through a group. Values are passed through unchanged — ciphertext stays
/// ciphertext — so a caller decides for itself whether and when to decrypt.
/// </para>
/// </summary>
public static class ConfigGroupMerge
{
    public static IReadOnlyList<EffectiveEnvironmentEntry> Merge(
        IEnumerable<EnvironmentVariable> ownVariables,
        IEnumerable<AttachedGroupEntries> attachedGroups,
        IEnumerable<AttachedBucketEnv>? attachedBuckets = null,
        IEnumerable<AttachedEmailProviderEnv>? attachedEmailProviders = null)
    {
        var byKey = new Dictionary<string, EffectiveEnvironmentEntry>(StringComparer.Ordinal);

        // Lowest precedence first, so a later write in this loop is the one that survives: buckets and
        // email providers in attachment order, then groups in attachment order, then the app's own
        // rows last, unconditionally on top.
        foreach (var bucket in (attachedBuckets ?? []).OrderBy(b => b.AttachOrder))
            foreach (var entry in bucket.Entries)
                byKey[entry.Key] = new EffectiveEnvironmentEntry(
                    entry.Key, entry.Value, entry.IsSecret, ConfigSource.Bucket,
                    null, null, bucket.BucketId, bucket.BucketName);

        foreach (var provider in (attachedEmailProviders ?? []).OrderBy(p => p.AttachOrder))
            foreach (var entry in provider.Entries)
                byKey[entry.Key] = new EffectiveEnvironmentEntry(
                    entry.Key, entry.Value, entry.IsSecret, ConfigSource.EmailProvider,
                    null, null, null, null, provider.EmailProviderId, provider.EmailProviderName);

        foreach (var group in attachedGroups.OrderBy(g => g.AttachOrder))
            foreach (var entry in group.Entries)
                byKey[entry.Key] = new EffectiveEnvironmentEntry(
                    entry.Key, entry.Value, entry.IsSecret, ConfigSource.Group, group.GroupId, group.GroupName);

        foreach (var v in ownVariables)
            byKey[v.Key] = new EffectiveEnvironmentEntry(v.Key, v.Value, v.IsSecret, ConfigSource.App, null, null);

        return byKey.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }
}
