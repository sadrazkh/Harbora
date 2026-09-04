using Harbora.Application.Abstractions;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The env an app attached to a primary receives for its primary's own read replica, if it has one
/// running (3.2, round-2 market-gaps plan) — the replica-side mirror of
/// <see cref="ManagedServiceAttachEnv"/>, one attachment kind later, and deliberately NOT a second
/// thing an app attaches to on its own: a replica never gets its own <see cref="AppManagedService"/>
/// row (see <see cref="ManagedService.PrimaryManagedServiceId"/>'s own doc for why), it only ever
/// rides along with the attachment its primary already has.
///
/// <para>
/// <c>REPLICA_URL</c> — never <c>DATABASE_URL</c> a second time — is the whole point: the plan's own
/// requirement is that this connection be unmistakably read-only in the variable name an application
/// actually reads, not merely documented as such on a page nobody's deploy pipeline looks at.
/// <see cref="AttachKeys.PrefixFor"/> gives it the alias-prefixed twin every other attach kind already
/// has, so a second attached database's replica cannot collide with the first's.
/// </para>
///
/// <para>
/// Built from the SAME resolved credentials <see cref="ManagedServiceAttachEnv"/> already worked out
/// for the primary side of this exact attachment — the login, password and logical database name a
/// physical replica of the primary necessarily also has, since it is a byte-for-byte copy of the same
/// data directory — with only the host swapped to the replica's own container. There is no second
/// credential to look up: a replica does not have its own login story, it inherits the primary's
/// whole one.
/// </para>
/// </summary>
public static class ReplicaAttachEnv
{
    /// <param name="primaryCreds">
    /// What <see cref="ManagedServiceAttachEnv.EntriesFor"/> already resolved for this same
    /// attachment's primary side — same user, password and database, only the host differs.
    /// </param>
    /// <param name="replica">The primary's running replica.</param>
    /// <param name="alias">The attachment's own alias — the same one every other key from this
    /// attachment is already prefixed with.</param>
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(
        ServiceCreds primaryCreds, ManagedService replica, string alias, ISecretProtector protector)
    {
        var replicaCreds = primaryCreds with { Host = replica.ContainerName };
        var url = $"postgresql://{replicaCreds.User}:{replicaCreds.Password}@{replicaCreds.Host}:{replicaCreds.Port}/{replicaCreds.Database}";
        var stored = protector.Protect(url);
        var prefix = AttachKeys.PrefixFor(alias);

        return [("REPLICA_URL", stored, true), ($"{prefix}REPLICA_URL", stored, true)];
    }
}
