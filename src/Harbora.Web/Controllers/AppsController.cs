using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Networking;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed class AppsController(
    HarboraDbContext db,
    IDeploymentEngine deployEngine,
    ISecretProtector protector,
    ICurrentUser currentUser,
    ISystemClock clock) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var apps = await db.Apps.Where(a => a.WorkspaceId == WorkspaceId)
            .OrderByDescending(a => a.UpdatedAt).ToListAsync(ct);
        return View(apps);
    }

    [HttpGet]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        await PopulateTemplates(ct);
        return View(new CreateAppViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAppViewModel model, CancellationToken ct)
    {
        if (await db.Apps.AnyAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == model.Slug, ct))
            ModelState.AddModelError(nameof(model.Slug), "An app with this slug already exists.");

        if (model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile
            && string.IsNullOrWhiteSpace(model.CloneUrl))
            ModelState.AddModelError(nameof(model.CloneUrl), "A Git repository URL is required.");

        if (model.SourceType == AppSourceType.PrebuiltImage && string.IsNullOrWhiteSpace(model.PrebuiltImage))
            ModelState.AddModelError(nameof(model.PrebuiltImage), "An image reference is required.");

        if (!ModelState.IsValid)
        {
            await PopulateTemplates(ct);
            return View(model);
        }

        var serverId = await db.Servers.Where(s => s.IsLocal).Select(s => s.Id).FirstAsync(ct);
        var app = new App
        {
            WorkspaceId = WorkspaceId,
            ServerId = serverId,
            Name = model.Name,
            Slug = model.Slug,
            SourceType = model.SourceType,
            ContainerPort = model.ContainerPort,
            DockerfilePath = model.DockerfilePath,
            PrebuiltImage = model.PrebuiltImage,
            GitRef = model.GitRef,
            TemplateId = model.TemplateId
        };

        if (model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile)
        {
            var provider = new GitProvider
            {
                WorkspaceId = WorkspaceId,
                Name = "Custom",
                Type = GitProviderType.Custom,
                ApiBaseUrl = string.Empty,
                EncryptedCredential = string.IsNullOrWhiteSpace(model.GitToken) ? null : protector.Protect(model.GitToken)
            };
            var repo = new GitRepository
            {
                Provider = provider,
                FullName = DeriveRepoName(model.CloneUrl!),
                CloneUrl = model.CloneUrl!,
                DefaultBranch = model.GitRef ?? "main",
                WebhookSecret = Guid.NewGuid().ToString("N")
            };
            db.GitProviders.Add(provider);
            db.GitRepositories.Add(repo);
            app.GitRepository = repo;
        }

        if (!string.IsNullOrWhiteSpace(model.Domain))
            app.Domains.Add(new DomainName { Host = model.Domain.Trim().ToLowerInvariant(), SslEnabled = true, ForceHttps = true, IsPrimary = true });

        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Details), new { id = app.Id });
    }

    public async Task<IActionResult> Details(Guid id, CancellationToken ct)
    {
        var app = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Domains)
            .Include(a => a.Deployments.OrderByDescending(d => d.Number).Take(20))
            .Include(a => a.GitRepository)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        return View(app);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Deploy(Guid id, string? gitRef, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var deploymentId = await deployEngine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, gitRef ?? app.GitRef), ct);

        return RedirectToAction("Details", "Deployments", new { id = deploymentId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rollback(Guid id, Guid deploymentId, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var newId = await deployEngine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Rollback, currentUser.UserId ?? Guid.Empty,
                RollbackToDeploymentId: deploymentId), ct);
        return RedirectToAction("Details", "Deployments", new { id = newId });
    }

    private async Task PopulateTemplates(CancellationToken ct)
    {
        var templates = await db.AppTemplates.Where(t => t.IsEnabled)
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        ViewBag.Templates = templates.Select(t => new SelectListItem($"{t.Name}", t.Id.ToString())).ToList();
    }

    private static string DeriveRepoName(string cloneUrl)
    {
        var trimmed = cloneUrl.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return name.EndsWith(".git") ? name[..^4] : name;
    }
}
