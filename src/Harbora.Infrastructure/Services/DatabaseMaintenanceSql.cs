using Harbora.Domain.Common;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The statements <c>VACUUM</c>/<c>VACUUM FULL</c>/<c>ANALYZE</c>/<c>REINDEX</c> (PostgreSQL) and
/// <c>OPTIMIZE TABLE</c> (MySQL/MariaDB) compile down to (2.3, round-2 market-gaps plan) — the
/// maintenance counterpart of <see cref="DatabaseGrantSql"/>, kept as its own class rather than added
/// to that one because a grant statement and a maintenance statement answer different questions about
/// safety: nothing here ever carries a password, so <see cref="DatabaseGrantExecutor.MaintainAsync"/>
/// is free to hand the engine's own stdout/stderr straight back to the caller, which
/// <see cref="DatabaseGrantExecutor.CreateAsync"/> and its siblings deliberately never do.
///
/// <para>
/// Every value here still ends up inside a statement, so the same rule applies:
/// <see cref="DatabaseGrantSql.IsSafe"/> is asked before anything is interpolated, and a name that
/// fails it is refused rather than escaped.
/// </para>
/// </summary>
public static class DatabaseMaintenanceSql
{
    /// <summary>Engines this can run any maintenance statement on at all. Anything else is refused by
    /// name — the same "no clean maintenance story" refusal <c>LogicalDatabaseService</c> already
    /// gives an engine with no per-database story.</summary>
    public static bool Supports(ManagedServiceType type) =>
        type is ManagedServiceType.PostgreSql or ManagedServiceType.MySql or ManagedServiceType.MariaDb;

    /// <summary>Why this engine has no maintenance story, for a message somebody can act on.</summary>
    public static string UnsupportedReason(ManagedServiceType type) =>
        $"Scheduled maintenance is not available for {type}. It is built for PostgreSQL " +
        "(VACUUM, VACUUM FULL, ANALYZE, REINDEX) and MySQL/MariaDB (OPTIMIZE TABLE).";

    /// <summary>
    /// Which operations this engine actually offers. PostgreSQL and MySQL/MariaDB do not share a
    /// vocabulary here — <c>VACUUM</c> has no MySQL equivalent, and <c>OPTIMIZE TABLE</c> has no
    /// PostgreSQL one — so this is a lookup, not a filter over one shared list.
    /// </summary>
    public static IReadOnlyList<DatabaseMaintenanceOperation> OperationsFor(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql =>
        [
            DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceOperation.VacuumFull,
            DatabaseMaintenanceOperation.Analyze, DatabaseMaintenanceOperation.Reindex
        ],
        ManagedServiceType.MySql or ManagedServiceType.MariaDb => [DatabaseMaintenanceOperation.Optimize],
        _ => []
    };

    public static bool SupportsOperation(ManagedServiceType type, DatabaseMaintenanceOperation operation) =>
        OperationsFor(type).Contains(operation);

    /// <summary>Why this specific operation is refused, for a message somebody can act on — distinct
    /// from <see cref="UnsupportedReason"/>, which is about the engine having no story at all.</summary>
    public static string UnsupportedOperationReason(ManagedServiceType type, DatabaseMaintenanceOperation operation) =>
        $"{Label(operation)} is not available for {type}.";

    /// <summary>The statement's own name, in the words an operator reads — what a failure names
    /// alongside the database, and what the panel's variant picker labels each button with.</summary>
    public static string Label(DatabaseMaintenanceOperation operation) => operation switch
    {
        DatabaseMaintenanceOperation.Vacuum => "VACUUM",
        DatabaseMaintenanceOperation.VacuumFull => "VACUUM FULL",
        DatabaseMaintenanceOperation.Analyze => "ANALYZE",
        DatabaseMaintenanceOperation.Reindex => "REINDEX",
        DatabaseMaintenanceOperation.Optimize => "OPTIMIZE TABLE",
        _ => operation.ToString()
    };

