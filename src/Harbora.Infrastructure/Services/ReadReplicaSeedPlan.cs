using Harbora.Domain.Common;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// How a fresh PostgreSQL read replica's data directory is seeded from its primary (3.2, round-2
/// market-gaps plan): <c>pg_basebackup -R</c>, run once, before the replica's own container is ever
/// started.
///
/// <para>
/// <c>-R</c> is the whole trick — it writes <c>standby.signal</c> and a <c>primary_conninfo</c> GUC
/// into the copied data directory as part of the same command, which is what makes the freshly seeded
/// directory boot straight into standby/recovery mode the moment
/// <c>ManagedServiceEngine.ProvisionAsync</c> starts the container on it, with no second command and
/// no hand-written recovery configuration. <c>-Xs</c> streams WAL alongside the base copy so the
/// directory is internally consistent and startable the instant the copy finishes, rather than only
/// once continuous streaming replication (the very <c>primary_conninfo</c> <c>-R</c> just wrote) has
/// caught up from nothing — the ordinary, documented way to seed a standby, not a shortcut invented
/// for this platform.
/// </para>
///
/// <para>
/// Run against the primary's own admin login rather than a dedicated replication role: a PostgreSQL
/// superuser already has the <c>REPLICATION</c> privilege implicitly (it bypasses every ACL check,
/// including that one), and every managed PostgreSQL instance on this platform is provisioned with its
/// <c>Username</c> as exactly that — the same "hand over the instance's own admin login" trust model
/// <c>ServiceCatalog</c>'s own Meilisearch entry already documents as the trade-off every attach on
/// this platform makes today, applied here to seeding rather than to an app's attach.
/// </para>
/// </summary>
public static class ReadReplicaSeedPlan
{
    /// <summary>
    /// The seed command, given the primary's reachable address, its admin login, and where the
    /// replica's own container will mount its (currently empty) data volume.
    /// </summary>
    public static IReadOnlyList<string> SeedCommand(
        string primaryHost, int primaryPort, string adminUser, string dataMountPath) =>
        [
            "pg_basebackup",
            "-h", primaryHost, "-p", primaryPort.ToString(), "-U", adminUser,
            "-D", dataMountPath,
            "-Fp",              // plain format: writes directly into a PGDATA-shaped directory, not a tar
            "-Xs",               // stream WAL during the copy — see the type doc above
            "-R",                // write standby.signal + primary_conninfo — the whole point
            "--checkpoint=fast", // do not wait for the primary's next scheduled checkpoint
            "--no-password"
        ];

    /// <summary>The client's environment — the admin password never goes in argv, the same rule
    /// <see cref="DatabaseGrantSql.Environment"/> already states for every other client command this
    /// platform issues.</summary>
    public static IReadOnlyDictionary<string, string> Environment(string adminPassword) =>
        new Dictionary<string, string> { ["PGPASSWORD"] = adminPassword };
}

/// <summary>
/// Whether a read replica may be created right now, decided once so the create form, the queued
/// provision and a test can all ask the same question (3.2, round-2 market-gaps plan) — the same
/// "WhyRefused, checked everywhere" shape <c>RedisMemoryPolicy</c> already gives an adjacent
/// instance-level decision.
/// </summary>
public static class ReadReplicaPlan
{
    /// <param name="primary">The instance the replica would stream from.</param>
    /// <param name="replicaServerId">Where the replica would be placed.</param>
    /// <param name="replicaVersion">The image tag the replica would run.</param>
    /// <returns>Null when the replica may be created; otherwise the sentence to show.</returns>
    public static string? WhyRefused(ManagedService primary, Guid replicaServerId, string replicaVersion)
    {
        if (!ReplicationSupport.Supports(primary.Type)) return ReplicationSupport.UnsupportedReason(primary.Type);

        // Chained replication (a replica of a replica) is a real PostgreSQL feature, and also a real
        // way to build a topology nobody on this platform can reason about yet — lag would have to be
        // measured against an upstream that is itself lagging, and a primary delete's "refuse while
        // replicas exist" guard would need to walk a chain instead of a single FK. Refused by name
        // rather than half-supported.
        if (primary.PrimaryManagedServiceId is not null)
            return $"'{primary.Name}' is itself a read replica. Replicating from a replica " +
                   "(chained replication) is not supported — create the new replica from the " +
                   "original primary instead.";

        // pg_basebackup needs a running server to copy from. A stopped or still-provisioning primary
        // has nothing to stream.
        if (primary.Status != ServiceStatus.Running)
            return $"'{primary.Name}' is not running. Start it before creating a replica of it.";

        // Cross-server private networking has not been built on this platform at all yet —
        // ExternalAccessAvailability.Refuse already declines exactly this for a database's outside
        // access gateway ("Reaching a database on another server is not built yet"). A replica needs
        // the same reachability an external client would, so it inherits the same limit rather than
        // silently pretending to solve it.
        if (replicaServerId != primary.ServerId)
            return $"A read replica of '{primary.Name}' must be placed on the same server it runs " +
                   "on. Harbora does not yet route database traffic between servers, so a replica " +
                   "placed elsewhere could never reach it.";

        // Physical (byte-for-byte) replication requires the same major version on both sides — a
        // replica one major version ahead or behind is not a supported PostgreSQL configuration, and
        // pg_basebackup itself would refuse it at seed time. Checked here, by name, rather than left
        // to surface as an opaque exit code from the seed command.
        if (!string.Equals(replicaVersion, primary.Version, StringComparison.Ordinal))
            return $"A read replica must run the exact same PostgreSQL version as '{primary.Name}' " +
                   $"({primary.Version}) — physical replication does not cross major versions.";

        return null;
    }
}

/// <summary>
/// Ends a replica's recovery mode and turns it into an ordinary, independent, writable instance
/// (3.2, round-2 market-gaps plan) — the deliberate, explicit way this platform's topology changes,
/// as opposed to the two ways it is refused to change implicitly: a primary restart or rebuild leaves
/// every replica's <see cref="ManagedService.PrimaryManagedServiceId"/> exactly as it was (the
/// replica simply reconnects — see <see cref="ManagedServiceEngine.ProvisionAsync"/>'s own reasoning
/// for why a rebuild is safe for a replica), and deleting a primary while a replica exists is refused
/// outright (<c>DatabasesController.Remove</c>). Promotion is the one place this link is ever
/// deliberately cut.
///
/// <para>
/// <c>SELECT pg_promote();</c> — PostgreSQL's own SQL-callable promotion, equivalent to <c>pg_ctl
/// promote</c> without needing a shell inside the container. It ends recovery and makes the instance
/// accept writes; the row's own <see cref="ManagedService.PrimaryManagedServiceId"/> is cleared only
/// after the engine confirms this succeeded — never before, and never assumed.
/// </para>
/// </summary>
public static class ReplicaPromotionPlan
{
    public static IReadOnlyList<string> Command(string host, int port, string adminUser, string database) =>
        [
            "psql", "-v", "ON_ERROR_STOP=1",
            "-h", host, "-p", port.ToString(), "-U", adminUser, "-d", database,
            "-c", "SELECT pg_promote();"
        ];

    public static IReadOnlyDictionary<string, string> Environment(string adminPassword) =>
        new Dictionary<string, string> { ["PGPASSWORD"] = adminPassword };
}
