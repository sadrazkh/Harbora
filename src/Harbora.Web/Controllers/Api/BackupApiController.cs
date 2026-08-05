using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// Versioned JSON API for the backup module.
///
/// <para>
/// Errors are RFC 7807 Problem Details, which differs from <see cref="ApiV1Controller"/>'s
/// <c>{ "error": "..." }</c> shape. Deliberate: the brief asked for Problem Details, and this is a
/// new surface with no clients, so adopting the standard here costs nothing — where changing the
/// existing endpoints would break the CLI.
/// </para>
/// <para>
/// Every action 404s while <c>Features:Backup</c> is off, so the routes do not exist rather than
/// existing and refusing. Secrets are never returned: not the repository password, not access keys,
/// not even masked.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/backup")]
[Authorize(AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class BackupApiController(
    HarboraDbContext db,
    BackupRepositoryService repositories,
    BackupSnapshotService snapshots,
    BackupPolicyService policies,
    RestoreService restores,
    IBackupTargetResolver targets,
    IIdempotencyStore idempotency,
    ICurrentUser currentUser,
    IOptions<BackupFeatureOptions> features,
    IOptions<BackupModuleOptions> moduleOptions) : ControllerBase
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool Disabled => !features.Value.Backup;

    /// <summary>Largest page a caller may ask for. Beyond this, a "limit" is not a limit.</summary>
    private const int MaxPageSize = 200;

    // ---- repositories ---------------------------------------------------------------------

    [HttpGet("repositories")]
    [ProducesResponseType<PagedResponse<RepositoryDto>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListRepositories(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? sort = null,
        CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.BackupRepositories.AsNoTracking();

        if (status is { Length: > 0 })
        {
            if (!Enum.TryParse<BackupRepositoryStatus>(status, true, out var parsed))
                return Invalid("status", $"'{status}' is not a repository status.");
            query = query.Where(r => r.Status == parsed);
        }

        query = sort switch
        {
            "name" => query.OrderBy(r => r.Name),
            "-name" => query.OrderByDescending(r => r.Name),
            "-created" => query.OrderByDescending(r => r.CreatedAt),
            "created" => query.OrderBy(r => r.CreatedAt),
            null or "" => query.OrderBy(r => r.Name),
            _ => query
        };

        if (sort is { Length: > 0 } and not ("name" or "-name" or "created" or "-created"))
            return Invalid("sort", $"'{sort}' is not a sortable field.");

        return Ok(await PageAsync(query, page, pageSize, RepositoryDto.From, ct));
    }

    [HttpPost("repositories")]
    [ProducesResponseType<RepositoryDto>(StatusCodes.Status201Created)]
    public async Task<IActionResult> CreateRepository(
        [FromBody] CreateRepositoryBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var result = await repositories.CreateAsync(WorkspaceId, new NewRepositoryRequest(
            body.Name, body.Type, body.Engine, body.Password, body.LocalPath, body.Endpoint,
            body.Bucket, body.Region, body.BasePath ?? body.LocalPath,
            body.AccessKeyId, body.SecretAccessKey), ct);

        if (!result.Succeeded) return Invalid("repository", result.Error!);

        var created = await db.BackupRepositories.AsNoTracking()
            .FirstAsync(r => r.Id == result.RepositoryId, ct);

        return CreatedAtAction(nameof(GetRepository), new { id = created.Id }, RepositoryDto.From(created));
    }

    [HttpGet("repositories/{id:guid}")]
    public async Task<IActionResult> GetRepository(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var repository = await db.BackupRepositories.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return repository is null ? NotFound() : Ok(RepositoryDto.From(repository));
    }

    [HttpPost("repositories/{id:guid}/health")]
    public async Task<IActionResult> CheckRepository(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        // Scoped read first: a repository in another workspace must 404, not be health-checked.
        if (!await db.BackupRepositories.AnyAsync(r => r.Id == id, ct)) return NotFound();

        var health = await repositories.CheckHealthAsync(id, ct);
        return Ok(new
        {
            reachable = health.Reachable,
            intact = health.Intact,
            checkedAt = health.CheckedAt,
            error = health.Error
        });
    }

    [HttpDelete("repositories/{id:guid}")]
    public async Task<IActionResult> DeleteRepository(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (!await db.BackupRepositories.AnyAsync(r => r.Id == id, ct)) return NotFound();

        var result = await repositories.DeleteAsync(id, ct);
        return result.Succeeded ? NoContent() : Conflict(ProblemFor("repository", result.Error!));
    }

    // ---- targets --------------------------------------------------------------------------

    /// <summary>
    /// What this deployment can actually back up.
    ///
    /// <para>
    /// Exposed so a client does not have to guess: directory sources depend on configuration that
    /// fails closed, and volume/application/database support differs by what is implemented.
    /// </para>
    /// </summary>
    [HttpGet("targets")]
    public IActionResult ListTargets()
    {
        if (Disabled) return NotFound();

        var supported = new[]
        {
            BackupTargetType.Directory, BackupTargetType.DockerVolume, BackupTargetType.Database
        };

        return Ok(new
        {
            supported = supported.Select(t => t.ToString()),
            allowedSourceRoots = moduleOptions.Value.AllowedSourceRoots,
            unsupported = Enum.GetValues<BackupTargetType>()
                .Except(supported)
                .Select(t => new { type = t.ToString(), reason = targets.Validate(t, "probe").Error })
        });
    }

    // ---- policies -------------------------------------------------------------------------

    [HttpGet("policies")]
    public async Task<IActionResult> ListPolicies(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] bool? enabled = null, CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.BackupPolicies.AsNoTracking();
        if (enabled is { } wanted) query = query.Where(p => p.Enabled == wanted);

        return Ok(await PageAsync(query.OrderBy(p => p.Name), page, pageSize, PolicyDto.From, ct));
    }

    [HttpPost("policies")]
    public async Task<IActionResult> CreatePolicy([FromBody] CreatePolicyBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var policy = new BackupPolicy
        {
            WorkspaceId = WorkspaceId,
            Name = body.Name,
            RepositoryId = body.RepositoryId,
            TargetType = body.TargetType,
            TargetRef = body.TargetRef,
            Schedule = body.Schedule,
            Timezone = string.IsNullOrWhiteSpace(body.Timezone) ? "UTC" : body.Timezone,
            Enabled = body.Enabled,
            Retention = new RetentionPolicy
            {
                KeepLatest = body.KeepLatest,
                KeepHourly = body.KeepHourly,
                KeepDaily = body.KeepDaily,
                KeepWeekly = body.KeepWeekly,
                KeepMonthly = body.KeepMonthly,
                KeepYearly = body.KeepYearly,
                MaximumAgeDays = body.MaximumAgeDays
            }
        };

        var result = await policies.SaveAsync(policy, ct);
        if (!result.Succeeded)
        {
            // One entry per field, so a client can attach each message to the input that caused it.
            var problem = new ValidationProblemDetails(
                result.Errors!.GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
            {
                Title = "The policy was not saved.",
                Status = StatusCodes.Status400BadRequest
            };
            return BadRequest(problem);
        }

        var saved = await db.BackupPolicies.AsNoTracking().FirstAsync(p => p.Id == result.PolicyId, ct);
        return CreatedAtAction(nameof(ListPolicies), new { }, PolicyDto.From(saved));
    }

    [HttpDelete("policies/{id:guid}")]
    public async Task<IActionResult> DeletePolicy(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        return await policies.DeleteAsync(id, ct) ? NoContent() : NotFound();
    }

    // ---- snapshots ------------------------------------------------------------------------

    [HttpGet("snapshots")]
    public async Task<IActionResult> ListSnapshots(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] Guid? repositoryId = null, [FromQuery] string? status = null,
        [FromQuery] string? targetRef = null, [FromQuery] string? sort = "-created",
        CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.BackupSnapshots.AsNoTracking();

        if (repositoryId is { } repo) query = query.Where(s => s.RepositoryId == repo);
        if (targetRef is { Length: > 0 }) query = query.Where(s => s.TargetRef == targetRef);

        if (status is { Length: > 0 })
        {
            if (!Enum.TryParse<BackupSnapshotStatus>(status, true, out var parsed))
                return Invalid("status", $"'{status}' is not a snapshot status.");
            query = query.Where(s => s.Status == parsed);
        }

        query = sort switch
        {
            "-created" or null or "" => query.OrderByDescending(s => s.CreatedAt),
            "created" => query.OrderBy(s => s.CreatedAt),
            "-size" => query.OrderByDescending(s => s.StoredSizeBytes),
            "size" => query.OrderBy(s => s.StoredSizeBytes),
            _ => query
        };

        if (sort is { Length: > 0 } and not ("created" or "-created" or "size" or "-size"))
            return Invalid("sort", $"'{sort}' is not a sortable field.");

        return Ok(await PageAsync(query, page, pageSize, SnapshotDto.From, ct));
    }

    [HttpGet("snapshots/{id:guid}")]
    public async Task<IActionResult> GetSnapshot(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var snapshot = await db.BackupSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return snapshot is null ? NotFound() : Ok(SnapshotDto.From(snapshot));
    }

    [HttpGet("snapshots/{id:guid}/entries")]
    public async Task<IActionResult> BrowseSnapshot(Guid id, [FromQuery] string path = "", CancellationToken ct = default)
    {
        if (Disabled) return NotFound();
        if (!await db.BackupSnapshots.AnyAsync(s => s.Id == id, ct)) return NotFound();

        var entries = await snapshots.BrowseAsync(id, path, ct);
        return Ok(entries.Select(e => new
        {
            name = e.Name,
            path = e.RelativePath,
            isDirectory = e.IsDirectory,
            sizeBytes = e.SizeBytes,
            modifiedAt = e.ModifiedAt
        }));
    }

    /// <summary>
    /// Queue a backup. Honours <c>Idempotency-Key</c>.
    /// </summary>
    [HttpPost("snapshots")]
    public async Task<IActionResult> CreateSnapshot([FromBody] CreateSnapshotBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var replay = await TryReplayAsync("snapshots", ct);
        if (replay.Invalid is { } invalidKey) return invalidKey;
        if (replay.ExistingId is { } existing)
            return Accepted(new { snapshotId = existing, replayed = true });

        var result = await snapshots.QueueAsync(
            WorkspaceId, body.RepositoryId, body.TargetType, body.TargetRef,
            null, BackupTrigger.Api, ct);

        if (!result.Succeeded) return Conflict(ProblemFor("snapshot", result.Error!));

        await RememberAsync("snapshots", result.SnapshotId!.Value, ct);
        return Accepted(new { snapshotId = result.SnapshotId, replayed = false });
    }

    [HttpDelete("snapshots/{id:guid}")]
    public async Task<IActionResult> DeleteSnapshot(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (!await db.BackupSnapshots.AnyAsync(s => s.Id == id, ct)) return NotFound();

        var result = await snapshots.DeleteAsync(id, ct);
        return result.Succeeded ? NoContent() : Conflict(ProblemFor("snapshot", result.Error!));
    }

    // ---- restore --------------------------------------------------------------------------

    [HttpGet("restore-jobs")]
    public async Task<IActionResult> ListRestores(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        return Ok(await PageAsync(
            db.RestoreJobs.AsNoTracking().OrderByDescending(r => r.CreatedAt),
            page, pageSize, RestoreDto.From, ct));
    }

    [HttpGet("restore-jobs/{id:guid}")]
    public async Task<IActionResult> GetRestore(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var job = await db.RestoreJobs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);
        return job is null ? NotFound() : Ok(RestoreDto.From(job));
    }

    /// <summary>
    /// Queue a restore. Honours <c>Idempotency-Key</c>, and it matters most here: a retried restore
    /// that ran twice would overwrite data a second time.
    /// </summary>
    [HttpPost("restore-jobs")]
    public async Task<IActionResult> CreateRestore([FromBody] CreateRestoreBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var replay = await TryReplayAsync("restore-jobs", ct);
        if (replay.Invalid is { } invalidKey) return invalidKey;
        if (replay.ExistingId is { } existing)
            return Accepted(new { restoreJobId = existing, replayed = true });

        var result = await restores.QueueAsync(WorkspaceId, new RestoreRequest(
            body.SnapshotId,
            body.RestoreType,
            body.Destination,
            body.ConflictStrategy,
            body.Entries,
            body.ConfirmationText), ct);

        if (!result.Succeeded) return Conflict(ProblemFor("restore", result.Error!));

        await RememberAsync("restore-jobs", result.RestoreJobId!.Value, ct);
        return Accepted(new { restoreJobId = result.RestoreJobId, replayed = false });
    }

    // ---- helpers --------------------------------------------------------------------------

    private IActionResult Invalid(string field, string message) => BadRequest(ProblemFor(field, message));

    private ProblemDetails ProblemFor(string field, string message) => new()
    {
        Title = message,
        Status = StatusCodes.Status400BadRequest,
        Instance = HttpContext?.Request.Path,
        Extensions = { ["field"] = field }
    };

    /// <summary>
    /// Reads the <c>Idempotency-Key</c> header, if present, and reports what it already produced.
    ///
    /// <para>
    /// Absent header means "no idempotency asked for" — not an error. A present but unusable key IS
    /// an error, because silently ignoring it would give the caller a guarantee they think they have
    /// and do not.
    /// </para>
    /// </summary>
    private async Task<(IActionResult? Invalid, Guid? ExistingId)> TryReplayAsync(
        string endpoint, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var raw)) return (null, null);

        var key = raw.ToString().Trim();
        if (key.Length is 0 or > 128)
            return (Invalid("Idempotency-Key", "The key must be between 1 and 128 characters."), null);

        return (null, await idempotency.FindAsync(endpoint, key, ct));
    }

    private async Task RememberAsync(string endpoint, Guid resultId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var raw)) return;

        await idempotency.RememberAsync(WorkspaceId, endpoint, raw.ToString().Trim(), resultId, ct);
    }

    private static async Task<PagedResponse<TDto>> PageAsync<TEntity, TDto>(
        IQueryable<TEntity> query, int page, int pageSize,
        Func<TEntity, TDto> project, CancellationToken ct)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var total = await query.CountAsync(ct);
        var items = await query.Skip((safePage - 1) * safeSize).Take(safeSize).ToListAsync(ct);

        return new PagedResponse<TDto>(items.Select(project).ToList(), safePage, safeSize, total);
    }
}

