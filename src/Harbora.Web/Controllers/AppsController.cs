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
    IAppOperationsService ops,
    IQuotaService quota,
    ISchedulerService scheduler,
    ISecretProtector protector,
    IAuditLogger audit,
    ICurrentUser currentUser) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

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
        // Auto-derive a unique slug from the name (keeps the form to just "name + source").
        var slug = await UniqueSlugAsync(Slugify(string.IsNullOrWhiteSpace(model.Slug) ? model.Name : model.Slug!), ct);

        if (model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile or AppSourceType.StaticSite
            && string.IsNullOrWhiteSpace(model.CloneUrl))
            ModelState.AddModelError(nameof(model.CloneUrl), "A Git repository URL is required.");

        if (model.SourceType == AppSourceType.PrebuiltImage && string.IsNullOrWhiteSpace(model.PrebuiltImage))
            ModelState.AddModelError(nameof(model.PrebuiltImage), "An image reference is required.");

        // Resolve the instance size (drives container limits + scheduling).
        var size = string.IsNullOrWhiteSpace(model.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.FirstOrDefaultAsync(s => s.Key == model.InstanceSizeKey, ct);
        var needMem = size?.MemoryBytes ?? 0;
        var needCpu = size?.CpuCores ?? 0;

        // Enforce the workspace's plan quota before creating anything.
        var check = await quota.CanAddAppAsync(WorkspaceId, model.InstanceSizeKey, excludeAppId: null, ct);
        if (!check.Allowed)
            ModelState.AddModelError(string.Empty, check.Reason ?? "Plan quota exceeded.");

        // Place on a node with capacity: honour an explicit choice (guarded) or auto-schedule.
        var placement = model.ServerId is { } chosen && await db.Servers.AnyAsync(s => s.Id == chosen, ct)
            ? await scheduler.CheckAsync(chosen, needMem, needCpu, ct)
            : await scheduler.PlaceAsync(needMem, needCpu, await PlanPoolAsync(ct), ct);
        if (!placement.Ok)
            ModelState.AddModelError(string.Empty, placement.Reason ?? "No server has capacity for this instance size.");

        if (!ModelState.IsValid)
        {
            await PopulateTemplates(ct);
            return View(model);
        }

        var serverId = placement.ServerId!.Value;
        var app = new App
        {
            WorkspaceId = WorkspaceId,
            ServerId = serverId,
            Name = model.Name,
            Slug = slug,
            SourceType = model.SourceType,
            ContainerPort = model.ContainerPort <= 0 ? 80 : model.ContainerPort,
            DockerfilePath = model.DockerfilePath,
            PrebuiltImage = model.PrebuiltImage,
            GitRef = model.GitRef,
            TemplateId = model.TemplateId,
            InstanceSizeKey = size?.Key,
            MemoryLimitBytes = size?.MemoryBytes ?? 0,
            CpuLimit = size?.CpuCores ?? 0
        };

        if (model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile or AppSourceType.StaticSite)
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

        // Domain: use the one given, else auto-assign {slug}.{root domain} so the app is instantly reachable.
        var host = model.Domain?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host))
        {
            var root = await db.Settings.Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.PlatformRootDomain)
                .Select(s => s.Value).FirstOrDefaultAsync(ct);
            if (!string.IsNullOrWhiteSpace(root)) host = $"{slug}.{root.Trim()}";
        }
        if (!string.IsNullOrWhiteSpace(host) && !await db.Domains.AnyAsync(d => d.Host == host, ct))
            app.Domains.Add(new DomainName { Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true });

        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);

        // "Give it a repo and it just works": build + deploy right away and show live logs.
        var canDeploy = model.SourceType is AppSourceType.GitRepository
            or AppSourceType.Dockerfile or AppSourceType.PrebuiltImage or AppSourceType.StaticSite;
        if (model.DeployNow && canDeploy)
        {
            var deploymentId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, app.GitRef), ct);
            return RedirectToAction("Details", "Deployments", new { id = deploymentId });
        }

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

        // Block deploys for suspended/over-quota workspaces (excludes this app from the count).
        var check = await quota.CanAddAppAsync(WorkspaceId, app.InstanceSizeKey, excludeAppId: app.Id, ct);
        if (!check.Allowed)
        {
            TempData["Error"] = check.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        var deploymentId = await deployEngine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, gitRef ?? app.GitRef), ct);
        await audit.LogAsync("app.deploy", "app", id.ToString(), ClientIp, ct: ct);

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
        await audit.LogAsync("app.rollback", "app", id.ToString(), ClientIp,
            metadataJson: $"{{\"toDeploymentId\":\"{deploymentId}\"}}", ct: ct);
        return RedirectToAction("Details", "Deployments", new { id = newId });
    }

    // ---- lifecycle ----

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Restart(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.RestartAsync(id, ct);
        TempData["Message"] = "Restarted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.StopAsync(id, ct);
        TempData["Message"] = "Stopped.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.StartAsync(id, ct);
        TempData["Message"] = "Started.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, bool removeVolumes, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.DeleteAsync(id, removeVolumes, ct);
        await audit.LogAsync("app.delete", "app", id.ToString(), ClientIp,
            metadataJson: $"{{\"removeVolumes\":{removeVolumes.ToString().ToLowerInvariant()}}}", ct: ct);
        TempData["Message"] = "App deleted.";
        return RedirectToAction(nameof(Index));
    }

    // ---- logs ----

    [HttpGet("/apps/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        return View(app);
    }

    [HttpGet("/apps/{id:guid}/logs/data")]
    public async Task<IActionResult> LogsData(Guid id, int tail = 200, CancellationToken ct = default)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        return Content(await ops.GetLogsAsync(id, tail, ct), "text/plain");
    }

    // ---- environment variables ----

    [HttpPost("/apps/{id:guid}/env")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddEnv(Guid id, string key, string? value, bool isSecret, bool availableAtBuild, CancellationToken ct)
    {
        var app = await db.Apps.Include(a => a.EnvironmentVariables).FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        if (string.IsNullOrWhiteSpace(key)) { TempData["Error"] = "Key is required."; return RedirectToAction(nameof(Details), new { id }); }

        var existing = app.EnvironmentVariables.FirstOrDefault(e => e.Key == key);
        var stored = isSecret ? protector.Protect(value ?? "") : value ?? "";
        if (existing is null)
            app.EnvironmentVariables.Add(new EnvironmentVariable { Key = key, Value = stored, IsSecret = isSecret, AvailableAtBuild = availableAtBuild });
        else { existing.Value = stored; existing.IsSecret = isSecret; existing.AvailableAtBuild = availableAtBuild; }
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Variable saved. Redeploy to apply.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/env/{envId:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteEnv(Guid id, Guid envId, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await db.EnvironmentVariables.Where(e => e.Id == envId && e.AppId == id).ExecuteDeleteAsync(ct);
        TempData["Message"] = "Variable removed. Redeploy to apply.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- domains ----

    [HttpPost("/apps/{id:guid}/domains")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddDomain(Guid id, string host, bool ssl, CancellationToken ct)
    {
        var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        host = (host ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) { TempData["Error"] = "Host is required."; return RedirectToAction(nameof(Details), new { id }); }
        if (await db.Domains.AnyAsync(d => d.Host == host, ct)) { TempData["Error"] = "This domain is already in use."; return RedirectToAction(nameof(Details), new { id }); }

        app.Domains.Add(new DomainName { Host = host, SslEnabled = ssl, ForceHttps = ssl, IsPrimary = app.Domains.Count == 0 });
        await db.SaveChangesAsync(ct);
        TempData["Message"] = "Domain added. Redeploy to route it.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/domains/{domainId:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteDomain(Guid id, Guid domainId, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        var host = await db.Domains.Where(d => d.Id == domainId && d.AppId == id).Select(d => d.Host).FirstOrDefaultAsync(ct);
        await db.Domains.Where(d => d.Id == domainId && d.AppId == id).ExecuteDeleteAsync(ct);
        if (host is not null) await db.Routes.Where(r => r.AppId == id && r.Host == host).ExecuteDeleteAsync(ct);
        TempData["Message"] = "Domain removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    private Task<bool> OwnsAsync(Guid appId, CancellationToken ct) =>
        db.Apps.AnyAsync(a => a.Id == appId && a.WorkspaceId == WorkspaceId, ct);

    private async Task PopulateTemplates(CancellationToken ct)
    {
        var templates = await db.AppTemplates.Where(t => t.IsEnabled)
            .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);
        ViewBag.Templates = templates.Select(t => new SelectListItem($"{t.Name}", t.Id.ToString())).ToList();

        ViewBag.Servers = await db.Servers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name)
            .Select(s => new SelectListItem(s.IsLocal ? s.Name + " (local)" : s.Name, s.Id.ToString())).ToListAsync(ct);

        // Offer only the instance sizes this workspace's plan allows.
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } pid
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == pid, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);
        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct);
        ViewBag.Sizes = sizes
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new SelectListItem($"{s.Name} — {s.CpuCores} vCPU / {s.MemoryBytes / 1024 / 1024} MB", s.Key))
            .ToList();
    }

    /// <summary>The node pool this workspace's plan restricts placement to (null = any pool).</summary>
    private async Task<string?> PlanPoolAsync(CancellationToken ct)
    {
        var planId = await db.Workspaces.Where(w => w.Id == WorkspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);
        var pool = planId is { } pid
            ? await db.Plans.Where(p => p.Id == pid).Select(p => p.NodePool).FirstOrDefaultAsync(ct)
            : await db.Plans.Where(p => p.IsDefault).Select(p => p.NodePool).FirstOrDefaultAsync(ct);
        return string.IsNullOrWhiteSpace(pool) ? null : pool;
    }

    private static string Slugify(string value)
    {
        var chars = (value ?? "").Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        if (slug.Length > 50) slug = slug[..50].Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "app" : slug;
    }

    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var n = 2; await db.Apps.AnyAsync(a => a.WorkspaceId == WorkspaceId && a.Slug == slug, ct); n++)
            slug = $"{baseSlug}-{n}";
        return slug;
    }

    private static string DeriveRepoName(string cloneUrl)
    {
        var trimmed = cloneUrl.TrimEnd('/');
        var name = trimmed[(trimmed.LastIndexOf('/') + 1)..];
        return name.EndsWith(".git") ? name[..^4] : name;
    }
}
