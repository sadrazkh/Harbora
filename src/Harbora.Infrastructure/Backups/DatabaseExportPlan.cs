namespace Harbora.Infrastructure.Backups;

/// <summary>
/// The decision behind the self-serve "export" button on a database's own page — sub-project 10.
///
/// <para>
/// A self-serve export is not a retained backup: it exists so a customer can download one copy of
/// their data, not so the platform keeps it. <see cref="ArtifactLifetime"/> is how long the produced
/// <c>Backup</c> row (and the artifact it names) is kept before <c>BackupEngine.EnforceRetentionAsync</c>
/// removes it — separate from, and longer than, <c>AdminerSession.Lifetime</c>, which only bounds one
/// minted download link. The two spans answer different questions: the artifact's window is "how long
/// do we keep this customer's data around for them to fetch", the link's window is "how long is one
/// URL good for" — a customer whose first link lapses can mint another against the same artifact
/// without re-running the export, right up until the artifact itself expires.
/// </para>
/// </summary>
public static class DatabaseExportPlan
{
    /// <summary>How long a self-serve export's artifact is kept before it is swept away unread.</summary>
    public static readonly TimeSpan ArtifactLifetime = TimeSpan.FromHours(24);
}
