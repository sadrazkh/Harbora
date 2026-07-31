using Harbora.Domain.Common;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Backups;

/// <summary>How a database's contents are taken out and put back.</summary>
/// <param name="Command">Run inside a container of the database's own image, through a shell.</param>
/// <param name="Env">Environment the command needs — the password, kept out of the command line.</param>
/// <param name="FileExtension">Names the artifact for what it is, so a restore knows what it has.</param>
public sealed record DumpPlan(IReadOnlyList<string> Command, IReadOnlyDictionary<string, string> Env, string FileExtension);

/// <summary>
/// A consistent backup of a database, engine by engine.
///
/// What this replaces: a "database" backup was a tar of the data directory taken while the database
/// was running. For PostgreSQL or MySQL that is not a backup — the files are being written to as
/// they are read, so what comes out may be torn, and nothing finds out until someone tries to
/// restore it. The panel reported success either way, which is the worst possible combination.
///
/// So each engine is asked for its contents in the way it supports: <c>pg_dump</c>, <c>mysqldump</c>,
/// <c>mongodump</c>. Redis is the exception and says so — see <see cref="For"/>.
///
/// The password travels in the environment, never on the command line, because a command line is
/// visible to every process on the host.
/// </summary>
public static class DatabaseDumpPlan
{
    /// <summary>
    /// The plan for this engine, or null when a logical dump is not the right tool — Redis is a
    /// cache whose own persistence file is the sensible artifact, and copying it is honest.
    /// </summary>
    public static DumpPlan? For(ManagedServiceType type, ServiceCreds creds, string targetPath) => type switch
    {
        ManagedServiceType.PostgreSql => new DumpPlan(
            ["sh", "-c",
             // pipefail, or gzip's success would hide pg_dump's failure and leave a valid-looking
             // archive of an error message.
             $"set -o pipefail; pg_dump -h {Quote(creds.Host)} -p {creds.Port} -U {Quote(creds.User)} " +
             // --clean --if-exists, or the dump only restores into an empty database: every
             // CREATE TABLE fails on the objects that are already there, and with ON_ERROR_STOP the
             // whole restore stops at the first one. Found by restoring for real, not by reading.
             $"-d {Quote(creds.Database)} --no-owner --no-privileges --clean --if-exists " +
             $"| gzip -c > {Quote(targetPath)}"],
            new Dictionary<string, string> { ["PGPASSWORD"] = creds.Password },
            ".sql.gz"),

        ManagedServiceType.MySql or ManagedServiceType.MariaDb => new DumpPlan(
            ["sh", "-c",
             $"set -o pipefail; mysqldump -h {Quote(creds.Host)} -P {creds.Port} -u {Quote(creds.User)} " +
             $"--single-transaction --routines --triggers {Quote(creds.Database)} | gzip -c > {Quote(targetPath)}"],
            new Dictionary<string, string> { ["MYSQL_PWD"] = creds.Password },
            ".sql.gz"),

        ManagedServiceType.MongoDb => new DumpPlan(
            ["sh", "-c",
             $"mongodump --host {Quote(creds.Host)} --port {creds.Port} -u {Quote(creds.User)} " +
             $"-p \"$MONGO_PWD\" --authenticationDatabase admin --db {Quote(creds.Database)} " +
             $"--archive={Quote(targetPath)} --gzip"],
            new Dictionary<string, string> { ["MONGO_PWD"] = creds.Password },
            ".archive.gz"),

        _ => null
    };

    /// <summary>The command that puts a dump back, paired with <see cref="For"/>.</summary>
    public static DumpPlan? RestoreFor(ManagedServiceType type, ServiceCreds creds, string sourcePath) => type switch
    {
        ManagedServiceType.PostgreSql => new DumpPlan(
            ["sh", "-c",
             $"set -o pipefail; gunzip -c {Quote(sourcePath)} | psql -h {Quote(creds.Host)} -p {creds.Port} " +
             // Stop at the first error rather than carrying on and reporting success over a
             // half-restored database.
             $"-U {Quote(creds.User)} -d {Quote(creds.Database)} -v ON_ERROR_STOP=1"],
            new Dictionary<string, string> { ["PGPASSWORD"] = creds.Password },
            ".sql.gz"),

        ManagedServiceType.MySql or ManagedServiceType.MariaDb => new DumpPlan(
            ["sh", "-c",
             $"set -o pipefail; gunzip -c {Quote(sourcePath)} | mysql -h {Quote(creds.Host)} -P {creds.Port} " +
             $"-u {Quote(creds.User)} {Quote(creds.Database)}"],
            new Dictionary<string, string> { ["MYSQL_PWD"] = creds.Password },
            ".sql.gz"),

        ManagedServiceType.MongoDb => new DumpPlan(
            ["sh", "-c",
             $"mongorestore --host {Quote(creds.Host)} --port {creds.Port} -u {Quote(creds.User)} " +
             $"-p \"$MONGO_PWD\" --authenticationDatabase admin --archive={Quote(sourcePath)} --gzip --drop"],
            new Dictionary<string, string> { ["MONGO_PWD"] = creds.Password },
            ".archive.gz"),

        _ => null
    };

    /// <summary>
    /// Why an engine has no logical dump, for the screen that would otherwise show nothing. Redis is
    /// a cache: its own snapshot file is the artifact, and copying the volume is the honest answer.
    /// </summary>
    public static string? WhyNoDump(ManagedServiceType type) => type switch
    {
        ManagedServiceType.Redis => "Redis keeps its own snapshot file, so its data volume is copied instead of being exported.",
        _ => null
    };

    /// <summary>
    /// Single-quotes a value for <c>sh -c</c>. These come from configuration rather than from a
    /// visitor, but a database name typed by hand is not a trusted input either.
    /// </summary>
    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";
}
