namespace Harbora.Modules.Backup.Contracts;

/// <summary>
/// A native dump/restore path for one database engine.
///
/// <para>
/// Databases are not backed up by copying their files while they run. Tarring a live PostgreSQL or
/// MySQL data directory produces a torn copy — the files are being written as they are read — and
/// nothing discovers that until someone tries to restore it, which is the worst possible moment.
/// So each engine is asked for its own contents, through its own client.
/// </para>
/// </summary>
public interface IDatabaseBackupProvider
{
    DatabaseEngine Engine { get; }

    Task<DatabaseBackupResult> CreateBackupAsync(
        DatabaseBackupContext context,
        CancellationToken cancellationToken);

    Task<DatabaseRestoreResult> RestoreAsync(
        DatabaseRestoreContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Confirm the dump would actually load. A file that decompresses cleanly and references a
    /// missing extension, or was cut short mid-write, passes every cheaper check and is worthless.
    /// </summary>
    Task<DatabaseBackupVerificationResult> VerifyAsync(
        DatabaseBackupVerificationContext context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Connection details for a dump or restore.
///
/// <para>
/// <see cref="Password"/> is plaintext in memory and must reach the client process through its
/// ENVIRONMENT (<c>PGPASSWORD</c>, <c>MYSQL_PWD</c>, a credential file), never as a command-line
/// argument — an argument is readable by any local user through <c>/proc/&lt;pid&gt;/cmdline</c>.
/// </para>
/// </summary>
public sealed record DatabaseConnection(
    string Host,
    int Port,
    string Username,
    string Password,
    string? DatabaseName = null);

/// <summary>
/// A dump about to be taken.
///
/// <para>
/// <see cref="OutputPath"/> must be created with restrictive permissions, must not appear in logs,
/// and must be deleted once it has been transferred — including when the transfer fails.
/// </para>
/// </summary>
public sealed record DatabaseBackupContext(
    DatabaseEngine Engine,
    DatabaseConnection Connection,
    string OutputPath,

    // Null dumps every database the credentials can see.
    string? DatabaseName = null,

    // Only honoured by engines with a schema concept.
    string? SchemaName = null,

    bool Compress = true);

public sealed record DatabaseRestoreContext(
    DatabaseEngine Engine,
    DatabaseConnection Connection,
    string InputPath,

    // Restore into a database other than the one it came from. The safe choice when inspecting.
    string? TargetDatabaseName = null,

    bool DropExisting = false);

public sealed record DatabaseBackupVerificationContext(
    DatabaseEngine Engine,
    DatabaseConnection Connection,
    string InputPath,

    // Scratch database to load into. Dropped afterwards whatever the outcome.
    string ScratchDatabaseName);

public sealed record DatabaseBackupResult(
    bool Succeeded,
    string? OutputPath = null,
    long SizeBytes = 0,
    string? Error = null);

public sealed record DatabaseRestoreResult(
    bool Succeeded,
    string? Error = null);

/// <summary>
/// <paramref name="Skipped"/> distinguishes "this engine has no dump to rehearse" from "the check
/// passed". Reporting the first as the second is how an unverifiable backup starts looking safe.
/// </summary>
public sealed record DatabaseBackupVerificationResult(
    bool Succeeded,
    bool Skipped = false,
    long ObjectsRestored = 0,
    string? Detail = null,
    string? Error = null);
