using Harbora.Domain.Authorization;
﻿using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class DeploymentsController(
    HarboraDbContext db,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    Harbora.Infrastructure.Assistant.AssistantService assistant,
    IDeploymentEngine deployEngine,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
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

        return View(deployment);
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

        var deploymentId = await deployEngine.QueueDeploymentAsync(new DeploymentRequest(
            target.Id, Harbora.Domain.Common.DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty,
            // The artifact, released as-is. Nothing is built and no source is fetched.
            ImageOverride: source.Value.Plan.ImageTag), ct);

        await audit.LogAsync("app.promote", "app", target.Id.ToString(), ClientIp, ct: ct);
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
        await audit.LogAsync("assistant.asked", "deployment", id.ToString(), ClientIp, ct: ct);

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
            return BadRequest(new { message = unavailable.Reason });

        return null;
    }

    /// <summary>Backfills already-persisted log lines before the SignalR stream takes over.</summary>
    [HttpGet("/deployments/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, long after = -1, CancellationToken ct = default)
    {
        var deployment = await db.Deployments.AsNoTracking()
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.AppId }).FirstOrDefaultAsync(ct);
        if (deployment is null) return NotFound();
        if (!await access.CanSeeAppAsync(deployment.AppId, ct)) return NotFound();

        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == id && l.Sequence > after)
            .OrderBy(l => l.Sequence)
            .Select(l => new { seq = l.Sequence, stream = l.Stream.ToString(), l.Message, ts = l.Timestamp })
            .ToListAsync(ct);

        return Json(lines);
    }
}