/// <summary>A page of results plus what a client needs to ask for the next one.</summary>
public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
    public bool HasMore => Page < TotalPages;
}

// ---- request bodies -------------------------------------------------------------------------

/// <summary>
/// <c>Password</c>, <c>AccessKeyId</c> and <c>SecretAccessKey</c> are write-only: they are accepted
/// here and never appear in any response.
/// </summary>
public sealed record CreateRepositoryBody(
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

public sealed record CreatePolicyBody(
    string Name,
    Guid RepositoryId,
    BackupTargetType TargetType,
    string TargetRef,
    string Schedule,
    string? Timezone = "UTC",
    bool Enabled = true,
    int KeepLatest = 3,
    int KeepHourly = 0,
    int KeepDaily = 30,
    int KeepWeekly = 0,
    int KeepMonthly = 12,
    int KeepYearly = 0,
    int? MaximumAgeDays = null);

public sealed record CreateSnapshotBody(Guid RepositoryId, BackupTargetType TargetType, string TargetRef);

public sealed record CreateRestoreBody(
    Guid SnapshotId,
    string Destination,
    RestoreType RestoreType = RestoreType.Folder,
    RestoreConflictStrategy ConflictStrategy = RestoreConflictStrategy.Fail,
    IReadOnlyList<string>? Entries = null,
    string? ConfirmationText = null);

// ---- responses ------------------------------------------------------------------------------

/// <summary>
/// A repository as the API describes it.
///
/// <para>
/// Note what is absent: no password, no access key, no encrypted blob, not even a masked form.
/// A field that is never serialised cannot leak through a logging middleware or a client that dumps
/// responses.
/// </para>
/// </summary>
public sealed record RepositoryDto(
    Guid Id, string Name, string Type, string Engine, string Status,
    string? Endpoint, string? Bucket, string? Region,
    long StorageUsageBytes, long SnapshotCount,
    DateTimeOffset? LastHealthCheckAt, DateTimeOffset? LastSuccessfulHealthCheckAt,
    string? LastError, bool IsEnabled, DateTimeOffset CreatedAt)
{
    public static RepositoryDto From(BackupRepository r) => new(
        r.Id, r.Name, r.Type.ToString(), r.Engine.ToString(), r.Status.ToString(),
        r.Endpoint, r.Bucket, r.Region, r.StorageUsageBytes, r.SnapshotCount,
        r.LastHealthCheckAt, r.LastSuccessfulHealthCheckAt, r.LastError, r.IsEnabled, r.CreatedAt);
}

public sealed record PolicyDto(
    Guid Id, string Name, bool Enabled, Guid RepositoryId, string TargetType, string TargetRef,
    string Schedule, string Timezone, DateTimeOffset? LastRunAt, DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextRunAt, RetentionDto Retention)
{
    public static PolicyDto From(BackupPolicy p) => new(
        p.Id, p.Name, p.Enabled, p.RepositoryId, p.TargetType.ToString(), p.TargetRef,
        p.Schedule, p.Timezone, p.LastRunAt, p.LastSuccessAt, p.NextRunAt,
        new RetentionDto(p.Retention.KeepLatest, p.Retention.KeepHourly, p.Retention.KeepDaily,
            p.Retention.KeepWeekly, p.Retention.KeepMonthly, p.Retention.KeepYearly,
            p.Retention.MaximumAgeDays));
}

public sealed record RetentionDto(
    int KeepLatest, int KeepHourly, int KeepDaily, int KeepWeekly, int KeepMonthly, int KeepYearly,
    int? MaximumAgeDays);

public sealed record SnapshotDto(
    Guid Id, Guid RepositoryId, Guid? PolicyId, string TargetType, string TargetRef,
    string Status, string VerificationStatus, string? VerificationNote,
    long OriginalSizeBytes, long StoredSizeBytes, long DeduplicatedSizeBytes, long FilesCount,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, double? DurationSeconds,
    string? FailureReason, string TriggeredBy, DateTimeOffset CreatedAt)
{
    public static SnapshotDto From(BackupSnapshot s) => new(
        s.Id, s.RepositoryId, s.PolicyId, s.TargetType.ToString(), s.TargetRef,
        s.Status.ToString(), s.VerificationStatus.ToString(), s.VerificationNote,
        s.OriginalSizeBytes, s.StoredSizeBytes, s.DeduplicatedSizeBytes, s.FilesCount,
        s.StartedAt, s.CompletedAt, s.Duration?.TotalSeconds,
        s.FailureReason, s.TriggeredBy.ToString(), s.CreatedAt);
}

public sealed record RestoreDto(
    Guid Id, Guid SnapshotId, string RestoreType, string Destination, bool OverwritesLiveTarget,
    string ConflictStrategy, string Status, int Progress,
    long RestoredFilesCount, long RestoredBytes,
    DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, string? FailureReason,
    DateTimeOffset CreatedAt)
{
    public static RestoreDto From(RestoreJob r) => new(
        r.Id, r.SnapshotId, r.RestoreType.ToString(), r.Destination, r.OverwritesLiveTarget,
        r.ConflictStrategy.ToString(), r.Status.ToString(), r.Progress,
        r.RestoredFilesCount, r.RestoredBytes, r.StartedAt, r.CompletedAt, r.FailureReason,
        r.CreatedAt);
}
