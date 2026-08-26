using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Whether a dump taken from one engine can be loaded into another (D2, 2026-08-25 shared-databases
/// plan: "restoring a PostgreSQL dump into MySQL must refuse by name").
///
/// <para>
/// MySQL and MariaDB are one family: <see cref="DatabaseDumpPlan.For"/> already gives them the
/// identical <c>mysqldump</c>/<c>mysql</c> command pair, so a dump taken from one loads cleanly into
/// the other. PostgreSQL's <c>pg_dump</c> output is a different SQL dialect entirely, and MongoDB's
/// <c>mongodump</c> archive is a different format again — neither is a text dump the other engine's
/// client could even parse. Redis is never asked: it has no logical dump at all
/// (<see cref="DatabaseDumpPlan.WhyNoDump"/>), so nothing here is ever compared against it.
/// </para>
/// </summary>
public static class DatabaseEngineCompat
{
    private static readonly ManagedServiceType[] MySqlFamily =
        [ManagedServiceType.MySql, ManagedServiceType.MariaDb];

    public static bool AreCompatible(ManagedServiceType source, ManagedServiceType target)
    {
        if (source == target) return true;
        return MySqlFamily.Contains(source) && MySqlFamily.Contains(target);
    }

    /// <summary>The refusal sentence, naming both engines the way the plan asks for.</summary>
    public static string Refusal(ManagedServiceType source, ManagedServiceType target) =>
        $"This backup is a {source} dump and cannot be restored into {target}. " +
        $"Restore it into a {source} database instead.";
}
