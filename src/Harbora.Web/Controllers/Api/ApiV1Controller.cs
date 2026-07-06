using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers.Api;

/// <summary>
/// Stable JSON API consumed by the CLI and CI. Authenticated with bearer API tokens
/// (<see cref="TokenAuthenticationHandler"/>); mirrors what the UI can do for deployments.
/// </summary>
[ApiController]
[Route("api/v1")]
[Authorize(AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
public sealed class ApiV1Controller(
    HarboraDbContext db,
    IDeploymentEngine deployEngine,
    ICurrentUser currentUser) : ControllerBase
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    [HttpGet("whoami")]
    public IActionResult WhoAmI() =>
        Ok(new { email = currentUser.Email, workspaceId = WorkspaceId });

    [HttpGet("apps")]
    public async Task<IActionResult> Apps(CancellationToken ct)
    {
        var apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new { a.Id, a.Name, a.Slug, status = a.Status.ToString(), source = a.SourceType.ToString() })
            .ToListAsync(ct);
        return Ok(apps);
    }

    [HttpPost("apps/{slug}/deploy")]
    public async Task<IActionResult> Deploy(string slug, [FromBody] DeployBody? body, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == slug, ct);
        if (app is null) return NotFound(new { error = "App not found." });

        var id = await deployEngine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Cli, currentUser.UserId ?? Guid.Empty, body?.GitRef ?? app.GitRef), ct);
        return Ok(new { deploymentId = id });
    }

    [HttpGet("deployments/{id:guid}")]
    public async Task<IActionResult> Deployment(Guid id, CancellationToken ct)
    {
        var d = await db.Deployments.Where(x => x.Id == id && x.App!.WorkspaceId == WorkspaceId)
            .Select(x => new { x.Id, x.Number, status = x.Status.ToString(), x.CommitSha, x.ErrorMessage })
            .FirstOrDefaultAsync(ct);
        return d is null ? NotFound() : Ok(d);
    }

    [HttpGet("deployments/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, long after = -1, CancellationToken ct = default)
    {
        var owns = await db.Deployments.AnyAsync(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId, ct);
        if (!owns) return NotFound();
        var lines = await db.DeploymentLogs
            .Where(l => l.DeploymentId == id && l.Sequence > after)
            .OrderBy(l => l.Sequence)
            .Select(l => new { seq = l.Sequence, stream = l.Stream.ToString(), l.Message })
            .ToListAsync(ct);
        return Ok(lines);
    }

    public sealed record DeployBody(string? GitRef);
}
