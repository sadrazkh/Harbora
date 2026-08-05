using Harbora.Application.Abstractions;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Harbora.Modules.Sync.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// Sync spaces, devices and conflicts.
///
/// <para>
/// Deliberately its own controller with its own screens, sharing nothing with
/// <see cref="BackupCenterController"/>. Sync propagates deletions; presenting it beside snapshots
/// and restores would invite exactly the confusion this module exists to avoid.
/// </para>
/// </summary>
[Authorize]
[Route("sync")]
public sealed class SyncCenterController(
    SyncSpaceService spaces,
    ISyncEngine engine,
    ICurrentUser currentUser,
    IOptions<SyncFeatureOptions> features,
    IOptions<SyncModuleOptions> moduleOptions) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool Enabled => features.Value.Sync;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        ViewData["Title"] = "Sync";

        return View(new SyncCenterViewModel
        {
            Spaces = await spaces.ListSpacesAsync(ct),
            Devices = await spaces.ListDevicesAsync(ct),
            Conflicts = await spaces.ListOpenConflictsAsync(ct),
            AllowedRoots = moduleOptions.Value.AllowedRoots,
            EncryptedNodeAllowed = moduleOptions.Value.AllowEncryptedNode,
            // Shown so the operator can hand it to the other device: pairing needs both ends.
            LocalDeviceId = await engine.GetLocalDeviceIdAsync(ct)
        });
    }

    [HttpPost("spaces")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSpace(
        string name, string localPath, SyncMode mode,
        SyncVersioningMode versioningMode, int versioningParameter, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.CreateSpaceAsync(WorkspaceId, new SyncSpace
        {
            Name = name,
            LocalPath = localPath,
            Mode = mode,
            VersioningMode = versioningMode,
            VersioningParameter = versioningParameter
        }, ct);

        Report(result, $"Sync space '{name}' created.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("devices")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RegisterDevice(
        string name, string engineDeviceId, bool untrusted, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.RegisterDeviceAsync(WorkspaceId, name, engineDeviceId, untrusted, ct);

        Report(result, $"Device '{name}' registered. It must add this node too before anything syncs.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("spaces/{spaceId:guid}/devices")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Pair(
        Guid spaceId, Guid deviceId, SyncMode mode, string? encryptionPassword, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.PairAsync(spaceId, deviceId, mode, encryptionPassword, ct);

        Report(result, "Device added. Pairing completes once the other end accepts.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("spaces/{spaceId:guid}/devices/{deviceId:guid}/remove")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unpair(Guid spaceId, Guid deviceId, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.UnpairAsync(spaceId, deviceId, ct);

        // Said explicitly: removing a device does not reach out and delete its copy, and nobody
        // should be left assuming it did.
        Report(result, "Device removed from the space. It keeps the files it already has.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("spaces/{spaceId:guid}/pause")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SetPaused(Guid spaceId, bool paused, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.SetPausedAsync(spaceId, paused, ct);

        Report(result, paused ? "Sync paused." : "Sync resumed.");
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("spaces/{spaceId:guid}/refresh")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(Guid spaceId, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        await spaces.RefreshAsync(spaceId, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("conflicts/{conflictId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> ResolveConflict(
        Guid conflictId, SyncConflictResolution resolution, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await spaces.ResolveConflictAsync(conflictId, resolution, ct);

        Report(result, "Noted. Harbora recorded your decision — the files themselves are unchanged.");
        return RedirectToAction(nameof(Index));
    }

    private void Report(SyncOutcome result, string success)
    {
        if (result.Succeeded) TempData["Message"] = success;
        else TempData["Error"] = result.Error
            ?? string.Join(" ", result.Errors?.Select(e => e.Message) ?? []);
    }
}

/// <summary>Everything the Sync page shows.</summary>
public sealed class SyncCenterViewModel
{
    public IReadOnlyList<SyncSpace> Spaces { get; init; } = [];
    public IReadOnlyList<SyncDevice> Devices { get; init; } = [];
    public IReadOnlyList<SyncConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> AllowedRoots { get; init; } = [];
    public bool EncryptedNodeAllowed { get; init; }

    /// <summary>Null when Syncthing is unreachable — the page says so rather than showing a blank.</summary>
    public string? LocalDeviceId { get; init; }

    public int OfflineDevices => Devices.Count(d => d.Status == SyncDeviceStatus.Disconnected);
    public int RelayedDevices => Devices.Count(d => d.ConnectionKind == SyncConnectionKind.Relay);
    public long PendingFiles => Spaces.Sum(s => s.PendingFiles);
}
