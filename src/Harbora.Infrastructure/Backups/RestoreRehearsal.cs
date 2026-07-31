using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Backups;

/// <summary>The commands that rehearse a restore, in the order they must run.</summary>
/// <param name="Create">Makes the throwaway database.</param>
/// <param name="Restore">Loads the dump into it.</param>
/// <param name="Count">Prints how many tables arrived — the proof that something was restored.</param>
/// <param name="Drop">Removes it, and must run whether the rest succeeded or not.</param>
/// <param name="Env">Carries the password; never on a command line.</param>
public sealed record RehearsalPlan(
    IReadOnlyList<string> Create,
    IReadOnlyList<string> Restore,
    IReadOnlyList<string> Count,
    IReadOnlyList<string> Drop,
    IReadOnlyDictionary<string, string> Env,
    string ScratchDatabase);

/// <summary>
/// Restoring a backup somewhere harmless, to find out whether it would restore at all.
///
/// Verification could already say an artifact was present, matched its checksum, decrypted, and was
/// a readable archive. None of that answers the only question anyone actually has. A gzip file full
/// of SQL that references a missing extension, or was cut short while the database was mid-write, is
/// a perfectly readable archive and a worthless backup — and the discovery happens during an
/// incident, which is the one moment it must not.
///
/// So the dump is restored into a scratch database on the same server and then counted. It cannot
/// touch the real one: everything happens inside a database created for the purpose and dropped
/// afterwards.
/// </summary>
public static class RestoreRehearsal
{
    /// <summary>
    /// A name that is obviously temporary, obviously ours, and cannot collide with a real database.
    /// It appears in the server's database list while the check runs, so it has to explain itself.
    /// </summary>
    public static string ScratchName(Guid backupId) =>
        $"harbora_restore_check_{backupId.ToString("N")[..12]}";

    public static RehearsalPlan? For(ManagedServiceType type, ServiceCreds creds, string dumpPath, Guid backupId)
    {
        var scratch = ScratchName(backupId);

        return type switch
        {
            ManagedServiceType.PostgreSql => new RehearsalPlan(
                // Connected to the service's own database to issue CREATE/DROP: postgres has no
                // "no database" connection, and this one is known to exist.
                Psql(creds, creds.Database, $"CREATE DATABASE {Identifier(scratch)}"),
                ["sh", "-c",
                 $"set -o pipefail; gunzip -c {Shell(dumpPath)} | psql -h {Shell(creds.Host)} -p {creds.Port} " +
                 $"-U {Shell(creds.User)} -d {Shell(scratch)} -v ON_ERROR_STOP=1"],
                Psql(creds, scratch,
                     "SELECT count(*) FROM information_schema.tables WHERE table_schema NOT IN " +
                     "('pg_catalog','information_schema')"),
                Psql(creds, creds.Database, $"DROP DATABASE IF EXISTS {Identifier(scratch)}"),
                new Dictionary<string, string> { ["PGPASSWORD"] = creds.Password },
                scratch),

            ManagedServiceType.MySql or ManagedServiceType.MariaDb => new RehearsalPlan(
                Mysql(creds, null, $"CREATE DATABASE {Backtick(scratch)}"),
                ["sh", "-c",
                 $"set -o pipefail; gunzip -c {Shell(dumpPath)} | mysql -h {Shell(creds.Host)} -P {creds.Port} " +
                 $"-u {Shell(creds.User)} {Shell(scratch)}"],
                Mysql(creds, null,
                      $"SELECT count(*) FROM information_schema.tables WHERE table_schema = {SqlString(scratch)}"),
                Mysql(creds, null, $"DROP DATABASE IF EXISTS {Backtick(scratch)}"),
                new Dictionary<string, string> { ["MYSQL_PWD"] = creds.Password },
                scratch),

            _ => null
        };
    }

    /// <summary>
    /// Why an engine cannot be rehearsed. Said out loud, because "not checked" and "checked and
    /// fine" must never look the same on a screen.
    /// </summary>
    public static string? WhyUnsupported(ManagedServiceType type) => type switch
    {
        ManagedServiceType.Redis =>
            "Redis backups are a copy of its snapshot file, so there is no dump to load into a " +
            "scratch database. Its artifact is still checked for integrity.",
        ManagedServiceType.MongoDb =>
            "MongoDB restores are not rehearsed yet — its tooling changed name between the versions " +
            "Harbora offers.",
        _ => null
    };

    /// <summary>
    /// Reads the table count out of the client's output. Null when nothing usable came back, which
    /// is treated as a failed rehearsal rather than as zero.
    /// </summary>
    public static int? ReadCount(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // Docker frames a container's output, so the digits arrive with control bytes attached.
            var line = new string(raw.Where(c => !char.IsControl(c)).ToArray()).Trim();

            if (line.Length > 0 && line.All(char.IsAsciiDigit) && int.TryParse(line, out var count))
                return count;
        }

        return null;
    }

    /// <summary>
    /// The verdict. A restore that produced no tables is a failure however cleanly it ran — that is
    /// exactly what an empty or truncated dump looks like.
    /// </summary>
    public static string? Explain(int? tablesRestored) => tablesRestored switch
    {
        null => "The rehearsal did not report what it restored, so the backup cannot be trusted yet.",
        0 => "The backup restored without error but contained no tables. It is empty.",
        _ => null
    };

    private static IReadOnlyList<string> Psql(ServiceCreds creds, string database, string sql) =>
        ["sh", "-c",
         $"psql -h {Shell(creds.Host)} -p {creds.Port} -U {Shell(creds.User)} -d {Shell(database)} " +
         $"-v ON_ERROR_STOP=1 -tAc {Shell(sql)}"];

    private static IReadOnlyList<string> Mysql(ServiceCreds creds, string? database, string sql) =>
        ["sh", "-c",
         $"mysql -h {Shell(creds.Host)} -P {creds.Port} -u {Shell(creds.User)} " +
         (database is null ? "" : $"{Shell(database)} ") +
         $"-N -B -e {Shell(sql)}"];

    private static string Shell(string value) => "'" + value.Replace("'", "'\\''") + "'";
    private static string SqlString(string value) => "'" + value.Replace("'", "''") + "'";
    private static string Identifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string Backtick(string value) => "`" + value.Replace("`", "``") + "`";
}
