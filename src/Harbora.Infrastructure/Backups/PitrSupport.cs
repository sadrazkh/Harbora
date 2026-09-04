using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Which engines point-in-time recovery covers (3.1, round-2 market-gaps plan) — the same
/// "supported by name, refused by name" shape <c>DatabaseGrantSql.Supports</c>/<c>UnsupportedReason</c>
/// already give every other per-engine capability on this platform.
///
/// <para>
/// PostgreSQL only. WAL archiving, base backups and replay are a PostgreSQL-specific mechanism —
/// MySQL/MariaDB's equivalent is binlog PITR, a different command surface entirely and explicitly a
/// follow-on item, not a variant this class pretends to almost support. Every caller that touches
/// PITR — <see cref="WalArchivingService"/>, the base-backup schedule, <c>PitrRestoreService</c> —
/// checks <see cref="Supports"/> before doing anything else, so an operator who tries this against a
/// MySQL instance is told exactly that, by name, rather than watching a Postgres-shaped command fail
/// against a server that never understood it.
/// </para>
/// </summary>
public static class PitrSupport
{
    public static bool Supports(ManagedServiceType type) => type is ManagedServiceType.PostgreSql;

    public static string UnsupportedReason(ManagedServiceType type) =>
        type switch
        {
            ManagedServiceType.MySql or ManagedServiceType.MariaDb =>
                $"Point-in-time recovery is not available for {type} yet. {type} recovers through its " +
                "own binlog mechanism, which is a separate feature Harbora has not built.",
            _ =>
                $"Point-in-time recovery is not available for {type}. It is built for PostgreSQL only."
        };
}
