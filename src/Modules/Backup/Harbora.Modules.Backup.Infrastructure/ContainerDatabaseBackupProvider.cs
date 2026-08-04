using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Modules.Backup.Contracts;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Runs a database's own client in a throwaway container to dump, restore or rehearse it.
///
/// <para>
/// One implementation serves every engine it supports; the differences live in
/// <see cref="DatabaseDumpCommands"/>, which is pure and therefore directly testable. An instance is
/// registered per <see cref="DatabaseEngine"/> so the contract's "one provider per engine" shape
/// holds without five near-identical classes.
/// </para>
/// <para>
/// The container is built from the database's <b>own image and version</b>, so client and server
/// always match. A fixed helper image works right up until someone upgrades a database, and then
/// fails with a version error nobody connects to the upgrade.
/// </para>
/// </summary>
public sealed class ContainerDatabaseBackupProvider(
    DatabaseEngine engine,
    IDockerEngine docker,
    ILogger<ContainerDatabaseBackupProvider> logger) : IDatabaseBackupProvider
{
    public DatabaseEngine Engine => engine;

    public async Task<DatabaseBackupResult> CreateBackupAsync(
        DatabaseBackupContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (DatabaseDumpCommands.WhyUnsupported(engine) is { } unsupported)
            return new DatabaseBackupResult(false, Error: unsupported);

        var command = DatabaseDumpCommands.Dump(engine, context.Connection, context.OutputName);
        if (command is null)
            return new DatabaseBackupResult(false, Error: $"{engine} cannot be exported.");

        var (exit, output) = await RunAsync(context.Execution, command, readOnlyMount: false, cancellationToken);

        if (exit != 0)
            return new DatabaseBackupResult(false, Error: $"The export failed (exit {exit}). {output}");

        var path = Path.Combine(context.OutputDirectory, command.FileName);

        // The client writes into the staging volume BY NAME while the panel reads it through a
        // mount. If those resolve to different volumes the dump reports success and lands somewhere
        // the panel can never read — and the backup would then archive an empty directory.
        if (!File.Exists(path))
            return new DatabaseBackupResult(false, Error:
                $"The export reported success but no file arrived at {path}. Check that the staging " +
                "volume the client mounts and the directory the panel reads are the same volume.");

        var size = new FileInfo(path).Length;
        if (size == 0)
            return new DatabaseBackupResult(false, Error:
                "The export produced an empty file, so there is nothing that could be restored.");

        logger.LogInformation("Exported {Engine} database to {File} ({Size} bytes).",
            engine, command.FileName, size);

        return new DatabaseBackupResult(true, path, size);
    }

    public async Task<DatabaseRestoreResult> RestoreAsync(
        DatabaseRestoreContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (DatabaseDumpCommands.WhyUnsupported(engine) is { } unsupported)
            return new DatabaseRestoreResult(false, unsupported);

        var target = context.TargetDatabaseName ?? context.Connection.DatabaseName;
        if (string.IsNullOrWhiteSpace(target))
            return new DatabaseRestoreResult(false, "No target database was given.");

        var command = DatabaseDumpCommands.Restore(engine, context.Connection, context.FileName, target);
        if (command is null)
            return new DatabaseRestoreResult(false, $"{engine} cannot be restored.");

        // The dump is mounted read-only: a restore reads it, and nothing about loading data into a
        // database should be able to alter the copy it came from.
        var (exit, output) = await RunAsync(context.Execution, command, readOnlyMount: true, cancellationToken);

        return exit == 0
            ? new DatabaseRestoreResult(true)
            : new DatabaseRestoreResult(false,
                $"The restore failed (exit {exit}). The database may be partly restored. {output}");
    }

    /// <summary>
    /// Restores the dump into a throwaway database and drops it again.
    ///
    /// <para>
    /// The only check that answers the question that matters. A dump that decompresses cleanly, has
    /// the right size and a matching checksum can still reference a missing extension or have been
    /// cut short mid-write — and it passes every cheaper check right up to the moment it is needed.
    /// </para>
    /// </summary>
    public async Task<DatabaseBackupVerificationResult> VerifyAsync(
        DatabaseBackupVerificationContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (DatabaseDumpCommands.WhyUnsupported(engine) is { } unsupported)
            return new DatabaseBackupVerificationResult(false, Skipped: true, Detail: unsupported);

        var create = DatabaseDumpCommands.CreateScratch(engine, context.Connection, context.ScratchDatabaseName);
        var drop = DatabaseDumpCommands.DropScratch(engine, context.Connection, context.ScratchDatabaseName);

        if (create is null || drop is null)
            return new DatabaseBackupVerificationResult(false, Skipped: true,
                Detail: $"{engine} cannot be rehearsed.");

        var (createExit, createOutput) =
            await RunAsync(context.Execution, create, readOnlyMount: false, cancellationToken);

        if (createExit != 0)
            return new DatabaseBackupVerificationResult(false,
                Error: $"A scratch database could not be created, so the backup was not rehearsed. {createOutput}");

        try
        {
            var restore = DatabaseDumpCommands.Restore(
                engine, context.Connection, context.FileName, context.ScratchDatabaseName);

            if (restore is null)
                return new DatabaseBackupVerificationResult(false, Skipped: true,
                    Detail: $"{engine} cannot be rehearsed.");

            var (restoreExit, restoreOutput) =
                await RunAsync(context.Execution, restore, readOnlyMount: true, cancellationToken);

            return restoreExit == 0
                ? new DatabaseBackupVerificationResult(true,
                    Detail: $"restored into {context.ScratchDatabaseName}")
                : new DatabaseBackupVerificationResult(false,
                    Error: $"This backup does not restore. {restoreOutput}");
        }
        finally
        {
            // Dropped whatever happened. A failed rehearsal that left its database behind would
            // collide with the next one, and the failure would look like a different problem.
            try
            {
                await RunAsync(context.Execution, drop, readOnlyMount: false, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The scratch database {Name} could not be dropped.",
                    context.ScratchDatabaseName);
            }
        }
    }

    /// <summary>
    /// Runs one client command and returns its exit code with redacted output.
    ///
    /// <para>
    /// The password reaches the process through <see cref="DatabaseCommand.Environment"/> and never
    /// appears in <see cref="DockerOneOffRequest.Command"/>, so it is not in the container's command
    /// line where any local user could read it (THREAT_MODEL T3).
    /// </para>
    /// </summary>
    private async Task<(int Exit, string Output)> RunAsync(
        DatabaseExecutionContext execution,
        DatabaseCommand command,
        bool readOnlyMount,
        CancellationToken ct)
    {
        var output = new StringBuilder();

        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                execution.ClientImage,
                command.Arguments,
                [(execution.StagingVolume, execution.ContainerMountPath, readOnlyMount)],
                Env: command.Environment,
                NetworkMode: execution.NetworkMode),
            new Progress<string>(line =>
            {
                lock (output)
                {
                    if (output.Length < 8192) output.AppendLine(line);
                }
            }),
            ct);

        // Client output can echo a connection string. Redacted with the password registered, so a
        // leak in a diagnostic message is masked before it reaches a row or a log.
        var redactor = new EngineOutputRedactor();
        foreach (var value in command.Environment.Values) redactor.Register(value);

        return (exit, redactor.Redact(output.ToString()).Trim());
    }
}

/// <summary>Picks the provider for an engine, or explains why there isn't one.</summary>
public interface IDatabaseBackupProviderResolver
{
    bool TryResolve(DatabaseEngine engine, out IDatabaseBackupProvider provider);
}

/// <inheritdoc />
public sealed class DatabaseBackupProviderResolver(IEnumerable<IDatabaseBackupProvider> providers)
    : IDatabaseBackupProviderResolver
{
    private readonly Dictionary<DatabaseEngine, IDatabaseBackupProvider> _providers =
        providers.ToDictionary(p => p.Engine);

    public bool TryResolve(DatabaseEngine engine, out IDatabaseBackupProvider provider)
    {
        var found = _providers.TryGetValue(engine, out var match);
        provider = match!;
        return found;
    }
}
