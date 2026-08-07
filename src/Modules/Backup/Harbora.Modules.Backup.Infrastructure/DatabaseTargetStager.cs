using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Modules.Backup.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>Everything needed to run a client against one managed database.</summary>
public sealed record DatabasePlan(
    DatabaseEngine Engine,
    DatabaseConnection Connection,
    DatabaseExecutionContext Execution,
    string ServiceName);

/// <summary>
/// Exports a managed database into the staging area so it can be snapshotted like any directory.
///
/// <para>
/// A database is not backed up by copying its files while it runs. Tarring a live PostgreSQL data
/// directory produces a torn copy — the files are written as they are read — and nothing discovers
/// that until a restore, which is the worst moment to find out. So the database is asked for its
/// contents through its own client, and the resulting dump becomes the thing the engine archives.
/// </para>
/// </summary>
public interface IDatabaseTargetStager
{
    /// <summary>Resolve the service and how to reach it, without contacting it.</summary>
    Task<(DatabasePlan? Plan, string? Error)> PlanAsync(Guid serviceId, CancellationToken ct);

    /// <summary>
    /// Export the database into a directory named from <paramref name="snapshotId"/>. Dispose the
    /// lease to remove it; see <see cref="BackupStagingLayout"/> for why the name is not a fresh
    /// Guid — a dump nothing can name from the row is a dump nothing can clean up after a crash,
    /// and a dump is an entire database in the clear.
    /// </summary>
    Task<TargetLease> StageAsync(Guid serviceId, Guid snapshotId, CancellationToken ct);
}

/// <inheritdoc />
public sealed class DatabaseTargetStager(
    HarboraDbContext db,
    ISecretProtector protector,
    IDatabaseBackupProviderResolver providers,
    IOptions<BackupModuleOptions> options,
    IOptions<HarboraRuntimeOptions> runtime,
    ILogger<DatabaseTargetStager> logger) : IDatabaseTargetStager
{
    private readonly BackupModuleOptions _options = options.Value;

    public async Task<(DatabasePlan? Plan, string? Error)> PlanAsync(Guid serviceId, CancellationToken ct)
    {
        // Unfiltered: reached from background jobs that run unscoped. The caller establishes
        // ownership before asking.
        var service = await db.ManagedServices.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId, ct);

        if (service is null) return (null, "That database no longer exists.");

        var engine = DatabaseDumpCommands.EngineFor(service.Type);
        if (engine is null) return (null, $"{service.Type} is not a database this module can export.");

        if (DatabaseDumpCommands.WhyUnsupported(engine.Value) is { } unsupported)
            return (null, unsupported);

        if (!providers.TryResolve(engine.Value, out _))
            return (null, $"No backup provider is registered for {engine.Value}.");

        var definition = ServiceCatalog.All[service.Type];

        string password;
        try
        {
            password = protector.Unprotect(service.EncryptedPassword);
        }
        catch (Exception ex)
        {
            // Named plainly: an empty password produces an authentication error nobody would trace
            // back to a key problem.
            logger.LogError(ex, "The stored password for service {ServiceId} could not be decrypted.", serviceId);
            return (null,
                "This database's stored password could not be decrypted, so it cannot be exported. " +
                "The platform master key has most likely changed since it was created.");
        }

        var workspaceSlug = await db.Workspaces.IgnoreQueryFilters()
            .Where(w => w.Id == service.WorkspaceId).Select(w => w.Slug).FirstOrDefaultAsync(ct);

        var connection = new DatabaseConnection(
            service.ContainerName, definition.Port, service.Username, password, service.DatabaseName);

        var execution = new DatabaseExecutionContext(
            // The database's OWN image at ITS version, so the client always matches the server.
            $"{definition.ImageRepo}:{service.Version}",
            workspaceSlug is null ? null : runtime.Value.WorkspaceNetwork(workspaceSlug),
            _options.StagingVolume,
            DatabaseDumpCommands.ContainerMountPath);

        return (new DatabasePlan(engine.Value, connection, execution, service.Name), null);
    }

    public async Task<TargetLease> StageAsync(Guid serviceId, Guid snapshotId, CancellationToken ct)
    {
        var (plan, error) = await PlanAsync(serviceId, ct);
        if (plan is null) return TargetLease.Fail(error!);

        if (!providers.TryResolve(plan.Engine, out var provider))
            return TargetLease.Fail($"No backup provider is registered for {plan.Engine}.");

        // A directory of its own per dump: it becomes the snapshot's source, and two concurrent
        // exports must never write into the same place. Named from the snapshot rather than a fresh
        // Guid so a crash mid-export leaves something the row can still point at.
        var stageName = BackupStagingLayout.DatabaseDirectory(snapshotId);
        var stagePath = Path.Combine(_options.StagingDirectory, stageName);

        try
        {
            // A retry of the same snapshot lands here again. Clear it first: a truncated dump from
            // the attempt that crashed must not be archived as though it were a whole database.
            // Safe because one execution per snapshot gets this far — BackupSnapshotService.RunAsync
            // refuses one already Preparing or Running. That is an ORDERING argument, not a lock:
            // RunAsync reads the row, tests it, and only then writes Preparing, with no concurrency
            // token across the gap. Nothing interleaves inside it today (one job row per snapshot
            // id, and the worker reserves in process before stamping its claim), but this comment is
            // what licenses a recursive delete, so the assumption is named rather than implied.
            Cleanup(stagePath);
            Directory.CreateDirectory(stagePath);
            RestrictPermissions(stagePath);

            var result = await provider.CreateBackupAsync(new DatabaseBackupContext(
                plan.Engine,
                plan.Connection,
                plan.Execution with
                {
                    // The client writes into <mount>/<stageName>/, which is this directory as the
                    // panel sees it.
                    ContainerMountPath = $"{DatabaseDumpCommands.ContainerMountPath}/{stageName}"
                },
                stagePath,
                "dump"), ct);

            if (!result.Succeeded)
            {
                Cleanup(stagePath);
                return TargetLease.Fail(result.Error ?? "The database could not be exported.");
            }

            return TargetLease.Ok(stagePath, () =>
            {
                // A dump is the database's entire contents in the clear. It goes as soon as the
                // snapshot that needed it is finished, successfully or not.
                Cleanup(stagePath);
                return ValueTask.CompletedTask;
            });
        }
        catch (Exception ex)
        {
            Cleanup(stagePath);
            logger.LogError(ex, "Exporting database {ServiceId} failed.", serviceId);
            return TargetLease.Fail($"The database could not be exported: {ex.Message}");
        }
    }

    /// <summary>
    /// Narrow the dump directory to the owner.
    ///
    /// <para>
    /// Best-effort and Unix-only: <see cref="File.SetUnixFileMode"/> does nothing on Windows, where
    /// the staging area is a development path rather than a shared server directory. Failure is
    /// logged rather than fatal — a backup should not be refused because a filesystem does not carry
    /// permissions, but nobody should assume it was tightened either.
    /// </para>
    /// </summary>
    private void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not restrict permissions on the dump directory {Path}.", path);
        }
    }

    private void Cleanup(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            // Loud: a dump left behind is a plaintext copy of an entire database sitting on disk.
            logger.LogWarning(ex, "A database dump could not be removed from {Path}.", path);
        }
    }
}
