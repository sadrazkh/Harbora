using Harbora.Domain.Authorization;
﻿using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Jobs;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class DeploymentsController(
    HarboraDbContext db,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    Harbora.Infrastructure.Assistant.AssistantService assistant,
    IDeploymentEngine deployEngine,
    IAuditLogger audit,
    ICurrentUser currentUser,
    IOptions<JobQueueOptions> jobQueue,
    ISystemClock clock) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Deployments";
        var deployments = await db.Deployments.Include(d => d.App)
            .Where(d => d.App!.WorkspaceId == WorkspaceId)
            .OrderByDescending(d => d.CreatedAt).Take(100).ToListAsync(ct);
        return View(deployments);
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var deployment = await db.Deployments
            .Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId, ct);
        if (deployment is null) return NotFound();

        // The build log and the configuration diff belong to the app, so they follow its visibility.
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        // What changed since the version before this one — the question people actually ask after a
        // bad release, and one the history could not answer until deployments recorded their config.
        var previous = await db.Deployments
            .Where(d => d.AppId == deployment.AppId && d.Number < deployment.Number && d.ConfigJson != null)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);

        var before = Harbora.Infrastructure.Deployments.DeploymentConfig.FromJson(previous?.ConfigJson);
        var after = Harbora.Infrastructure.Deployments.DeploymentConfig.FromJson(deployment.ConfigJson);

        ViewBag.ComparedWith = previous?.Number;
        ViewBag.ConfigChanges = Harbora.Infrastructure.Deployments.ConfigDiff.Between(before, after);
        ViewBag.ConfigIdentical = Harbora.Infrastructure.Deployments.ConfigDiff.AreIdentical(before, after);
        ViewBag.Config = after;

        // Where this exact release could go next: other services in the same project, in another
        // environment. Offered only when it is genuinely possible — see PromotionPlan.
        ViewBag.PromotionTargets = await PromotionTargetsAsync(deployment, ct);

        // Offered only where it could help, and only when an administrator has actually configured
        // it. The check lives in one place so the button and the endpoint cannot disagree.
        ViewBag.AssistantAvailable =
            deployment.Status == Harbora.Domain.Common.DeploymentStatus.Failed
            && Harbora.Infrastructure.Assistant.AssistantAvailability.IsAvailable(
                await assistant.GetConfigAsync(ct));

        // Why it has not started. Asked only of a Queued deployment: anything else is either running
        // or over, and a queue position for it would be describing something that is not happening.
        if (deployment.Status == DeploymentStatus.Queued && await QueuePlaceAsync(deployment.Id, ct) is { } place)
        {
            ViewBag.QueuePlace = place;
            ViewBag.QueueExplanation = QueuePosition.Describe(
                place, System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa");
        }

        // The button is drawn from the same answer the endpoint gives, so the two cannot disagree —
        // offering a control that always refuses teaches people to ignore it.
        ViewBag.CanCancel = DeploymentStateMachine.IsInFlight(deployment.Status)
                            && await access.CanTouchAppAsync(deployment.AppId, Capabilities.AppsDeploy, ct);

        return View(deployment);
    }

    /// <summary>
    /// Where this deployment's job stands in the platform's queue, or null when it has none — a
    /// reconciler can settle the job while the deployment row is still Queued, and inventing a
    /// position for a deployment that is in no queue would be the same lie in a friendlier voice.
    ///
    /// <para>
    /// Read platform-wide on purpose. The queue is one queue; counting only this workspace's rows
    /// would produce a smaller, more comfortable, wrong number. Nothing but each row's <i>kind</i>
    /// leaves this method, so another tenant's work is counted without being named.
    /// </para>
    /// </summary>
    private async Task<QueuePlace?> QueuePlaceAsync(Guid deploymentId, CancellationToken ct)
    {
        // The newest Pending-or-Running job for this deployment — the same predicate
        // DatabaseJobQueue.RequestCancellationAsync uses to pick the row it would cancel, so the
        // position and the cancel are answers about the same row rather than merely the newest one
        // regardless of status (a settled row newer than the live one would otherwise hide it).
        var jobId = await db.Jobs.AsNoTracking()
            .Where(j => j.Kind == JobKind.Deployment && j.TargetId == deploymentId &&
                        (j.Status == JobStatus.Pending || j.Status == JobStatus.Running))
            .OrderByDescending(j => j.CreatedAt)
            .Select(j => (Guid?)j.Id)
            .FirstOrDefaultAsync(ct);

        if (jobId is not { } id) return null;

        var rows = await db.Jobs.AsNoTracking()
            .Where(j => j.Status == JobStatus.Pending || j.Status == JobStatus.Running)
            .Select(j => new
            {
                j.Id, j.Kind, j.TargetId, j.ExclusiveWith, j.Status, j.CreatedAt, j.NextAttemptAt,
                j.CancelRequested
            })
            .ToListAsync(ct);

        // ExcludesOn is not a mapped property — the same reason JobClaimQuery spells the coalesce
        // out — so the fallback is applied here, once, on the way into the rule.
        var place = QueuePosition.For(
            rows.Select(r => new QueuedJob(
                r.Id, r.Kind, r.ExclusiveWith ?? r.TargetId, r.Status, r.CreatedAt, r.NextAttemptAt,
                r.CancelRequested)),
            id, clock.UtcNow, jobQueue.Value.EffectiveMaxConcurrency);

        return place.Wait == QueueWait.NotQueued ? null : place;
    }

    /// <summary>
    /// Stops a deployment that is queued or in flight.
    ///
    /// <para>
    /// The engine has been able to do this since it was written; nothing could ask it to. The
    /// interesting case is the one this method exists to get right: a deployment that reached a
    /// terminal state between the page being drawn and the button being pressed. The state machine
    /// would throw on that transition, so the status is read first — and read <i>again</i>
    /// afterwards, because the deployment can finish during the call as easily as before it. What is
    /// reported is what the row actually says, never what was asked for.
    /// </para>
    /// </summary>
    [HttpPost("/deployments/{id:guid}/cancel")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var row = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId, d.Status, d.Number })
            .FirstOrDefaultAsync(ct);

        if (row is null) return NotFound();
        if (!await access.CanTouchAppAsync(row.AppId, Capabilities.AppsDeploy, ct)) return Forbid();

        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

        if (DeploymentStateMachine.IsTerminal(row.Status))
        {
            TempData["Error"] = Ended(row.Number, row.Status, isFa);
            return RedirectToAction(nameof(Details), new { id });
        }

        await deployEngine.CancelAsync(id, ct);

        var settled = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id).Select(d => d.Status).FirstOrDefaultAsync(ct);

        if (settled == DeploymentStatus.Cancelled)
        {
            await audit.LogAsync("deployment.cancelled", "deployment", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
            TempData["Message"] = isFa
                ? $"استقرار #{row.Number} لغو شد."
                : $"Deployment #{row.Number} was cancelled.";
        }
        else TempData["Error"] = Ended(row.Number, settled, isFa);

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>What to say about a deployment that was already over. Names the status it ended in,
    /// because "could not cancel" without it reads as a failure of the panel.</summary>
    private static string Ended(int number, DeploymentStatus status, bool isFa) => isFa
        ? $"استقرار #{number} پیش از لغو شدن به پایان رسیده بود ({status})."
        : $"Deployment #{number} had already ended ({status}), so there was nothing to cancel.";

    /// <summary>
    /// Retries a failed deployment (P6, 2026-08-17 app-environment-management design). Gated on
    /// <see cref="DeploymentStatus.Failed"/> — the same "offering a control that always refuses
    /// teaches people to ignore it" rule <see cref="Promote"/>'s target list already follows — and,
    /// like every other action here, mints a fresh <see cref="Deployment"/> row rather than mutating
    /// the failed one: <c>Deployment</c> is immutable history, and a retry that fails again must open
    /// its own incident rather than reopen one someone already closed
    /// (<c>DeploymentPipeline.cs:616-619</c>).
    ///
    /// <para>
    /// <b>§7 Q4 — what this honestly re-uses.</b> The failed attempt's own recorded
    /// <see cref="Deployment.GitRef"/>, and nothing else: that is the one field a redeploy could get
    /// wrong that a retry should not — the app's current default branch may have moved since the
    /// attempt that failed, especially for a webhook-triggered deploy of a branch that was never the
    /// app's default in the first place. Every other candidate is deliberately left alone —
    /// <see cref="Deployment.SourceArchivePath"/> is never carried forward (an uploaded archive is
    /// deleted the moment <c>DeploymentPipeline.MaterialiseSourceAsync</c> reads it, so a second
    /// reference to it points at nothing by the time a retry could run), and
    /// <see cref="Deployment.ImageTag"/> is never carried forward either — skipping the build for an
    /// exact image is <see cref="Promote"/>'s job, not a retry's. Environment variables, volumes and
    /// instance size were never part of the request to begin with: <c>DeploymentRequest</c> carries
    /// none of them, and the pipeline always reads the app as it stands right now — a full replay of
    /// <see cref="Deployment.ConfigJson"/> is impossible by construction anyway, since its secrets are
    /// stored as HMAC fingerprints rather than recoverable values.
    /// </para>
    /// </summary>
    [HttpPost("/deployments/{id:guid}/retry")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Retry(Guid id, CancellationToken ct)
    {
        var failed = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId, d.Status, d.Number, d.GitRef })
            .FirstOrDefaultAsync(ct);

        if (failed is null) return NotFound();
        if (!await access.CanTouchAppAsync(failed.AppId, Capabilities.AppsDeploy, ct)) return Forbid();

        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

        if (failed.Status != DeploymentStatus.Failed)
        {
            TempData["Error"] = isFa
                ? $"فقط یک استقرار ناموفق را می‌توان دوباره امتحان کرد — #{failed.Number} اکنون {failed.Status} است."
                : $"Only a failed deployment can be retried — #{failed.Number} is now {failed.Status}.";
            return RedirectToAction(nameof(Details), new { id });
        }

        Guid newId;
        try
        {
            newId = await deployEngine.QueueDeploymentAsync(new DeploymentRequest(
                failed.AppId, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty,
                GitRef: failed.GitRef), ct);
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (InvalidOperationException ex)
        {
            // e.g. another deployment of this app started between the page loading and this post.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("deployment.retried", "deployment", newId.ToString(), ClientIp,
            workspaceId: WorkspaceId,
            metadataJson: $"{{\"retryOf\":\"{id}\"}}", ct: ct);

        TempData["Message"] = isFa
            ? $"در حال تلاش دوباره برای استقرار #{failed.Number} — همان شاخهٔ گیت دوباره ساخته می‌شود؛ " +
              "متغیرها، حجم‌ها و اندازه از تنظیمات فعلی برنامه گرفته می‌شوند، نه از این نسخه."
            : $"Retrying deployment #{failed.Number} — rebuilding the same git ref. Variables, volumes " +
              "and instance size come from the app's current configuration, not from this snapshot.";

        return RedirectToAction(nameof(Details), new { id = newId });
    }

    /// <summary>
    /// Releases this exact image into another service in the same project, without rebuilding.
    ///
    /// Building twice from one commit does not reliably produce the same image, so "we tested this
    /// in staging" only means something if the bytes that reach production are the bytes that
    /// passed. Configuration is deliberately not carried across: the target keeps its own variables,
    /// database and domains.
    /// </summary>
    [HttpPost("/deployments/{id:guid}/promote")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Promote(Guid id, Guid targetAppId, CancellationToken ct)
    {
        var source = await LoadPromotionSourceAsync(id, ct);
        if (source is null) return NotFound();

        // Asked about the target as well: promoting into a service someone cannot deploy would be
        // a way around the permission on the target itself.
        if (!await access.CanTouchAppAsync(targetAppId, Capabilities.AppsDeploy, ct)) return NotFound();

        var target = await db.Apps.AsNoTracking()
            .Where(a => a.Id == targetAppId && a.WorkspaceId == WorkspaceId)
            .Select(a => new { a.Id, a.Name, a.ServerId, ProjectId = (Guid?)a.Environment!.ProjectId })
            .FirstOrDefaultAsync(ct);
        if (target is null) return NotFound();

        var refusal = Harbora.Infrastructure.Deployments.PromotionPlan.Refuse(
            source.Value.Plan,
            new Harbora.Infrastructure.Deployments.PromotionTarget(target.Id, target.ProjectId, target.ServerId));

        if (refusal is not null)
        {
            TempData["Error"] = refusal;
            return RedirectToAction(nameof(Details), new { id });
        }

        Guid deploymentId;
        try
        {
            deploymentId = await deployEngine.QueueDeploymentAsync(new DeploymentRequest(
                target.Id, Harbora.Domain.Common.DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty,
                // The artifact, released as-is. Nothing is built and no source is fetched.
                ImageOverride: source.Value.Plan.ImageTag), ct);
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        // Matching this method's own existing QuotaRefusedException catch just above: English-only
        // here, same as that one already was — not a new gap this introduces.
        catch (CapacityRefusedException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (LowDiskRefusedException ex)
        {
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.promote", "app", target.Id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = $"Promoting {source.Value.Plan.ImageTag} to {target.Name}.";
        return RedirectToAction(nameof(Details), new { id = deploymentId });
    }

    private async Task<(Harbora.Infrastructure.Deployments.PromotionSource Plan, Guid AppId)?>
        LoadPromotionSourceAsync(Guid deploymentId, CancellationToken ct)
    {
        var row = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == deploymentId && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new
            {
                d.Status, d.ImageTag, d.AppId,
                d.App!.ServerId,
                ProjectId = (Guid?)d.App!.Environment!.ProjectId
            })
            .FirstOrDefaultAsync(ct);

        if (row is null) return null;
        if (!await access.CanSeeAppAsync(row.AppId, ct)) return null;

        return (new Harbora.Infrastructure.Deployments.PromotionSource(
            row.Status, row.ImageTag, row.AppId, row.ProjectId, row.ServerId), row.AppId);
    }

    /// <summary>
    /// The services this release could be promoted to. Only those the promotion rule would actually
    /// accept — offering a button that always refuses teaches people to ignore the feature.
    /// </summary>
    private async Task<IReadOnlyList<(Guid Id, string Name, string Environment)>> PromotionTargetsAsync(
        Harbora.Domain.Deployments.Deployment deployment, CancellationToken ct)
    {
        if (await LoadPromotionSourceAsync(deployment.Id, ct) is not { } source) return [];

        var candidates = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == WorkspaceId && a.Id != deployment.AppId)
            .Select(a => new
            {
                a.Id, a.Name, a.ServerId,
                ProjectId = (Guid?)a.Environment!.ProjectId,
                EnvironmentName = a.Environment!.Name
            })
            .ToListAsync(ct);

        var allowed = new List<(Guid, string, string)>();
        foreach (var candidate in candidates)
        {
            var refusal = Harbora.Infrastructure.Deployments.PromotionPlan.Refuse(
                source.Plan,
                new Harbora.Infrastructure.Deployments.PromotionTarget(candidate.Id, candidate.ProjectId, candidate.ServerId));

            if (refusal is null && await access.CanTouchAppAsync(candidate.Id, Capabilities.AppsDeploy, ct))
                allowed.Add((candidate.Id, candidate.Name, candidate.EnvironmentName));
        }

        return allowed;
    }

    /// <summary>
    /// Shows exactly what would be sent to the AI provider, and sends nothing.
    ///
    /// The whole reason the assistant is two steps: the text that leaves this server has to be text
    /// somebody has read. Building the preview and building the request are the same function, so a
    /// preview cannot drift from what is actually sent.
    /// </summary>
    [HttpGet("/deployments/{id:guid}/assistant/preview")]
    public async Task<IActionResult> AssistantPreview(Guid id, CancellationToken ct)
    {
        if (await MayAskAboutAsync(id, ct) is { } failure) return failure;

        var ask = await assistant.PrepareAsync(id, ct);
        if (ask is null) return NotFound();

        return Json(new { text = ask.UserPrompt, removed = ask.Removed, truncated = ask.Truncated });
    }

    /// <summary>Sends the question the person has just been shown.</summary>
    [HttpPost("/deployments/{id:guid}/assistant/ask")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AssistantAsk(Guid id, CancellationToken ct)
    {
        if (await MayAskAboutAsync(id, ct) is { } failure) return failure;

        var ask = await assistant.PrepareAsync(id, ct);
        if (ask is null) return NotFound();

        var answer = await assistant.AskAsync(ask, ct);

        // Audited because it is the moment data left this server, and how much of it was removed
        // first is exactly what somebody would want to reconstruct later.
        await audit.LogAsync("assistant.asked", "deployment", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);

        return Json(new { ok = answer.Ok, text = answer.Text });
    }

    /// <summary>
    /// Visibility plus configuration, in one place. Reading a deployment is enough to ask about it —
    /// the same people who can read the log, which is all the assistant is shown.
    /// </summary>
    private async Task<IActionResult?> MayAskAboutAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == deploymentId && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId }).FirstOrDefaultAsync(ct);
        if (deployment is null) return NotFound();
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        if (Harbora.Infrastructure.Assistant.AssistantAvailability.Check(
                await assistant.GetConfigAsync(ct)) is { } unavailable)
            return BadRequest(new
            {
                message = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa"
                    ? unavailable.ReasonFa
                    : unavailable.Reason
            });

        return null;
    }

    /// <summary>Backfills already-persisted log lines before the SignalR stream takes over.</summary>
    [HttpGet("/deployments/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, long after = -1, CancellationToken ct = default)
    {
        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId, d.Status }).FirstOrDefaultAsync(ct);
        if (deployment is null) return NotFound();
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == id && l.Sequence > after)
            .OrderBy(l => l.Sequence)
            .Select(l => new { seq = l.Sequence, stream = l.Stream.ToString(), l.Message, ts = l.Timestamp })
            .ToListAsync(ct);

        // The status travels with the lines. Without it the polling fallback — the path taken when
        // the socket cannot open — had no way to learn the deployment had ended: it polled every
        // 1.5 seconds for ever, and the progress bar it was supposed to move never moved.
        // The row is already loaded here for the access check, so this costs nothing.
        return Json(new { status = deployment.Status.ToString(), lines });
    }
}
