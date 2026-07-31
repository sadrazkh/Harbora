using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Jobs;
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
    IRollbackPlanner rollbackPlanner,
    IDomainInspector domains,
    Harbora.Infrastructure.Projects.ProjectService projects,
    IJobQueue jobs,
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
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(Guid? environmentId, CancellationToken ct)
    {
        await projects.EnsureDefaultEnvironmentAsync(WorkspaceId, ct);
        await PopulateTemplates(ct);
        // Arriving from a project page pre-selects that environment.
        ViewData["EnvironmentId"] = environmentId;
        return View(new CreateAppViewModel());
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(CreateAppViewModel model, CancellationToken ct)
    {
        // Auto-derive a unique slug from the name (keeps the form to just "name + source").
        var slug = await UniqueSlugAsync(Slugify(string.IsNullOrWhiteSpace(model.Slug) ? model.Name : model.Slug!), ct);

        if (model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile or AppSourceType.StaticSite
            && string.IsNullOrWhiteSpace(model.CloneUrl))
            ModelState.AddModelError(nameof(model.CloneUrl), "A Git repository URL is required.");

        if (model.SourceType == AppSourceType.PrebuiltImage && string.IsNullOrWhiteSpace(model.PrebuiltImage))
            ModelState.AddModelError(nameof(model.PrebuiltImage), "An image reference is required.");

        // Checked here so an unreadable schedule is a form error rather than a job that silently
        // never runs and is only noticed weeks later.
        if (model.Kind == ServiceKind.Cron
            && !Harbora.Infrastructure.Deployments.CronSchedule.TryParse(model.CronExpression, out _, out var cronError))
            ModelState.AddModelError(nameof(model.CronExpression), cronError!);

        // A schedule with nothing to run is the quietest possible failure: the job fires on time,
        // the image starts and exits, and the history fills with successful runs that did nothing.
        if (model.Kind == ServiceKind.Cron && string.IsNullOrWhiteSpace(model.Command))
            ModelState.AddModelError(nameof(model.Command),
                "Enter the command this job should run, for example \"php artisan backup:run\".");

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

        // Everything belongs to a project from the moment it is created. Ownership of a chosen
        // environment is checked inside the service — see ResolveEnvironmentAsync.
        var environment = await projects.ResolveEnvironmentAsync(WorkspaceId, model.EnvironmentId, ct);

        var app = new App
        {
            WorkspaceId = WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = serverId,
            Name = model.Name,
            Slug = slug,
            SourceType = model.SourceType,
            Kind = model.Kind,
            ReleaseCommand = string.IsNullOrWhiteSpace(model.ReleaseCommand) ? null : model.ReleaseCommand.Trim(),
            CronExpression = model.Kind == ServiceKind.Cron && !string.IsNullOrWhiteSpace(model.CronExpression)
                ? model.CronExpression.Trim()
                : null,
            Command = model.Kind == ServiceKind.Cron && !string.IsNullOrWhiteSpace(model.Command)
                ? model.Command.Trim()
                : null,
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
        var rootDomain = await db.Settings.Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.PlatformRootDomain)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);
        var host = Harbora.Infrastructure.Deployments.ServicePlan.HostFor(model.Kind, model.Domain, slug, rootDomain);
        if (!string.IsNullOrWhiteSpace(host) && !await db.Domains.AnyAsync(d => d.Host == host, ct))
            app.Domains.Add(new DomainName { Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true });

        // A template describes more than an image and a port. Until this was applied, an app
        // created from one arrived without the volume it declared — a static site whose content
        // vanished on every redeploy — and without the variables it said it needed.
        string? templateAdvice = null;
        if (model.TemplateId is { } templateId)
            templateAdvice = await ApplyTemplateAsync(app, templateId, ct);

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

        if (templateAdvice is not null) TempData["Message"] = templateAdvice;
        return RedirectToAction(nameof(Details), new { id = app.Id });
    }

    /// <summary>
    /// Gives the app what its template declares: the volumes its data lives in, and the variables it
    /// needs — secrets generated, plain ones left for a person and named in the message.
    /// </summary>
    private async Task<string?> ApplyTemplateAsync(App app, Guid templateId, CancellationToken ct)
    {
        var template = await db.AppTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct);
        if (template is null) return null;

        if (!Harbora.Infrastructure.Templates.TemplateManifest.TryParse(
                template.ManifestJson, out var manifest, out _))
            return null;

        var plan = Harbora.Infrastructure.Templates.TemplateSetup.Prepare(
            manifest!, () => Harbora.Infrastructure.Services.ServiceCredentials.Generate());

        foreach (var mount in plan.VolumeMounts)
            app.Volumes.Add(new Volume
            {
                // Named after the app, not the template: two apps from one template must not share
                // a volume and therefore each other's data.
                Name = $"harbora-vol-{app.Slug}-{Slugify(mount.Trim('/'))}",
                MountPath = mount
            });

        foreach (var variable in plan.Variables)
            app.EnvironmentVariables.Add(new EnvironmentVariable
            {
                Key = variable.Key,
                Value = variable.Secret && variable.Value is not null
                    ? protector.Protect(variable.Value)
                    : variable.Value ?? "",
                IsSecret = variable.Secret
            });

        if (!string.IsNullOrWhiteSpace(manifest!.Image)) app.PrebuiltImage ??= manifest.Image;
        if (manifest.Port is { } port && app.ContainerPort <= 0) app.ContainerPort = port;

        return Harbora.Infrastructure.Templates.TemplateSetup.Advice(plan);
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

        // A scheduled job has no container to look at, so its history IS the app: whether it ran,
        // whether it worked, and what it said. Loaded only for cron services — every other kind
        // would pay for a query that returns nothing.
        if (app.Kind == ServiceKind.Cron)
            ViewBag.CronRuns = await db.CronRuns
                .Where(r => r.AppId == app.Id)
                .OrderByDescending(r => r.StartedAt)
                .Take(20)
                .ToListAsync(ct);

        return View(app);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
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

        Guid deploymentId;
        try
        {
            deploymentId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, gitRef ?? app.GitRef), ct);
        }
        catch (InvalidOperationException ex)
        {
            // e.g. a rollback is mid-flight — surface the reason instead of a 500.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        await audit.LogAsync("app.deploy", "app", id.ToString(), ClientIp, ct: ct);

        return RedirectToAction("Details", "Deployments", new { id = deploymentId });
    }

    /// <summary>
    /// Confirmation step for a rollback: shows exactly which version would be restored and blocks
    /// up front if the artifact is gone, instead of failing part-way through the deploy.
    /// </summary>
    [HttpGet]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> ConfirmRollback(Guid id, Guid deploymentId, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var plan = await rollbackPlanner.PrepareAsync(id, deploymentId, ct);
        return View(new RollbackViewModel(app.Id, app.Name, deploymentId, plan));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Rollback(Guid id, Guid deploymentId, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // Re-check rather than trusting the confirmation screen: retention could have pruned the
        // image between rendering the page and submitting it.
        var plan = await rollbackPlanner.PrepareAsync(id, deploymentId, ct);
        if (!plan.CanRollback)
        {
            TempData["Error"] = plan.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        Guid newId;
        try
        {
            newId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Rollback, currentUser.UserId ?? Guid.Empty,
                    RollbackToDeploymentId: deploymentId), ct);
        }
        catch (InvalidOperationException ex)
        {
            // A rollback must never be silently coalesced onto an in-flight deploy — say so.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        await audit.LogAsync("app.rollback", "app", id.ToString(), ClientIp,
            metadataJson: $"{{\"toDeploymentId\":\"{deploymentId}\"}}", ct: ct);
        return RedirectToAction("Details", "Deployments", new { id = newId });
    }

    // ---- lifecycle ----

    /// <summary>
    /// Runs a scheduled job now, without disturbing its schedule.
    ///
    /// Queued rather than run inline: a job can take minutes, and the request that started it must
    /// not be what keeps it alive. Going through the durable queue also means a restart mid-run
    /// resumes from the database instead of losing it.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> RunNow(Guid id, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        if (app.Kind != ServiceKind.Cron)
        {
            TempData["Error"] = "Only a scheduled job can be run this way.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Said here rather than discovered as a run that quietly never appears.
        if (await db.CronRuns.AnyAsync(r => r.AppId == id && r.FinishedAt == null, ct))
        {
            TempData["Error"] = "This job is already running.";
            return RedirectToAction(nameof(Details), new { id });
        }

        await jobs.EnqueueAsync(JobKind.CronRun, app.Id, ct);
        await audit.LogAsync("app.cron.run", "app", id.ToString(), ClientIp, ct: ct);
        TempData["Message"] = "Started. The run will appear below when it finishes.";
        return RedirectToAction(nameof(Details), new { id });
    }
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> Restart(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.RestartAsync(id, ct);
        TempData["Message"] = "Restarted.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> Stop(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.StopAsync(id, ct);
        TempData["Message"] = "Stopped.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> Start(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();
        await ops.StartAsync(id, ct);
        TempData["Message"] = "Started.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDelete)]
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

    /// <summary>
    /// Checks what a browser would actually get for this domain: where DNS points, and which
    /// certificate is live. The Domains list otherwise shows "SSL" because a checkbox was ticked.
    /// </summary>
    [HttpGet("/apps/{id:guid}/domains/{domainId:guid}/check")]
    public async Task<IActionResult> CheckDomain(Guid id, Guid domainId, CancellationToken ct)
    {
        var host = await db.Domains
            .Where(d => d.Id == domainId && d.AppId == id && d.App!.WorkspaceId == WorkspaceId)
            .Select(d => d.Host)
            .FirstOrDefaultAsync(ct);
        if (host is null) return NotFound();

        var status = await domains.InspectAsync(host, ct);
        return Ok(new
        {
            host = status.Host,
            readiness = status.Readiness.ToString(),
            ready = status.IsReady,
            summary = status.Summary,
            action = status.Action,
            resolvedIps = status.Probe.ResolvedIps,
            expectedIps = status.Probe.ExpectedIps,
            issuer = status.Probe.CertificateIssuer,
            expiresAt = status.Probe.CertificateExpiresAt
        });
    }

    // ---- environment variables ----

    [HttpPost("/apps/{id:guid}/env")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
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
    [Authorize(Policy = Capabilities.AppsEnv)]
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
    [Authorize(Policy = Capabilities.AppsEnv)]
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
    [Authorize(Policy = Capabilities.AppsEnv)]
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
        // Every project's environments, so a service can be created where it belongs rather than
        // always landing in the default one.
        ViewBag.Environments = await db.Environments
            .Where(e => e.WorkspaceId == WorkspaceId)
            .OrderBy(e => e.Project!.Name).ThenByDescending(e => e.IsDefault).ThenBy(e => e.Name)
            .Select(e => new SelectListItem($"{e.Project!.Name} · {e.Name}", e.Id.ToString()))
            .ToListAsync(ct);

        // The same visibility rule the catalog screen uses: offering a template another tenant
        // wrote and nobody reviewed would run their image inside this one's private network.
        var templates = (await db.AppTemplates
                .OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct))
            .Where(t => Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(t, WorkspaceId))
            .ToList();
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
