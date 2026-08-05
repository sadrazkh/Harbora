using Harbora.Domain.Common;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// One invocation of a database client, as an argument list and an environment.
/// </summary>
/// <param name="Arguments">Passed to the process verbatim. No shell, so nothing is re-interpreted.</param>
/// <param name="Environment">Where the password travels. Never an argument.</param>
/// <param name="FileName">What the artifact is called, so a restore knows what it is holding.</param>
public sealed record DatabaseCommand(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> Environment,
    string FileName);

/// <summary>
/// Dump and restore commands for the engines this module can back up.
///
/// <para>
/// <b>Why this exists next to the platform's <c>DatabaseDumpPlan</c>.</b> That one pipes into
/// <c>gzip</c>, which needs a shell, because its artifact IS the compressed dump. This module's
/// artifact is a snapshot in a repository that already compresses and deduplicates, so the pipe is
/// redundant here — and without it the whole command becomes an argument list with no shell
/// anywhere (THREAT_MODEL T1). The platform's plan is unchanged and still used by the original
/// backup feature.
/// </para>
/// <para>
/// Dumps are written <b>uncompressed</b> on purpose. Compressing before the engine sees the data
/// defeats deduplication completely: change one row, and every byte of a compressed dump differs, so
/// each nightly backup stores in full. Uncompressed, the engine stores only what actually changed.
/// </para>
/// </summary>
public static class DatabaseDumpCommands
{
    /// <summary>Path inside the helper container where the staging area is mounted.</summary>
    public const string ContainerMountPath = "/dump";

    /// <summary>
    /// Maps the platform's service type to this module's engine vocabulary, or null when the module
    /// has no path for it.
    /// </summary>
    public static DatabaseEngine? EngineFor(ManagedServiceType type) => type switch
    {
        ManagedServiceType.PostgreSql => DatabaseEngine.PostgreSql,
        ManagedServiceType.MySql => DatabaseEngine.MySql,
        ManagedServiceType.MariaDb => DatabaseEngine.MariaDb,
        ManagedServiceType.MongoDb => DatabaseEngine.MongoDb,
        ManagedServiceType.Redis => DatabaseEngine.Redis,
        _ => null
    };

    /// <summary>
    /// Why an engine cannot be dumped by this module, or null when it can.
    ///
    /// <para>
    /// Both refusals are specific, because "unsupported" tells an operator nothing about what to do
    /// instead. Redis has a real alternative; MongoDB has a real blocker with a known fix.
    /// </para>
    /// </summary>
    public static string? WhyUnsupported(DatabaseEngine engine) => engine switch
    {
        DatabaseEngine.Redis =>
            "Redis keeps its own snapshot file rather than offering a logical dump. Back up its data " +
            "volume instead — choose the Docker volume target.",

        DatabaseEngine.MongoDb =>
            "mongodump has no way to take a password that does not end up in the process table, where " +
            "any local user can read it. Supporting it needs a credential file this module does not " +
            "write yet, so it is refused rather than shipped with the password exposed.",

        _ => null
    };

    /// <summary>Export the whole database to a file in the mounted staging directory.</summary>
    public static DatabaseCommand? Dump(DatabaseEngine engine, DatabaseConnection connection, string stageName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (WhyUnsupported(engine) is not null) return null;

        // The stage name is generated from a Guid by the caller, never user input, but it becomes a
        // path inside the container and is held to the same rule as everything else that does.
        EngineArgumentGuard.Require(stageName, EngineArgumentGuard.IsSafeSnapshotId, "Dump file name");

        var host = Require(connection.Host, "Database host");
        var user = Require(connection.User, "Database user");
        var database = Require(connection.DatabaseName, "Database name");
        var port = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return engine switch
        {
            DatabaseEngine.PostgreSql => new DatabaseCommand(
                [
                    "pg_dump",
                    "--host", host,
                    "--port", port,
                    "--username", user,
                    "--dbname", database,
                    // Restored by pg_restore, which can go into a database that already has objects.
                    "--format=custom",
                    // Uncompressed so the repository can deduplicate between nightly dumps.
                    "--compress=0",
                    // The restoring role is rarely the dumping one, and ownership it cannot grant
                    // turns every statement into an error.
                    "--no-owner",
                    "--no-privileges",
                    $"--file={ContainerMountPath}/{stageName}.pgdump"
                ],
                new Dictionary<string, string> { ["PGPASSWORD"] = connection.Password },
                $"{stageName}.pgdump"),

            DatabaseEngine.MySql or DatabaseEngine.MariaDb => new DatabaseCommand(
                [
                    "mysqldump",
                    "--host", host,
                    "--port", port,
                    "--user", user,
                    // A consistent point-in-time view on InnoDB without locking the whole database.
                    "--single-transaction",
                    "--routines",
                    "--triggers",
                    $"--result-file={ContainerMountPath}/{stageName}.sql",
                    database
                ],
                new Dictionary<string, string> { ["MYSQL_PWD"] = connection.Password },
                $"{stageName}.sql"),

            _ => null
        };
    }

