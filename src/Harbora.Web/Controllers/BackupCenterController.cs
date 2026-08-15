using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// The backup module's screens: repositories, policies, snapshots and restores.
///
/// <para>
/// Separate from <see cref="BackupsController"/>, which owns Harbora's original backup feature.
/// Merging them would put two different data models and two different restore paths behind one set
/// of buttons, which is the one place in this product ambiguity is expensive.
/// </para>
/// <para>
/// Every action returns 404 while <c>Features:Backup</c> is off, so the routes do not exist rather
/// than existing and refusing.
/// </para>
/// </summary>
[Authorize]
[Route("backup-center")]
public sealed class BackupCenterController(
    HarboraDbContext db,
    BackupRepositoryService repositories,
    BackupSnapshotService snapshots,
    BackupPolicyService policies,
    RestoreService restores,
    ICurrentUser currentUser,
    IOptions<BackupFeatureOptions> features,
    IOptions<BackupModuleOptions> moduleOptions,
    IOptions<HarboraRuntimeOptions> runtimeOptions) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool Enabled => features.Value.Backup;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        ViewData["Title"] = "Backup Center";

        var vm = new BackupCenterViewModel
        {
            Repositories = await repositories.ListAsync(ct),
            Policies = await policies.ListAsync(ct),
            Snapshots = await snapshots.ListAsync(null, 50, ct),
            Restores = await restores.ListAsync(20, ct),
            RestoreRoot = moduleOptions.Value.RestoreRoot,
            AllowedSourceRoots = moduleOptions.Value.AllowedSourceRoots
        };

        return View(vm);
    }

    [HttpPost("repositories")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRepository(
        string name, BackupRepositoryType type, BackupEngineKind engine, string password,
        string? localPath, string? endpoint, string? bucket, string? region,
        string? accessKeyId, string? secretAccessKey, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await repositories.CreateAsync(WorkspaceId, new NewRepositoryRequest(
            name, type, engine, password, localPath, endpoint, bucket, region,
            localPath, accessKeyId, secretAccessKey), ct);

        if (result.Succeeded) TempData["Message"] = $"Repository '{name}' is ready.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("repositories/{id:guid}/check")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CheckRepository(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var health = await repositories.CheckHealthAsync(id, ct);
        if (health.Reachable && health.Intact) TempData["Message"] = "The repository is reachable.";
        else TempData["Error"] = health.Error ?? "The repository could not be reached.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("repositories/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteRepository(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await repositories.DeleteAsync(id, ct);
        if (result.Succeeded) TempData["Message"] = "Repository removed.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("policies")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePolicy(
        string name, Guid repositoryId, BackupTargetType targetType, string targetRef,
        string schedule, string timezone, int keepLatest, int keepDaily, int keepMonthly,
        CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var policy = new BackupPolicy
        {
            WorkspaceId = WorkspaceId,
            Name = name,
            RepositoryId = repositoryId,
            TargetType = targetType,
            TargetRef = targetRef,
            Schedule = schedule,
            Timezone = string.IsNullOrWhiteSpace(timezone) ? "UTC" : timezone,
            Retention = new RetentionPolicy
            {
                KeepLatest = keepLatest,
                KeepDaily = keepDaily,
                KeepMonthly = keepMonthly,
                // Not on the simple form. Left at zero rather than at a number the user never chose:
                // the advanced form is where the other tiers are set.
                KeepHourly = 0,
                KeepWeekly = 0,
                KeepYearly = 0
            }
        };

        var result = await policies.SaveAsync(policy, ct);
        if (result.Succeeded) TempData["Message"] = $"Policy '{name}' saved.";
        else TempData["Error"] = string.Join(" ", result.Errors?.Select(e => e.Message) ?? []);

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("policies/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePolicy(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        TempData[await policies.DeleteAsync(id, ct) ? "Message" : "Error"] =
            "Policy removed.";

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("snapshots")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RunBackup(
        Guid repositoryId, BackupTargetType targetType, string targetRef, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await snapshots.QueueAsync(
            WorkspaceId, repositoryId, targetType, targetRef, null, BackupTrigger.Manual, ct);

        if (result.Succeeded) TempData["Message"] = "Backup queued. It runs in the background.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }

    [HttpGet("snapshots/{id:guid}")]
    public async Task<IActionResult> Snapshot(Guid id, string path = "", CancellationToken ct = default)
    {
        if (!Enabled) return NotFound();

        var (snapshot, repositoryName) = await snapshots.GetForDisplayAsync(id, ct);
        if (snapshot is null || repositoryName is null) return NotFound();

        // A snapshot from another workspace must be indistinguishable from one that does not exist.
        if (snapshot.WorkspaceId != WorkspaceId) return NotFound();

        ViewData["Title"] = "Snapshot";

        return View(new SnapshotBrowserViewModel
        {
            Snapshot = snapshot,
            RepositoryName = repositoryName,
            CurrentPath = path,
            Entries = snapshot.IsRestorable ? await snapshots.BrowseAsync(id, path, ct) : [],
            RestoreRoot = moduleOptions.Value.RestoreRoot,
            RestoreImageIsInstant = await RestoreImageIsInstantAsync(snapshot, ct)
        });
    }

    /// <summary>
    /// Sub-project E, Task 3: whether the image this application backup would restore onto is still
    /// pullable — F's exact question (<c>DeploymentPlanning.RollbackEligibleDeploymentIds</c>,
    /// <c>2026-08-15-rollback-depth-design</c>), asked about a backup instead of a Deployments-tab
    /// row, and answered from the same rule so the two can never disagree.
    ///
    /// <para>
    /// Null for anything that is not <see cref="BackupTargetType.Application"/> — the question has no
    /// meaning for a directory, database or volume backup, none of which name an image at all.
    /// </para>
    /// <para>
    /// The deployment a restore would need is the one <c>ApplicationTargetStager</c> read
    /// <c>App.ActiveDeploymentId</c> from at backup time, which this method was not given (the file
    /// carrying it is inside the encrypted archive, not a queryable column — reading it back would
    /// mean restoring the archive just to look, which is not a GET request's business). It is
    /// approximated instead as the newest succeeded deployment at or before the snapshot's
    /// <c>StartedAt</c> — exactly when the stager ran — checked against
    /// <see cref="DeploymentPlanning.RollbackEligibleDeploymentIds"/> computed from the app's CURRENT
    /// history, so the answer moves with the pruner rather than needing a second column to agree
    /// with it. False, not null, when the app no longer exists or never had a succeeded deployment
    /// before the backup was taken — either way there is no image to call instant.
    /// </para>
    /// </summary>
    private async Task<bool?> RestoreImageIsInstantAsync(BackupSnapshot snapshot, CancellationToken ct)
    {
        if (snapshot.TargetType != BackupTargetType.Application) return null;
        if (!Guid.TryParse(snapshot.TargetRef, out var appId)) return null;

        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return false;

        var deployments = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == appId).ToListAsync(ct);

        var capturedAt = snapshot.StartedAt ?? snapshot.CreatedAt;
        var captured = deployments
            .Where(d => d.Status == DeploymentStatus.Succeeded && d.CreatedAt <= capturedAt)
            .OrderByDescending(d => d.Number)
            .FirstOrDefault();

        if (captured is null || string.IsNullOrWhiteSpace(captured.ImageTag)) return false;

        var eligible = DeploymentPlanning.RollbackEligibleDeploymentIds(
            deployments, app.ActiveDeploymentId, runtimeOptions.Value.ImageRetentionCount);

        return eligible.Contains(captured.Id);
    }

    /// <summary>
    /// "Verify now" — the operator-triggered half of the check.
    ///
    /// <para>
    /// The automatic one runs at the end of every backup; this exists for the snapshot that was
    /// taken before that did, for the one whose check failed and may have been a passing storage
    /// fault, and for the operator who simply wants to know today rather than tomorrow.
    /// </para>
    /// </summary>
    [HttpPost("snapshots/{id:guid}/verify")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifySnapshot(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await snapshots.QueueVerificationAsync(id, ct);

        if (result.Succeeded)
            TempData["Message"] = "Verification queued. The Verified column updates when it finishes.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("snapshots/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSnapshot(Guid id, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var result = await snapshots.DeleteAsync(id, ct);
        if (result.Succeeded) TempData["Message"] = "Snapshot deleted.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("restore")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restore(
        Guid snapshotId, string destination, RestoreConflictStrategy conflictStrategy,
        string? entries, string? confirmationText, CancellationToken ct)
    {
        if (!Enabled) return NotFound();

        var selected = entries?
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var result = await restores.QueueAsync(WorkspaceId, new RestoreRequest(
            snapshotId,
            RestoreType.Folder,
            destination,
            conflictStrategy,
            selected,
            confirmationText), ct);

        if (result.Succeeded) TempData["Message"] = "Restore queued. It runs in the background.";
        else TempData["Error"] = result.Error;

        return RedirectToAction(nameof(Index));
    }
}
