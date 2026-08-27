using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Shared;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Modules.Sync.Infrastructure;

public sealed record SyncOutcome(
    bool Succeeded, Guid? Id = null, IReadOnlyList<SyncValidationError>? Errors = null, string? Error = null)
{
    public static SyncOutcome Fail(string message) => new(false, Error: message);
    public static SyncOutcome Invalid(IReadOnlyList<SyncValidationError> errors) => new(false, Errors: errors);
}

/// <summary>
/// Creating sync spaces, adding devices to them, and recording what the engine reports back.
///
/// <para>
/// Kept entirely separate from anything in the backup module. The two share a branch and nothing
/// else: a sync space has no snapshots, no retention and no restore, because there is no earlier
/// state to go back to (THREAT_MODEL T9).
/// </para>
/// </summary>
public sealed class SyncSpaceService(
    HarboraDbContext db,
    ISyncEngine engine,
    ISecretProtector protector,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IOptions<SyncModuleOptions> options,
    ILogger<SyncSpaceService> logger)
{
    private readonly SyncModuleOptions _options = options.Value;

    public async Task<SyncOutcome> CreateSpaceAsync(
        Guid workspaceId, SyncSpace space, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(space);

        var errors = SyncValidation.ValidateSpace(space).ToList();
        if (errors.Count > 0) return SyncOutcome.Invalid(errors);

        var confined = ConfineToAllowedRoot(space.LocalPath);
        if (confined is null)
            return SyncOutcome.Fail(_options.AllowedRoots.Count == 0
                ? "No sync directories are configured. Set Sync:Module:AllowedRoots before creating a space."
                : $"'{space.LocalPath}' is not inside any configured sync directory.");

        space.WorkspaceId = workspaceId;
        space.LocalPath = confined;

        if (await db.SyncSpaces.AnyAsync(s => s.WorkspaceId == workspaceId && s.Name == space.Name, ct))
            return SyncOutcome.Fail($"A sync space called '{space.Name}' already exists.");

        var result = await engine.CreateFolderAsync(new CreateSyncFolderRequest(
            space.Id, space.Name, confined, space.Mode, space.VersioningMode,
            space.VersioningParameter,
            space.IgnorePatterns?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            ct);

        if (!result.Succeeded) return SyncOutcome.Fail(result.Error!);

        space.EngineFolderId = result.EngineFolderId;
        space.Status = SyncSpaceStatus.Pending;

        db.SyncSpaces.Add(space);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("sync.space.create", "SyncSpace", space.Id.ToString(),
            userIdOverride: currentUser.UserId, workspaceId: workspaceId, ct: ct);

        return new SyncOutcome(true, space.Id);
    }

    public async Task<SyncOutcome> RegisterDeviceAsync(
        Guid workspaceId, string name, string engineDeviceId, bool untrusted, CancellationToken ct)
    {
        if (!SyncValidation.IsValidDeviceId(engineDeviceId))
            return SyncOutcome.Fail("That does not look like a device id. Copy it from the other device exactly.");

        if (untrusted && !_options.AllowEncryptedNode)
            return SyncOutcome.Fail(
                "Encrypted-only devices are switched off. Enable Sync:Module:AllowEncryptedNode first — " +
                "and note the feature is experimental.");

        var normalised = SyncValidation.NormaliseDeviceId(engineDeviceId);

        if (await db.SyncDevices.AnyAsync(d => d.WorkspaceId == workspaceId && d.EngineDeviceId == normalised, ct))
            return SyncOutcome.Fail("That device is already registered.");

        var device = new SyncDevice
        {
            WorkspaceId = workspaceId,
            Name = name,
            EngineDeviceId = normalised,
            IsUntrusted = untrusted,
            Status = SyncDeviceStatus.PendingPairing
        };

        var result = await engine.RegisterDeviceAsync(
            new RegisterSyncDeviceRequest(device.Id, normalised, name, null, untrusted), ct);

        if (!result.Succeeded) return SyncOutcome.Fail(result.Error!);

        db.SyncDevices.Add(device);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("sync.device.register", "SyncDevice", device.Id.ToString(),
            userIdOverride: currentUser.UserId, workspaceId: workspaceId, ct: ct);

        return new SyncOutcome(true, device.Id);
    }

    /// <summary>
    /// Share a space with a device.
    ///
    /// <para>
    /// The mode check runs before anything reaches the engine, because the mistake it prevents —
    /// sending readable files to a device that exists precisely so it cannot read them — is silent
    /// once made.
    /// </para>
    /// </summary>
    public async Task<SyncOutcome> PairAsync(
        Guid spaceId, Guid deviceId, SyncMode mode, string? encryptionPassword, CancellationToken ct)
    {
        var space = await db.SyncSpaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct);
        if (space is null) return SyncOutcome.Fail("That sync space no longer exists.");

        var device = await db.SyncDevices.FirstOrDefaultAsync(d => d.Id == deviceId, ct);
        if (device is null) return SyncOutcome.Fail("That device no longer exists.");

        if (SyncValidation.ValidateMembership(device, mode, encryptionPassword) is { } problem)
            return SyncOutcome.Invalid([problem]);

        if (await db.SyncSpaceMembers.AnyAsync(m => m.SyncSpaceId == spaceId && m.SyncDeviceId == deviceId, ct))
            return SyncOutcome.Fail($"'{device.Name}' is already in this space.");

        var result = await engine.PairDeviceAsync(
            new PairSyncDeviceRequest(spaceId, deviceId, mode, encryptionPassword), ct);

        if (!result.Succeeded) return SyncOutcome.Fail(result.Error!);

        var member = new SyncSpaceMember
        {
            WorkspaceId = space.WorkspaceId,
            SyncSpaceId = spaceId,
            SyncDeviceId = deviceId,
            Mode = mode,
            AcceptedByPeer = result.AcceptedByPeer,
            // Stored so trusted devices can be told what it is. Never sent to the untrusted device,
            // which would defeat the whole arrangement.
            EncryptedFolderPassword = string.IsNullOrEmpty(encryptionPassword)
                ? null
                : protector.Protect(encryptionPassword)
        };

        db.SyncSpaceMembers.Add(member);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("sync.pair", "SyncSpace", spaceId.ToString(),
            userIdOverride: currentUser.UserId,
            metadataJson: $"{{\"device\":\"{device.Id}\",\"mode\":\"{mode}\"}}",
            workspaceId: space.WorkspaceId, ct: ct);

        return new SyncOutcome(true, member.Id);
    }

    /// <summary>
    /// Remove a device from a space.
    ///
    /// <para>
    /// It keeps the files it already has. Sync is not remote wipe, and the message says so rather
    /// than letting someone assume otherwise.
    /// </para>
    /// </summary>
    public async Task<SyncOutcome> UnpairAsync(Guid spaceId, Guid deviceId, CancellationToken ct)
    {
        var member = await db.SyncSpaceMembers
            .FirstOrDefaultAsync(m => m.SyncSpaceId == spaceId && m.SyncDeviceId == deviceId, ct);

        if (member is null) return SyncOutcome.Fail("That device is not in this space.");

        var result = await engine.UnpairDeviceAsync(
            new PairSyncDeviceRequest(spaceId, deviceId, member.Mode), ct);

        if (!result.Succeeded) return SyncOutcome.Fail(result.Error!);

        db.SyncSpaceMembers.Remove(member);
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("sync.unpair", "SyncSpace", spaceId.ToString(),
            userIdOverride: currentUser.UserId, workspaceId: member.WorkspaceId, ct: ct);

        return new SyncOutcome(true, spaceId);
    }

    public async Task<SyncOutcome> SetPausedAsync(Guid spaceId, bool paused, CancellationToken ct)
    {
        var space = await db.SyncSpaces.FirstOrDefaultAsync(s => s.Id == spaceId, ct);
        if (space is null) return SyncOutcome.Fail("That sync space no longer exists.");

        var result = await engine.SetPausedAsync(spaceId, paused, ct);
        if (!result.Succeeded) return SyncOutcome.Fail(result.Error!);

        space.IsPaused = paused;
        space.Status = paused ? SyncSpaceStatus.Paused : SyncSpaceStatus.Pending;
        await db.SaveChangesAsync(ct);

        return new SyncOutcome(true, spaceId);
    }

    /// <summary>
    /// Refresh one space's status and its conflict list.
    ///
    /// <para>
    /// Conflicts are recorded, not resolved. A conflict that disappears from disk is marked as having
    /// gone rather than deleted from the record, so "this happened and someone dealt with it" stays
    /// visible.
    /// </para>
    /// </summary>
    public async Task RefreshAsync(Guid spaceId, CancellationToken ct)
    {
        var space = await db.SyncSpaces.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == spaceId, ct);
        if (space is null) return;

        var status = await engine.GetFolderStatusAsync(spaceId, ct);

        if (!status.Reachable)
        {
            space.Status = SyncSpaceStatus.Error;
            space.LastError = status.Error;
            await db.SaveChangesAsync(ct);
            return;
        }

        space.LastError = null;
        space.PendingFiles = status.PendingFiles;
        space.PendingBytes = status.PendingBytes;
        space.TotalFiles = status.TotalFiles;
        space.TotalBytes = status.TotalBytes;
        if (status.LastSyncAt is { } at) space.LastSyncAt = at;

        await ReconcileConflictsAsync(space, ct);

        var open = await db.SyncConflicts.IgnoreQueryFilters()
            .CountAsync(c => c.SyncSpaceId == spaceId
                             && c.Resolution == SyncConflictResolution.Unresolved, ct);

        space.ConflictCount = open;
        space.Status = open > 0 && !space.IsPaused ? SyncSpaceStatus.HasConflicts : status.Status;

        await db.SaveChangesAsync(ct);
    }

    private async Task ReconcileConflictsAsync(SyncSpace space, CancellationToken ct)
    {
        var onDisk = await engine.ListConflictsAsync(space.Id, ct);
        var known = await db.SyncConflicts.IgnoreQueryFilters()
            .Where(c => c.SyncSpaceId == space.Id).ToListAsync(ct);

        foreach (var file in onDisk)
        {
            if (known.Any(k => k.RelativePath == file.RelativePath)) continue;

            db.SyncConflicts.Add(new SyncConflict
            {
                WorkspaceId = space.WorkspaceId,
                SyncSpaceId = space.Id,
                RelativePath = file.RelativePath,
                OriginalRelativePath = file.OriginalRelativePath,
                SizeBytes = file.SizeBytes,
                DetectedAt = file.DetectedAt,
                OriginatingDevice = file.OriginatingDevice
            });

            logger.LogInformation("Sync conflict in {Space}: {Path}", space.Name, file.RelativePath);
        }

        // Gone from disk means somebody dealt with it, here or on the device. The row stays, marked,
        // rather than vanishing — a conflict that happened is worth being able to look back at.
        foreach (var stale in known.Where(k =>
            k.Resolution == SyncConflictResolution.Unresolved
            && !onDisk.Any(f => f.RelativePath == k.RelativePath)))
        {
            stale.Resolution = SyncConflictResolution.Disappeared;
            stale.ResolvedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Record what a person decided about a conflict.
    ///
    /// <para>
    /// Harbora records the decision; it does not move or delete the files. Acting on the user's
    /// behalf here would mean deleting one of two versions of their work on the strength of a click,
    /// and the file operations belong on the device that holds them.
    /// </para>
    /// </summary>
    public async Task<SyncOutcome> ResolveConflictAsync(
        Guid conflictId, SyncConflictResolution resolution, CancellationToken ct)
    {
        if (resolution is SyncConflictResolution.Unresolved)
            return SyncOutcome.Fail("Choose what to do with the conflicting copy.");

        var conflict = await db.SyncConflicts.FirstOrDefaultAsync(c => c.Id == conflictId, ct);
        if (conflict is null) return SyncOutcome.Fail("That conflict no longer exists.");

        conflict.Resolution = resolution;
        conflict.ResolvedAt = DateTimeOffset.UtcNow;
        conflict.ResolvedByUserId = currentUser.UserId;

        var space = await db.SyncSpaces.FirstOrDefaultAsync(s => s.Id == conflict.SyncSpaceId, ct);
        if (space is not null && space.ConflictCount > 0) space.ConflictCount--;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("sync.conflict.resolve", "SyncConflict", conflictId.ToString(),
            userIdOverride: currentUser.UserId, workspaceId: space?.WorkspaceId, ct: ct);

        return new SyncOutcome(true, conflictId);
    }

    /// <summary>Confines a sync path to a configured root, the same way restore destinations are.</summary>
    private string? ConfineToAllowedRoot(string path)
    {
        foreach (var root in _options.AllowedRoots)
        {
            var check = PathGuard.ResolveWithin(root, path);
            if (check.Allowed) return check.ResolvedPath;
        }
        return null;
    }

    public Task<List<SyncSpace>> ListSpacesAsync(CancellationToken ct) =>
        db.SyncSpaces.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);

    public Task<List<SyncDevice>> ListDevicesAsync(CancellationToken ct) =>
        db.SyncDevices.AsNoTracking().OrderBy(d => d.Name).ToListAsync(ct);

    public Task<List<SyncConflict>> ListOpenConflictsAsync(CancellationToken ct) =>
        db.SyncConflicts.AsNoTracking()
            .Where(c => c.Resolution == SyncConflictResolution.Unresolved)
            .OrderByDescending(c => c.DetectedAt).ToListAsync(ct);
}
