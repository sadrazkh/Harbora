using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Harbora.Modules.Sync.Infrastructure;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// Versioned JSON API for the sync module.
///
/// <para>
/// Same conventions as <see cref="BackupApiController"/> — Problem Details, paging, filtering,
/// idempotency keys — and deliberately no endpoint they share. There is no <c>/restore</c> here and
/// there never will be: a sync space has no earlier state to go back to, and offering one would be
/// the confusion this module exists to prevent (THREAT_MODEL T9).
/// </para>
/// <para>
/// Every route 404s while <c>Features:Sync</c> is off. The folder encryption password is write-only:
/// it is accepted on a pairing and never appears in any response.
/// </para>
/// </summary>
[ApiController]
[Route("api/v1/sync")]
[Authorize(AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
[Produces("application/json")]
public sealed class SyncApiController(
    HarboraDbContext db,
    SyncSpaceService spaces,
    ISyncEngine engine,
    IIdempotencyStore idempotency,
    ICurrentUser currentUser,
    IOptions<SyncFeatureOptions> features,
    IOptions<SyncModuleOptions> moduleOptions) : ControllerBase
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool Disabled => !features.Value.Sync;

    private const int MaxPageSize = 200;

    // ---- this node ---------------------------------------------------------------------------

    /// <summary>
    /// This node's own identity and what it will accept.
    ///
    /// <para>
    /// The device id has to be given to the other end before anything can pair, so a client should
    /// not have to go and read Syncthing's config to find it.
    /// </para>
    /// </summary>
    [HttpGet("node")]
    public async Task<IActionResult> GetNode(CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var deviceId = await engine.GetLocalDeviceIdAsync(ct);

        return Ok(new
        {
            deviceId,
            engineReachable = deviceId is not null,
            allowedRoots = moduleOptions.Value.AllowedRoots,
            encryptedNodeAllowed = moduleOptions.Value.AllowEncryptedNode,
            // Said in the payload, not just the docs: a client building a UI on this should repeat it.
            notice = "Sync replicates deletions. It is not a backup."
        });
    }

    // ---- spaces ------------------------------------------------------------------------------

    [HttpGet("spaces")]
    public async Task<IActionResult> ListSpaces(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] string? status = null, [FromQuery] string? sort = null,
        CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.SyncSpaces.AsNoTracking();

        if (status is { Length: > 0 })
        {
            if (!Enum.TryParse<SyncSpaceStatus>(status, true, out var parsed))
                return Invalid("status", $"'{status}' is not a sync status.");
            query = query.Where(s => s.Status == parsed);
        }

        if (sort is { Length: > 0 } and not ("name" or "-name" or "pending" or "-pending"))
            return Invalid("sort", $"'{sort}' is not a sortable field.");

        query = sort switch
        {
            "-name" => query.OrderByDescending(s => s.Name),
            "pending" => query.OrderBy(s => s.PendingFiles),
            "-pending" => query.OrderByDescending(s => s.PendingFiles),
            _ => query.OrderBy(s => s.Name)
        };

        return Ok(await PageAsync(query, page, pageSize, SyncSpaceDto.From, ct));
    }

    [HttpGet("spaces/{id:guid}")]
    public async Task<IActionResult> GetSpace(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var space = await db.SyncSpaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct);
        return space is null ? NotFound() : Ok(SyncSpaceDto.From(space));
    }

    [HttpPost("spaces")]
    public async Task<IActionResult> CreateSpace([FromBody] CreateSyncSpaceBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var replay = await TryReplayAsync("spaces", ct);
        if (replay.Invalid is { } invalid) return invalid;
        if (replay.ExistingId is { } existing)
            return Accepted(new { spaceId = existing, replayed = true });

        var result = await spaces.CreateSpaceAsync(WorkspaceId, new SyncSpace
        {
            Name = body.Name,
            LocalPath = body.LocalPath,
            Mode = body.Mode,
            VersioningMode = body.VersioningMode,
            VersioningParameter = body.VersioningParameter,
            IgnorePatterns = body.IgnorePatterns is { Count: > 0 }
                ? string.Join('\n', body.IgnorePatterns)
                : null
        }, ct);

        if (!result.Succeeded) return Problem(result);

        await RememberAsync("spaces", result.Id!.Value, ct);

        var created = await db.SyncSpaces.AsNoTracking().FirstAsync(s => s.Id == result.Id, ct);
        return CreatedAtAction(nameof(GetSpace), new { id = created.Id }, SyncSpaceDto.From(created));
    }

    [HttpPost("spaces/{id:guid}/pause")]
    public async Task<IActionResult> SetPaused(Guid id, [FromQuery] bool paused = true, CancellationToken ct = default)
    {
        if (Disabled) return NotFound();
        if (!await db.SyncSpaces.AnyAsync(s => s.Id == id, ct)) return NotFound();

        var result = await spaces.SetPausedAsync(id, paused, ct);
        return result.Succeeded ? NoContent() : Problem(result);
    }

    [HttpPost("spaces/{id:guid}/refresh")]
    public async Task<IActionResult> RefreshSpace(Guid id, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (!await db.SyncSpaces.AnyAsync(s => s.Id == id, ct)) return NotFound();

        await spaces.RefreshAsync(id, ct);

        var space = await db.SyncSpaces.AsNoTracking().FirstAsync(s => s.Id == id, ct);
        return Ok(SyncSpaceDto.From(space));
    }

    // ---- devices -----------------------------------------------------------------------------

    [HttpGet("devices")]
    public async Task<IActionResult> ListDevices(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] bool? untrusted = null, CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.SyncDevices.AsNoTracking();
        if (untrusted is { } wanted) query = query.Where(d => d.IsUntrusted == wanted);

        return Ok(await PageAsync(query.OrderBy(d => d.Name), page, pageSize, SyncDeviceDto.From, ct));
    }

    [HttpPost("devices")]
    public async Task<IActionResult> RegisterDevice(
        [FromBody] RegisterSyncDeviceBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var result = await spaces.RegisterDeviceAsync(
            WorkspaceId, body.Name, body.DeviceId, body.Untrusted, ct);

        if (!result.Succeeded) return Problem(result);

        var created = await db.SyncDevices.AsNoTracking().FirstAsync(d => d.Id == result.Id, ct);
        return CreatedAtAction(nameof(ListDevices), new { }, SyncDeviceDto.From(created));
    }

    // ---- pairings ----------------------------------------------------------------------------

    [HttpGet("pairings")]
    public async Task<IActionResult> ListPairings(
        [FromQuery] Guid? spaceId = null, CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.SyncSpaceMembers.AsNoTracking();
        if (spaceId is { } id) query = query.Where(m => m.SyncSpaceId == id);

        var members = await query.ToListAsync(ct);
        return Ok(members.Select(SyncPairingDto.From));
    }

    /// <summary>
    /// Share a space with a device.
    ///
    /// <para>
    /// Idempotent: a retry returns the original pairing rather than a second attempt. The service
    /// also refuses a duplicate outright, so both layers agree.
    /// </para>
    /// </summary>
    [HttpPost("pairings")]
    public async Task<IActionResult> CreatePairing([FromBody] CreatePairingBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");

        var replay = await TryReplayAsync("pairings", ct);
        if (replay.Invalid is { } invalid) return invalid;
        if (replay.ExistingId is { } existing)
            return Accepted(new { pairingId = existing, replayed = true });

        var result = await spaces.PairAsync(
            body.SpaceId, body.DeviceId, body.Mode, body.EncryptionPassword, ct);

        if (!result.Succeeded) return Problem(result);

        await RememberAsync("pairings", result.Id!.Value, ct);

        // Reported rather than assumed: pairing is mutual, and nothing moves until the other end
        // adds this node too.
        return Accepted(new { pairingId = result.Id, acceptedByPeer = false, replayed = false });
    }

    [HttpDelete("pairings")]
    public async Task<IActionResult> DeletePairing(
        [FromQuery] Guid spaceId, [FromQuery] Guid deviceId, CancellationToken ct)
    {
        if (Disabled) return NotFound();

        var result = await spaces.UnpairAsync(spaceId, deviceId, ct);
        if (!result.Succeeded) return Problem(result);

        return Ok(new
        {
            removed = true,
            // Sync is not remote wipe, and a client should not be able to imply that it is.
            notice = "The device keeps the files it already has."
        });
    }

    // ---- conflicts ---------------------------------------------------------------------------

    [HttpGet("conflicts")]
    public async Task<IActionResult> ListConflicts(
        [FromQuery] int page = 1, [FromQuery] int pageSize = 50,
        [FromQuery] Guid? spaceId = null, [FromQuery] bool openOnly = true,
        CancellationToken ct = default)
    {
        if (Disabled) return NotFound();

        var query = db.SyncConflicts.AsNoTracking();
        if (spaceId is { } id) query = query.Where(c => c.SyncSpaceId == id);
        if (openOnly) query = query.Where(c => c.Resolution == SyncConflictResolution.Unresolved);

        return Ok(await PageAsync(
            query.OrderByDescending(c => c.DetectedAt), page, pageSize, SyncConflictDto.From, ct));
    }

    /// <summary>
    /// Record what was decided about a conflict.
    ///
    /// <para>
    /// Records only. Harbora does not move or delete either copy — whichever an automatic rule
    /// discarded would be somebody's work, and the file operations belong on the device holding them.
    /// </para>
    /// </summary>
    [HttpPost("conflicts/{id:guid}/resolution")]
    public async Task<IActionResult> ResolveConflict(
        Guid id, [FromBody] ResolveConflictBody body, CancellationToken ct)
    {
        if (Disabled) return NotFound();
        if (body is null) return Invalid("body", "A request body is required.");
        if (!await db.SyncConflicts.AnyAsync(c => c.Id == id, ct)) return NotFound();

        var result = await spaces.ResolveConflictAsync(id, body.Resolution, ct);
        if (!result.Succeeded) return Problem(result);

        return Ok(new
        {
            recorded = true,
            notice = "Harbora recorded your decision. No file was moved or deleted."
        });
    }

    // ---- helpers -----------------------------------------------------------------------------

    private IActionResult Invalid(string field, string message) => BadRequest(ProblemFor(field, message));

    /// <summary>
    /// Turns a service outcome into Problem Details, keeping per-field errors per-field so a client
    /// can attach each message to the input that caused it.
    /// </summary>
    private IActionResult Problem(SyncOutcome outcome)
    {
        if (outcome.Errors is { Count: > 0 })
        {
            return BadRequest(new ValidationProblemDetails(
                outcome.Errors.GroupBy(e => e.Field)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.Message).ToArray()))
            {
                Title = "The request was not accepted.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        return Conflict(ProblemFor("sync", outcome.Error ?? "The operation did not succeed."));
    }

    private ProblemDetails ProblemFor(string field, string message) => new()
    {
        Title = message,
        Status = StatusCodes.Status400BadRequest,
        Instance = HttpContext?.Request.Path,
        Extensions = { ["field"] = field }
    };

    private async Task<(IActionResult? Invalid, Guid? ExistingId)> TryReplayAsync(
        string endpoint, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var raw)) return (null, null);

        var key = raw.ToString().Trim();
        if (key.Length is 0 or > 128)
            return (Invalid("Idempotency-Key", "The key must be between 1 and 128 characters."), null);

        return (null, await idempotency.FindAsync($"sync:{endpoint}", key, ct));
    }

    private async Task RememberAsync(string endpoint, Guid resultId, CancellationToken ct)
    {
        if (!Request.Headers.TryGetValue("Idempotency-Key", out var raw)) return;

        // Namespaced, so the same key used against the backup API and this one are different
        // requests rather than a confusing replay of an unrelated result.
        await idempotency.RememberAsync(
            WorkspaceId, $"sync:{endpoint}", raw.ToString().Trim(), resultId, ct);
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

// ---- request bodies ---------------------------------------------------------------------------

public sealed record CreateSyncSpaceBody(
    string Name,
    string LocalPath,
    SyncMode Mode = SyncMode.SendAndReceive,
    SyncVersioningMode VersioningMode = SyncVersioningMode.None,
    int VersioningParameter = 0,
    IReadOnlyList<string>? IgnorePatterns = null);

public sealed record RegisterSyncDeviceBody(string Name, string DeviceId, bool Untrusted = false);

/// <summary>
/// <c>EncryptionPassword</c> is write-only, and required only for
/// <see cref="SyncMode.EncryptedReceiveOnly"/>. It never appears in a response.
/// </summary>
public sealed record CreatePairingBody(
    Guid SpaceId,
    Guid DeviceId,
    SyncMode Mode = SyncMode.SendAndReceive,
    string? EncryptionPassword = null);

public sealed record ResolveConflictBody(SyncConflictResolution Resolution);

// ---- responses --------------------------------------------------------------------------------

public sealed record SyncSpaceDto(
    Guid Id, string Name, string LocalPath, string Mode, string Status, bool IsPaused,
    string VersioningMode, int VersioningParameter,
    long PendingFiles, long PendingBytes, long TotalFiles, long TotalBytes,
    int ConflictCount, DateTimeOffset? LastSyncAt, string? LastError, DateTimeOffset CreatedAt)
{
    public static SyncSpaceDto From(SyncSpace s) => new(
        s.Id, s.Name, s.LocalPath, s.Mode.ToString(), s.Status.ToString(), s.IsPaused,
        s.VersioningMode.ToString(), s.VersioningParameter,
        s.PendingFiles, s.PendingBytes, s.TotalFiles, s.TotalBytes,
        s.ConflictCount, s.LastSyncAt, s.LastError, s.CreatedAt);
}

/// <summary>
/// A device as the API describes it. The engine device id IS public — it is a key fingerprint, and
/// exchanging it is how pairing works — but nothing else about the device is.
/// </summary>
public sealed record SyncDeviceDto(
    Guid Id, string Name, string DeviceId, string Status, string ConnectionKind,
    bool IsUntrusted, bool IsLocalNode, string? ClientVersion,
    DateTimeOffset? LastSeenAt, DateTimeOffset CreatedAt)
{
    public static SyncDeviceDto From(SyncDevice d) => new(
        d.Id, d.Name, d.EngineDeviceId, d.Status.ToString(), d.ConnectionKind.ToString(),
        d.IsUntrusted, d.IsLocalNode, d.ClientVersion, d.LastSeenAt, d.CreatedAt);
}

/// <summary>
/// Note what is absent: no folder password, encrypted or otherwise. A field that is never serialised
/// cannot leak through a logging middleware or a client that dumps responses.
/// </summary>
public sealed record SyncPairingDto(
    Guid Id, Guid SpaceId, Guid DeviceId, string Mode, bool AcceptedByPeer, bool IsEncrypted)
{
    public static SyncPairingDto From(SyncSpaceMember m) => new(
        m.Id, m.SyncSpaceId, m.SyncDeviceId, m.Mode.ToString(), m.AcceptedByPeer,
        m.EncryptedFolderPassword is { Length: > 0 });
}

public sealed record SyncConflictDto(
    Guid Id, Guid SpaceId, string Path, string OriginalPath, long SizeBytes,
    DateTimeOffset DetectedAt, string? OriginatingDevice, string Resolution,
    DateTimeOffset? ResolvedAt)
{
    public static SyncConflictDto From(SyncConflict c) => new(
        c.Id, c.SyncSpaceId, c.RelativePath, c.OriginalRelativePath, c.SizeBytes,
        c.DetectedAt, c.OriginatingDevice, c.Resolution.ToString(), c.ResolvedAt);
}
