using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Which engines a read replica can exist for (3.2, round-2 market-gaps plan) — the same
/// "supported by name, refused by name" shape <c>Harbora.Infrastructure.Backups.PitrSupport</c>
/// already gives PostgreSQL's other streaming-WAL capability, and for the identical reason: streaming
/// replication is PostgreSQL-specific machinery built on the exact WAL stream 3.1 already taught this
/// platform to archive. MySQL/MariaDB replicate too, but through binlog replication — a different
/// command surface entirely, and an explicit follow-on rather than a variant this class pretends to
/// almost support.
///
/// <para>
/// Every entry point that can bring a replica into existence or change what it does — the create
/// form, <c>ManagedServiceEngine.ProvisionAsync</c>'s seeding step, <c>ReplicationLagMonitor</c> —
/// checks <see cref="Supports"/> before doing anything else, so an operator who tries this against a
/// MySQL instance is told exactly that, by name, rather than watching a Postgres-shaped
/// <c>pg_basebackup</c> invocation fail against a server that never understood it.
/// </para>
/// </summary>
public static class ReplicationSupport
{
    public static bool Supports(ManagedServiceType type) => type is ManagedServiceType.PostgreSql;

    public static string UnsupportedReason(ManagedServiceType type) =>
        type switch
        {
            ManagedServiceType.MySql or ManagedServiceType.MariaDb =>
                $"Read replicas are not available for {type} yet. {type} replicates through its own " +
                "binlog mechanism, which is a separate feature Harbora has not built.",
            _ =>
                $"Read replicas are not available for {type}. Streaming replication is built for " +
                "PostgreSQL only."
        };
}
