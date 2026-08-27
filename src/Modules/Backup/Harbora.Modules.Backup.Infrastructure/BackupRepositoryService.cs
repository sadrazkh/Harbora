using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>What a caller asks for when adding a repository. Secrets are write-only.</summary>
public sealed record NewRepositoryRequest(
    string Name,
    BackupRepositoryType Type,
    BackupEngineKind Engine,
    string Password,
    string? LocalPath = null,
    string? Endpoint = null,
    string? Bucket = null,
    string? Region = null,
    string? BasePath = null,
    string? AccessKeyId = null,
    string? SecretAccessKey = null);

public sealed record RepositoryOutcome(bool Succeeded, Guid? RepositoryId = null, string? Error = null);

/// <summary>
/// Creating, checking and removing repositories.
///
/// <para>
/// The only place a repository row is written, so credential encryption happens once and cannot be
/// forgotten by a caller. Nothing here ever returns a secret — not masked, not round-tripped.
/// </para>
/// </summary>
public sealed class BackupRepositoryService(
    HarboraDbContext db,
    IBackupEngineResolver engines,
    ISecretProtector protector,
    IAuditLogger audit,
    ICurrentUser currentUser,
    ILogger<BackupRepositoryService> logger)
{
    public async Task<RepositoryOutcome> CreateAsync(
        Guid workspaceId, NewRepositoryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!EngineArgumentGuard.IsSafeName(request.Name))
            return new RepositoryOutcome(false, Error:
                "Use letters, digits, spaces, dots, hyphens or underscores for the name.");

        if (string.IsNullOrWhiteSpace(request.Password))
            return new RepositoryOutcome(false, Error:
                "A repository needs a password. Without it nothing in it can be read — including by you.");

        if (request.Bucket is { Length: > 0 } bucket && !EngineArgumentGuard.IsSafeBucket(bucket))
            return new RepositoryOutcome(false, Error: "That bucket name is not valid.");

        var duplicate = await db.BackupRepositories
            .AnyAsync(r => r.WorkspaceId == workspaceId && r.Name == request.Name, ct);
        if (duplicate)
            return new RepositoryOutcome(false, Error: $"A repository called '{request.Name}' already exists.");

        var repository = new BackupRepository
        {
            WorkspaceId = workspaceId,
            Name = request.Name,
            Type = request.Type,
            Engine = request.Engine,
            Endpoint = request.Endpoint,
            Bucket = request.Bucket,
            Region = request.Region,
            BasePath = request.BasePath ?? request.LocalPath,
            Status = BackupRepositoryStatus.Pending,
            EncryptedPassword = protector.Protect(request.Password)
        };

        if (request.AccessKeyId is not null || request.SecretAccessKey is not null)
        {
            repository.EncryptedCredentials = protector.Protect(JsonSerializer.Serialize(
                new RepositoryCredentials(request.AccessKeyId, request.SecretAccessKey)));
        }

        // The engine is asked to open the repository BEFORE the row is committed as usable. A row
        // that says Ready for a bucket that does not exist is how a backup schedule runs green for a
        // month and has nothing in it.
        var engine = engines.Resolve(request.Engine);
        var result = await engine.CreateRepositoryAsync(new CreateBackupRepositoryRequest(
            repository.Id,
            repository.Name,
            repository.Type,
            request.Password,
            request.LocalPath,
            request.Endpoint,
            request.Bucket,
            request.Region,
            request.BasePath,
            new RepositoryCredentials(request.AccessKeyId, request.SecretAccessKey)), ct);

        if (!result.Succeeded)
            return new RepositoryOutcome(false, Error: result.Error);

        repository.Status = BackupRepositoryStatus.Ready;
        repository.EngineRepositoryId = result.EngineRepositoryId;
        repository.LastHealthCheckAt = DateTimeOffset.UtcNow;
        repository.LastSuccessfulHealthCheckAt = DateTimeOffset.UtcNow;

        db.BackupRepositories.Add(repository);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("backup.repository.create", "BackupRepository", repository.Id.ToString(),
            metadataJson: JsonSerializer.Serialize(new
            {
                repository.Name,
                Type = repository.Type.ToString(),
                Engine = repository.Engine.ToString(),
                result.AlreadyExisted
            }),
            workspaceId: repository.WorkspaceId, ct: ct);

        logger.LogInformation("Repository {RepositoryId} ready ({Engine}, existing={Existing}).",
            repository.Id, repository.Engine, result.AlreadyExisted);

        return new RepositoryOutcome(true, repository.Id);
    }

    /// <summary>
    /// Re-check a repository and record the verdict.
    ///
    /// <para>
    /// <c>LastHealthCheckAt</c> moves every time; <c>LastSuccessfulHealthCheckAt</c> only on success.
    /// The gap between them is what tells an operator how long something has been broken, which
    /// a single "last checked" timestamp cannot.
    /// </para>
    /// </summary>
    public async Task<BackupRepositoryHealthResult> CheckHealthAsync(Guid repositoryId, CancellationToken ct)
    {
        // Unfiltered: also called from the unscoped health-check job.
        var repository = await db.BackupRepositories.IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct);

        if (repository is null)
            return new BackupRepositoryHealthResult(false, false, Error: "That repository no longer exists.");

        var engine = engines.Resolve(repository.Engine);
        var health = await engine.CheckHealthAsync(repositoryId, ct);

        repository.LastHealthCheckAt = health.CheckedAt ?? DateTimeOffset.UtcNow;
        repository.LastError = health.Error;

        if (health.Reachable && health.Intact)
        {
            repository.LastSuccessfulHealthCheckAt = repository.LastHealthCheckAt;
            repository.Status = BackupRepositoryStatus.Ready;
        }
        else
        {
            repository.Status = health.Reachable
                ? BackupRepositoryStatus.Degraded
                : BackupRepositoryStatus.Unavailable;
        }

        if (health.TotalSizeBytes is { } size) repository.StorageUsageBytes = size;
        if (health.SnapshotCount is { } count) repository.SnapshotCount = count;

        await db.SaveChangesAsync(ct);
        return health;
    }

    /// <summary>
    /// Remove a repository. Refuses while anything still points at it.
    ///
    /// <para>
    /// Cascade would be the convenient choice and the wrong one: deleting a repository row would
    /// silently take its snapshot history with it, leaving artifacts in a bucket that nothing knows
    /// about and a restore path that no longer exists.
    /// </para>
    /// </summary>
    public async Task<RepositoryOutcome> DeleteAsync(Guid repositoryId, CancellationToken ct)
    {
        var repository = await db.BackupRepositories.FirstOrDefaultAsync(r => r.Id == repositoryId, ct);
        if (repository is null) return new RepositoryOutcome(false, Error: "That repository no longer exists.");

        var policies = await db.BackupPolicies.CountAsync(p => p.RepositoryId == repositoryId, ct);
        if (policies > 0)
            return new RepositoryOutcome(false, Error:
                $"{policies} backup polic{(policies == 1 ? "y" : "ies")} still store backups here. " +
                "Remove or repoint them first.");

        var snapshots = await db.BackupSnapshots.CountAsync(s => s.RepositoryId == repositoryId, ct);
        if (snapshots > 0)
            return new RepositoryOutcome(false, Error:
                $"This repository still holds {snapshots} snapshot(s). Delete them first if you are sure.");

        db.BackupRepositories.Remove(repository);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("backup.repository.delete", "BackupRepository", repositoryId.ToString(),
            userIdOverride: currentUser.UserId, workspaceId: repository.WorkspaceId, ct: ct);

        return new RepositoryOutcome(true, repositoryId);
    }

    public Task<List<BackupRepository>> ListAsync(CancellationToken ct) =>
        db.BackupRepositories.AsNoTracking().OrderBy(r => r.Name).ToListAsync(ct);
}
