using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;
using Microsoft.AspNetCore.RateLimiting;
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
    IPasswordHasher passwordHasher,
    ITokenService tokens,
    IAuditLogger audit,
    ICurrentUser currentUser,
    // 4.1 (2026-09-04 local-dev-parity plan): what `Env` below decrypts a secret entry with, and the
    // endpoint an attached bucket's S3_ENDPOINT resolves to — the same two dependencies
    // DeploymentPipeline.BuildEnv and AppsController.Details already carry for the same reason.
    ISecretProtector protector,
    Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Storage.ObjectStorageOptions> storageOptions) : ControllerBase
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

    /// <summary>
    /// Exchanges an email and password for a CLI token.
    ///
    /// Without this, `harbora login` could only be completed by opening the panel in a browser,
    /// creating a token by hand and pasting it into a terminal — the one step of the CapRover-style
    /// flow that could not be done from the command line.
    ///
    /// Held to the same rules as the web login: the same per-IP limiter, a password check that runs
    /// even for an unknown address so timing cannot confirm who has an account, and one audit entry
    /// either way. The reply is a real token, so it is exactly as sensitive as the password.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("auth/token")]
    [EnableRateLimiting("auth")]
    public async Task<IActionResult> IssueToken([FromBody] TokenRequest? body, CancellationToken ct)
    {
        var email = (body?.Email ?? "").Trim().ToLowerInvariant();
        var password = body?.Password ?? "";

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Email == email && u.IsActive, ct);
        var ok = user is not null && user.EmailVerifiedAt is not null
            && passwordHasher.Verify(password, user.PasswordHash);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        if (!ok || user is null)
        {
            await audit.LogAsync("user.login_failed", "user", user?.Id.ToString(), ip,
                actorEmailOverride: email, userIdOverride: user?.Id, workspaceId: null);
            // Deliberately the same wording as the panel: which half was wrong is not the caller's
            // business, and saying so turns this into an account-enumeration endpoint.
            return Unauthorized(new { error = "Invalid email or password." });
        }

        var label = string.IsNullOrWhiteSpace(body!.Name) ? "CLI login" : body.Name!.Trim();
        var issued = tokens.Issue(user.Id, label, TokenType.Cli, null);
        db.ApiTokens.Add(new Harbora.Domain.Identity.ApiToken
        {
            UserId = user.Id, Name = label, Prefix = issued.Prefix,
            TokenHash = issued.Hash, Type = TokenType.Cli
        });
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("token.issued", "token", issued.Prefix, ip,
            actorEmailOverride: user.Email, userIdOverride: user.Id, workspaceId: null);

        return Ok(new { token = issued.PlaintextToken, email = user.Email, name = label });
    }

    /// <summary>Body of POST /auth/token.</summary>
    public sealed record TokenRequest(string? Email, string? Password, string? Name = null);

    /// <summary>
    /// What this panel is, so a client can tell whether it is older than the server it is talking to.
    ///
    /// Anonymous on purpose: `harbora update` has to work before, and regardless of, being signed in.
    /// The version comes from the assembly, which is stamped from the one number the whole product
    /// shares — a panel and a CLI reporting versions from different places could never be compared.
    /// </summary>
    [AllowAnonymous]
    [HttpGet("version")]
    public IActionResult Version() => Ok(new
    {
        server = ProductVersion,
        // The CLI is released from this repository at the same version, so this is also the newest
        // CLI that is known to match this panel.
        cli = ProductVersion
    });

    private static string ProductVersion =>
        typeof(ApiV1Controller).Assembly
            .GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?
            .InformationalVersion.Split('+')[0]
        ?? typeof(ApiV1Controller).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    [HttpGet("whoami")]
    public IActionResult WhoAmI() =>
        Ok(new { email = currentUser.Email, workspaceId = WorkspaceId });

    [HttpGet("apps")]
    public async Task<IActionResult> Apps(CancellationToken ct)
    {
        var apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .OrderBy(a => a.Name)
            .Select(a => new
             {
                 a.Id, a.Name, a.Slug,
                 status = a.Status.ToString(),
                 source = a.SourceType.ToString(),
                 // Whether the server has a repository it could pull from. The CLI used to assume that
                 // a local .git meant "let the server pull", which silently deployed nothing for an
                 // app created without a repository.
                 canServerPull = a.GitRepositoryId != null
             })
            .ToListAsync(ct);
        return Ok(apps);
    }

    /// <summary>
    /// The app's effective environment — the exact merge <c>DeploymentPipeline.BuildEnv</c> injects
    /// into a container and the panel's env page renders, via
    /// <see cref="Harbora.Infrastructure.Apps.EffectiveEnvironmentBuilder"/>. Backs <c>harbora env
    /// pull</c> (4.1, 2026-09-04 local-dev-parity plan) — the reason "effective" has to mean exactly
    /// what a deploy computes, not a second implementation the CLI keeps in step by hand.
    ///
    /// <para>
    /// Unlike the env page, which never decrypts a secret because it only ever needs to mask one, this
    /// hands back real plaintext: the whole point of the command is that a developer stops copying a
    /// credential out of the panel by hand. <c>isSecret</c> travels with every entry regardless, so the
    /// CLI can mark it in <c>.env.local</c> rather than writing it indistinguishably from an ordinary
    /// value. Gated on <see cref="Capabilities.AppsEnv"/> — the same capability that gates editing an
    /// env var in the panel — because handing back a secret's plaintext is at least as sensitive as
    /// changing one.
    /// </para>
    /// </summary>
    [HttpGet("apps/{slug}/env")]
    [Authorize(Policy = Capabilities.AppsEnv, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> Env(string slug, CancellationToken ct)
    {
        var app = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.ConfigGroups).ThenInclude(cg => cg.ConfigGroup!).ThenInclude(g => g.Entries)
            .Include(a => a.StorageBuckets).ThenInclude(sb => sb.StorageBucket)
            .Include(a => a.EmailProviders).ThenInclude(ep => ep.EmailProvider)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.Database)
            // 3.2 (round-2 market-gaps plan): a running read replica rides along with its primary's
            // own attachment — see AttachedReplicaEnv's own doc, and DeploymentPipeline.cs's matching
            // Include for why this is not a second, independent attachment. `harbora env pull` must
            // see the exact same REPLICA_URL a real deploy would inject, or the CLI and the container
            // disagree about what the app actually gets.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService!).ThenInclude(m => m.Replicas)
            .FirstOrDefaultAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == slug, ct);
        if (app is null) return NotFound(new { error = "App not found." });

        var merged = Harbora.Infrastructure.Apps.EffectiveEnvironmentBuilder.Compute(
            app, protector, storageOptions.Value.CustomerEndpoint);

        var entries = merged.Select(e => new
        {
            key = e.Key,
            value = e.IsSecret ? SafeUnprotect(e.Value) : e.Value,
            isSecret = e.IsSecret,
            source = e.Source.ToString()
        });

        return Ok(entries);
    }

    /// <summary>
    /// The same never-throw shape <c>DeploymentPipeline</c>'s own <c>SafeUnprotect</c> uses: a
    /// ciphertext this panel cannot decrypt (an old key, a corrupted row) must not turn a `harbora env
    /// pull` for nine working variables into a 500 over the tenth.
    /// </summary>
    private string SafeUnprotect(string ciphertext)
    {
        try { return protector.Unprotect(ciphertext); }
        catch { return string.Empty; }
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

    /// <summary>
    /// Stops a deployment that is queued or in flight — the CLI half of the panel's cancel button.
    ///
    /// <para>
    /// Its most important answer is the unglamorous one. A deployment can reach a terminal state
    /// between the caller reading its status and calling this, and <c>DeploymentStateMachine</c>
    /// treats Succeeded → Cancelled as illegal. So the status is checked before, and read back after,
    /// and a deployment that ended on its own is a <c>409</c> naming the state it ended in rather
    /// than a <c>500</c> or — far worse — a <c>200</c> for a cancellation that never happened.
    /// </para>
    /// </summary>
    [HttpPost("deployments/{id:guid}/cancel")]
    [Authorize(Policy = Capabilities.AppsDeploy, AuthenticationSchemes = TokenAuthenticationHandler.SchemeName)]
    public async Task<IActionResult> CancelDeployment(Guid id, CancellationToken ct)
    {
        var deployment = await db.Deployments
            .Where(d => d.Id == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => new { d.Id, d.Number, d.Status })
            .FirstOrDefaultAsync(ct);

        // Same answer for "not yours" as for "not there", like every other endpoint here.
        if (deployment is null) return NotFound(new { error = "Deployment not found." });

        if (Harbora.Domain.Deployments.DeploymentStateMachine.IsTerminal(deployment.Status))
            return Conflict(new { error = Ended(deployment.Number, deployment.Status) });

        await deployEngine.CancelAsync(id, ct);

        var settled = await db.Deployments.Where(d => d.Id == id)
            .Select(d => d.Status).FirstOrDefaultAsync(ct);

        if (settled != DeploymentStatus.Cancelled)
            return Conflict(new { error = Ended(deployment.Number, settled) });

        await audit.LogAsync("deployment.cancelled", "deployment", id.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(), workspaceId: WorkspaceId, ct: ct);

        return Ok(new { deploymentId = id, status = settled.ToString() });
    }

    private static string Ended(int number, DeploymentStatus status) =>
        $"Deployment #{number} had already ended ({status}), so there was nothing to cancel.";

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
