using Npgsql;

namespace Harbora.Infrastructure.Backups;

/// <summary>
/// Decides whether an upgrade needs a restore point before the schema changes under it, and builds
/// the command that takes one.
///
/// Separated from the running of it so the two decisions that matter — "is this an upgrade of an
/// existing install?" and "what exactly gets executed?" — are testable without Docker or a database.
/// </summary>
public static class UpgradeSafetyPlan
{
    /// <summary>
    /// A restore point is warranted only when migrations are about to run against data that already
    /// exists.
    ///
    /// A fresh install has every migration pending and nothing to lose, so backing it up would turn
    /// first-boot into a Docker round-trip that can only fail. An ordinary restart has no pending
    /// migrations and changes nothing. The dangerous case is the remaining one: an existing database
    /// about to be altered by code that was not running when the data was written.
    /// </summary>
    public static bool NeedsRestorePoint(int pendingMigrations, int appliedMigrations) =>
        pendingMigrations > 0 && appliedMigrations > 0;

    /// <summary>
    /// The shell the dump helper runs.
    ///
    /// <c>set -o pipefail</c> is not decoration: <c>pg_dump | gzip</c> reports gzip's exit code, so
    /// without it a dump that failed halfway still "succeeds" and leaves a valid gzip of a truncated
    /// dump — the worst possible outcome for a restore point, because it looks fine until it is
    /// needed. The password travels in the environment rather than the command line, which would
    /// otherwise show up in <c>docker inspect</c> and in process listings.
    /// </summary>
    public static IReadOnlyList<string> DumpCommand(NpgsqlConnectionStringBuilder db, string targetPath) =>
    [
        "sh", "-c",
        $"set -o pipefail; pg_dump -h {Shell(db.Host!)} -p {db.Port} -U {Shell(db.Username!)} " +
        $"-d {Shell(db.Database!)} --no-owner --no-privileges | gzip -c > {Shell(targetPath)}"
    ];

    /// <summary>
    /// Single-quotes a value for <c>sh -c</c>. Database names and users come from configuration, not
    /// from users, but a connection string edited by hand is not a trusted input either.
    /// </summary>
    public static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>
    /// Which dumps to delete, oldest first, keeping the newest <paramref name="keep"/>.
    /// Names are timestamped, so ordering by name is ordering by age.
    /// </summary>
    public static IReadOnlyList<string> DumpsToPrune(IEnumerable<string> fileNames, int keep)
    {
        if (keep < 0) keep = 0;
        return fileNames
            .Where(IsRestorePoint)
            .OrderByDescending(f => f, StringComparer.Ordinal)
            .Skip(keep)
            .ToList();
    }

    public const string FilePrefix = "pre-upgrade-";

    public static bool IsRestorePoint(string fileName) =>
        fileName.StartsWith(FilePrefix, StringComparison.Ordinal) &&
        fileName.EndsWith(".sql.gz", StringComparison.Ordinal);

    /// <summary>
    /// Timestamped so the newest sorts last lexically, and so two upgrades never collide. Invariant
    /// because a Jalali year in the name would sort — and read — as a different file entirely.
    /// </summary>
    public static string FileNameFor(DateTimeOffset when) =>
        FilePrefix + when.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)
        + ".sql.gz";
}