    /// <summary>
    /// What this operation does and what it costs, in the operator's own words — the honesty
    /// requirement this feature exists to satisfy. <see cref="DatabaseMaintenanceOperation.VacuumFull"/>
    /// and <see cref="DatabaseMaintenanceOperation.Optimize"/> both say plainly that they lock the
    /// table and need free disk headroom; the online operations say plainly that they do not, so the
    /// two are never offered as one undifferentiated list.
    /// </summary>
    public static string Describe(DatabaseMaintenanceOperation operation) => operation switch
    {
        DatabaseMaintenanceOperation.Vacuum =>
            "Reclaims space left by deleted or updated rows and refreshes the visibility map. Online — reads and writes continue normally while it runs.",
        DatabaseMaintenanceOperation.VacuumFull =>
            "Rewrites the whole table into a new file to reclaim every byte of dead space. Takes an ACCESS EXCLUSIVE lock for the duration — nothing can read or write the table — and needs free disk space roughly equal to the table's own size.",
        DatabaseMaintenanceOperation.Analyze =>
            "Refreshes the statistics the query planner uses to choose a plan. Online — reads and writes continue normally while it runs.",
        DatabaseMaintenanceOperation.Reindex =>
            "Rebuilds every index in the database. Each index is locked against writes while it rebuilds, and the operation needs free disk space for the copy of the index being built.",
        DatabaseMaintenanceOperation.Optimize =>
            "Rebuilds every table to reclaim space and defragment it. Each table is locked while it runs.",
        _ => ""
    };

    /// <summary>Whether this operation is safe to run against a live workload without a maintenance
    /// window — the plain fact the panel's variant picker leads with, before the longer
    /// <see cref="Describe"/> sentence.</summary>
    public static bool IsOnline(DatabaseMaintenanceOperation operation) =>
        operation is DatabaseMaintenanceOperation.Vacuum or DatabaseMaintenanceOperation.Analyze;

    /// <summary>
    /// Builds the statement, or null when it cannot be built safely — the same null-means-refused
    /// shape <see cref="DatabaseGrantSql.Create"/> already uses.
    ///
    /// <para>
    /// PostgreSQL runs a single <c>-c</c> statement, exactly like every other operation
    /// <see cref="DatabaseGrantSql"/> issues. MySQL/MariaDB uses <c>mariadb-check --optimize</c>
    /// rather than hand-building an <c>OPTIMIZE TABLE</c> per table: the client already knows how to
    /// enumerate a database's own tables, so nothing here has to build a second statement out of a
    /// list of names it would first have to ask the database for — the same "let the client's own
    /// tool do it" reasoning that keeps <c>pg_basebackup</c> out of a hand-rolled loop over
    /// <c>pg_dump</c> calls elsewhere in this codebase. It is the one client image
    /// <see cref="DatabaseGrantSql"/> already uses for both MySQL and MariaDB (see
    /// <see cref="DatabaseGrantSql.Rotate"/>'s own remarks on why one image can talk to both), and it
    /// reads <c>MYSQL_PWD</c> from the environment the same way every other command from that image
    /// does — see <see cref="DatabaseGrantSql.Environment"/>.
    /// </para>
    /// </summary>
    public static GrantCommand? Build(
        ManagedServiceType type, DatabaseMaintenanceOperation operation,
        string host, int port, string adminUser, string database)
    {
        if (!SupportsOperation(type, operation)) return null;
        if (!DatabaseGrantSql.IsSafe(database) || !DatabaseGrantSql.IsSafe(adminUser)) return null;

        return type switch
        {
            ManagedServiceType.PostgreSql => new GrantCommand("postgres:16-alpine",
            [
                "psql", "-v", "ON_ERROR_STOP=1",
                "-h", host, "-p", port.ToString(), "-U", adminUser, "-d", database,
                "-c", PostgresStatement(operation, database)
            ]),

            // No -e/statement here at all — the whole point of mariadb-check over a hand-built
            // OPTIMIZE TABLE list, see this method's own remarks above.
            _ => new GrantCommand("mariadb:11",
            [
                "mariadb-check", "-h", host, "-P", port.ToString(), "-u", adminUser, "--optimize", database
            ])
        };
    }

    private static string PostgresStatement(DatabaseMaintenanceOperation operation, string database) => operation switch
    {
        DatabaseMaintenanceOperation.Vacuum => "VACUUM;",
        DatabaseMaintenanceOperation.VacuumFull => "VACUUM FULL;",
        DatabaseMaintenanceOperation.Analyze => "ANALYZE;",

        // REINDEX DATABASE, not REINDEX SCHEMA/TABLE: this reaches every index in every schema of
        // the connected (logical) database, which is the whole-database scope the panel offers.
        DatabaseMaintenanceOperation.Reindex => $"REINDEX DATABASE \"{database}\";",

        _ => throw new InvalidOperationException($"{operation} has no PostgreSQL statement.")
    };
}
