using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
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
    Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Deployments.HarboraRuntimeOptions> runtime,
    ICurrentUser currentUser) : ControllerBase
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    /// <summary>
    /// Push source from a developer's machine and deploy it — the CapRover-style flow: create the app
    /// in the panel once, then `harbora deploy` from any folder, with no Git remote in between.
    ///
    /// The body is a gzipped tar of the working directory, streamed straight to disk so a large
    /// project never has to fit in memory. The archive belongs to this deployment alone.
    /// </summary>
    [HttpPost("apps/{slug}/deploy/archive")]
    [Authorize(Policy = Capabilities.AppsDeploy, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    [RequestSizeLimit(MaxUploadBytes)]
    public async Task<IActionResult> DeployArchive(string slug, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == slug, ct);
        if (app is null) return NotFound(new { error = "App not found." });

        if (Request.ContentLength is 0 or null && !Request.Headers.ContainsKey("Transfer-Encoding"))
            return BadRequest(new { error = "Send the project as a gzipped tar in the request body." });

        var uploadDir = Path.Combine(runtime.Value.WorkDir, "_uploads");
        Directory.CreateDirectory(uploadDir);
        var archivePath = Path.Combine(uploadDir, $"{app.Slug}-{Guid.CreateVersion7():N}.tar.gz");

        try
        {
            await using (var file = System.IO.File.Create(archivePath))
                await Request.Body.CopyToAsync(file, ct);
        }
        catch (Exception)
        {
            // Never leave a half-written archive behind for the pipeline to trip over.
            TryDelete(archivePath);
            throw;
        }

        try
        {
            var id = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Cli, currentUser.UserId ?? Guid.Empty,
                    SourceArchivePath: archivePath), ct);
            return Ok(new { deploymentId = id });
        }
        catch (InvalidOperationException ex)
        {
            // Coalesced onto another in-flight deployment, or a rollback is running: the upload is
            // orphaned, so remove it rather than leaking a file per rejected push.
            TryDelete(archivePath);
            return Conflict(new { error = ex.Message });
        }
    }

    /// <summary>Ceiling for a pushed source archive. Source trees far above this are a mistake.</summary>
    private const long MaxUploadBytes = 512L * 1024 * 1024;

    private static void TryDelete(string path)
    {
        try { if (System.IO.File.Exists(path)) System.IO.File.Delete(path); } catch { /* best effort */ }
    }

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
    [Authorize(Policy = Capabilities.AppsDeploy, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Deploy(string slug, [FromBody] DeployBody? body, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == slug, ct);
        if (app is null) return NotFound(new { error = "App not found." });

        try
        {
            var id = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Cli, currentUser.UserId ?? Guid.Empty,
                    body?.GitRef ?? app.GitRef,
                    // An explicit image means "release this, build nothing".
                    ImageOverride: string.IsNullOrWhiteSpace(body?.Image) ? null : body!.Image), ct);
            return Ok(new { deploymentId = id });
        }
        catch (InvalidOperationException ex)
        {
            // e.g. a rollback is mid-flight and must not be coalesced onto.
            return Conflict(new { error = ex.Message });
        }
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

    /// <summary>Body of POST /apps/{slug}/deploy. Both fields optional; see docs/cli-deploy.md.</summary>
    public sealed record DeployBody(string? GitRef, string? Image = null);
}