    /// <summary>
    /// Load a dump back. <paramref name="targetDatabase"/> may differ from the one it came from,
    /// which is how a restore is inspected without touching the live database.
    /// </summary>
    public static DatabaseCommand? Restore(
        DatabaseEngine engine, DatabaseConnection connection, string fileName, string targetDatabase)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (WhyUnsupported(engine) is not null) return null;

        var host = Require(connection.Host, "Database host");
        var user = Require(connection.User, "Database user");
        var database = Require(targetDatabase, "Target database name");
        var port = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var safeFile = RequireFileName(fileName);

        return engine switch
        {
            DatabaseEngine.PostgreSql => new DatabaseCommand(
                [
                    "pg_restore",
                    "--host", host,
                    "--port", port,
                    "--username", user,
                    "--dbname", database,
                    // Replaces what is there rather than colliding with it.
                    "--clean",
                    "--if-exists",
                    "--no-owner",
                    "--no-privileges",
                    // Stop at the first error. Without this pg_restore reports success over a
                    // half-restored database, which is the worst possible outcome to report well of.
                    "--exit-on-error",
                    $"{ContainerMountPath}/{safeFile}"
                ],
                new Dictionary<string, string> { ["PGPASSWORD"] = connection.Password },
                safeFile),

            DatabaseEngine.MySql or DatabaseEngine.MariaDb => new DatabaseCommand(
                [
                    "mysql",
                    "--host", host,
                    "--port", port,
                    "--user", user,
                    // SOURCE is a client built-in that reads the file itself, so the dump is loaded
                    // without the "< file" redirect that would otherwise require a shell.
                    $"--execute=SOURCE {ContainerMountPath}/{safeFile}",
                    database
                ],
                new Dictionary<string, string> { ["MYSQL_PWD"] = connection.Password },
                safeFile),

            _ => null
        };
    }

    /// <summary>
    /// Create an empty database to rehearse a restore into.
    ///
    /// <para>
    /// The only question that matters about a backup is whether it restores, and the only honest way
    /// to answer it is to restore it — into a database created for the purpose and dropped
    /// afterwards, never the live one.
    /// </para>
    /// </summary>
    public static DatabaseCommand? CreateScratch(
        DatabaseEngine engine, DatabaseConnection connection, string scratchDatabase)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var name = RequireIdentifier(scratchDatabase);
        var port = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return engine switch
        {
            DatabaseEngine.PostgreSql => new DatabaseCommand(
                ["createdb", "--host", connection.Host, "--port", port,
                 "--username", connection.User, name],
                new Dictionary<string, string> { ["PGPASSWORD"] = connection.Password },
                name),

            DatabaseEngine.MySql or DatabaseEngine.MariaDb => new DatabaseCommand(
                ["mysql", "--host", connection.Host, "--port", port, "--user", connection.User,
                 $"--execute=CREATE DATABASE `{name}`"],
                new Dictionary<string, string> { ["MYSQL_PWD"] = connection.Password },
                name),

            _ => null
        };
    }

    /// <summary>Remove the scratch database, whatever the rehearsal concluded.</summary>
    public static DatabaseCommand? DropScratch(
        DatabaseEngine engine, DatabaseConnection connection, string scratchDatabase)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var name = RequireIdentifier(scratchDatabase);
        var port = connection.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return engine switch
        {
            DatabaseEngine.PostgreSql => new DatabaseCommand(
                ["dropdb", "--host", connection.Host, "--port", port,
                 "--username", connection.User, "--if-exists", name],
                new Dictionary<string, string> { ["PGPASSWORD"] = connection.Password },
                name),

            DatabaseEngine.MySql or DatabaseEngine.MariaDb => new DatabaseCommand(
                ["mysql", "--host", connection.Host, "--port", port, "--user", connection.User,
                 $"--execute=DROP DATABASE IF EXISTS `{name}`"],
                new Dictionary<string, string> { ["MYSQL_PWD"] = connection.Password },
                name),

            _ => null
        };
    }

    /// <summary>A scratch name derived from the snapshot, so a stray one can be traced back.</summary>
    public static string ScratchNameFor(Guid snapshotId) => $"harbora_verify_{snapshotId:N}"[..40];

    private static string Require(string? value, string what)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"{what} is required.", nameof(value));

        // These reach a process as arguments. They come from a managed-service row rather than from
        // a visitor, but a database name typed by hand is not a trusted input either.
        if (value.Contains('\0') || value.Contains('\n') || value.Contains('\r'))
            throw new ArgumentException($"{what} contains characters that are not permitted.", nameof(value));

        return value;
    }

    /// <summary>
    /// A database identifier that will be interpolated into SQL (<c>CREATE DATABASE</c>), so it is
    /// held to a strict allowlist rather than escaped — there is no safe escaping of an identifier
    /// that can contain a backtick.
    /// </summary>
    private static string RequireIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > 63
            || !value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            throw new ArgumentException(
                "A database identifier must be letters, digits or underscores.", nameof(value));

        return value;
    }

    /// <summary>An artifact file name, which becomes a path inside the helper container.</summary>
    private static string RequireFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Contains('/') || value.Contains('\\') || value.Contains("..", StringComparison.Ordinal)
            || value.StartsWith('-'))
            throw new ArgumentException("That dump file name is not valid.", nameof(value));

        return value;
    }
}
