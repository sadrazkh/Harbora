using Harbora.Modules.Backup.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Loads a restored dump back into a live database.
///
/// <para>
/// The second half of a database restore. The engine puts the dump file on disk like any other
/// file; this takes it from there and runs the database's own client against the server. Separate
/// from <see cref="RestoreService"/> so that the generic restore path stays free of database
/// knowledge, and so this can be exercised on its own.
/// </para>
/// </summary>
public interface IDatabaseRestoreExecutor
{
    Task<DatabaseRestoreResult> LoadAsync(
        Guid serviceId, string restoredDirectory, CancellationToken ct);

    /// <summary>
    /// The database's display name, or null if it is gone.
    ///
    /// <para>
    /// Used for the confirmation prompt: the operator types the name of the database they are about
    /// to replace. A generated id would be copy-pasted without being read, which is not a
    /// confirmation of anything.
    /// </para>
    /// </summary>
    Task<string?> DescribeAsync(Guid serviceId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class DatabaseRestoreExecutor(
    IDatabaseTargetStager stager,
    IDatabaseBackupProviderResolver providers,
    IOptions<BackupModuleOptions> options,
    ILogger<DatabaseRestoreExecutor> logger) : IDatabaseRestoreExecutor
{
    private readonly BackupModuleOptions _options = options.Value;

    public async Task<string?> DescribeAsync(Guid serviceId, CancellationToken ct)
    {
        var (plan, _) = await stager.PlanAsync(serviceId, ct);
        return plan?.ServiceName;
    }

    public async Task<DatabaseRestoreResult> LoadAsync(
        Guid serviceId, string restoredDirectory, CancellationToken ct)
    {
        var (plan, error) = await stager.PlanAsync(serviceId, ct);
        if (plan is null) return new DatabaseRestoreResult(false, error);

        if (!providers.TryResolve(plan.Engine, out var provider))
            return new DatabaseRestoreResult(false, $"No backup provider is registered for {plan.Engine}.");

        // The snapshot held exactly one dump. More than one means this snapshot was not taken from a
        // database, and guessing which file to load into a live server is not a guess worth making.
        var files = Directory.Exists(restoredDirectory)
            ? Directory.GetFiles(restoredDirectory)
            : [];

        if (files.Length == 0)
            return new DatabaseRestoreResult(false,
                "The restored backup contains no dump file, so there is nothing to load.");

        if (files.Length > 1)
            return new DatabaseRestoreResult(false,
                $"The restored backup contains {files.Length} files rather than one dump. " +
                "This does not look like a database backup, and nothing was loaded.");

        var dumpPath = files[0];
        var fileName = Path.GetFileName(dumpPath);

        // The client container reaches the file through the staging volume, so the restored copy has
        // to be inside the staging directory the volume backs.
        var relative = Path.GetRelativePath(_options.StagingDirectory, restoredDirectory);
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
            return new DatabaseRestoreResult(false,
                $"A database restore must land inside {_options.StagingDirectory} so the database's " +
                "client can read it. Restore to a destination under that directory.");

        var mountPath = $"{DatabaseDumpCommands.ContainerMountPath}/{relative.Replace('\\', '/')}";

        logger.LogInformation("Loading {File} into {Service}.", fileName, plan.ServiceName);

        var result = await provider.RestoreAsync(new DatabaseRestoreContext(
            plan.Engine,
            plan.Connection,
            plan.Execution with { ContainerMountPath = mountPath },
            restoredDirectory,
            fileName,
            plan.Connection.DatabaseName), ct);

        return result;
    }
}
