using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Jobs;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Networking;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

[Authorize]
public sealed partial class AppsController(
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
    Harbora.Infrastructure.Security.ProjectAccessService access,
    Harbora.Infrastructure.Services.ServiceUsageService serviceUsage,
    IServerEngineFactory engines,
    IProxyEngine proxy,
    ILogger<AppsController> logger,
    IJobQueue jobs,
    IConfiguration config,
    ICurrentUser currentUser,
    Harbora.Infrastructure.Billing.ResourceCreationBilling creationBilling,
    AppAddressAssigner addresses) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>
    /// Whether a typed host is one the platform answers on itself — see <see cref="ReservedHosts"/>
    /// for what claiming the node channel's host silently costs.
    ///
    /// <para>
    /// Read from configuration on each call rather than cached or mirrored into the database: these
    /// are per-install values (<c>PANEL_DOMAIN</c>, <c>NodeAgent__PublicUrl</c>,
    /// <c>Storage__S3__PublicEndpoint</c> in <c>deploy/.env</c>) that <c>harbora set-domain</c> can
    /// change under a running panel, and a second copy is a second thing to be wrong.
    /// </para>
    /// </summary>
    private bool IsReservedHost(string? host) =>
        ReservedHosts.IsReserved(host, ReservedHosts.ForPlatform(
            config["PANEL_DOMAIN"], config["NodeAgent:PublicUrl"], config["Storage:S3:PublicEndpoint"]));

    private string ReservedHostRefusal(string host) => IsFa
        ? $"«{host}» یکی از نام‌های خودِ سامانه است و نمی‌توان آن را به یک اپ داد."
        : $"'{host}' is one of the platform's own host names and cannot be routed to an app.";

    /// <summary>The same refusal AddDomain has always given a typed host that is already taken.</summary>
    private string TakenHostRefusal(string host) => IsFa
        ? $"«{host}» پیش‌تر استفاده شده است."
        : $"'{host}' is already in use.";

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var query = db.Apps.Where(a => a.WorkspaceId == WorkspaceId);

        // A list that shows what the buttons refuse is worse than a shorter list. Null means "every
        // project", which is the common case and must not be confused with an empty list.
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            query = query.Where(a => a.EnvironmentId != null && visible.Contains(a.Environment!.ProjectId));

        var apps = await query
            .AsNoTracking()
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .Include(a => a.Domains.Where(d => d.IsPrimary))
            .Include(a => a.Deployments.OrderByDescending(d => d.Number).Take(1))
            .OrderByDescending(a => a.UpdatedAt)
            .ToListAsync(ct);
        var operable = await access.TouchableAppIdsAsync(
            apps.Select(a => a.Id), Capabilities.AppsOperate, ct);
        var activeDeploymentIds = apps
            .Where(a => a.ActiveDeploymentId is not null && a.SourceType != AppSourceType.DockerCompose)
            .Select(a => a.ActiveDeploymentId!.Value)
            .ToList();
        var activeNumbers = activeDeploymentIds.Count == 0
            ? new Dictionary<Guid, int>()
            : await db.Deployments.AsNoTracking()
                .Where(d => activeDeploymentIds.Contains(d.Id))
                .ToDictionaryAsync(d => d.Id, d => d.Number, ct);
        var metricRefs = apps
            .Where(a => a.ActiveDeploymentId is { } id && activeNumbers.ContainsKey(id))
            .ToDictionary(
                a => a.Id,
                a => Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(
                    a.Slug, activeNumbers[a.ActiveDeploymentId!.Value]));
        var resourceRefs = metricRefs.Values.Distinct().ToList();
        var metrics = resourceRefs.Count == 0
            ? new List<MonitoringMetric>()
            : await db.MonitoringMetrics.AsNoTracking()
                .Where(m => m.ResourceRef != null && resourceRefs.Contains(m.ResourceRef)
                            && (m.Name == "cpu.percent" || m.Name == "mem.used"))
                .OrderByDescending(m => m.Timestamp).Take(2_000).ToListAsync(ct);

        return View(new ApplicationsPageViewModel
        {
            Apps = apps.Select(a =>
            {
                var deployment = a.Deployments.OrderByDescending(d => d.Number).FirstOrDefault();
                metricRefs.TryGetValue(a.Id, out var metricRef);
                var cpu = metricRef is null ? null : metrics
                    .FirstOrDefault(m => m.ResourceRef == metricRef && m.Name == "cpu.percent")?.Value;
                var memory = metricRef is null ? null : metrics
                    .FirstOrDefault(m => m.ResourceRef == metricRef && m.Name == "mem.used")?.Value;
                return new ApplicationRowViewModel(
                    a.Id, a.Name, a.Slug, a.SourceType, a.Kind, a.Status,
                    a.Environment?.Project?.Name ?? "—", a.Environment?.Name ?? "—",
                    a.Domains.FirstOrDefault(d => d.IsPrimary)?.Host,
                    a.InstanceSizeKey, deployment?.Status, deployment?.Number,
                    deployment?.FinishedAt ?? deployment?.CreatedAt,
                    deployment?.CommitSha is { Length: > 0 } sha ? sha[..Math.Min(7, sha.Length)] : null,
                    operable.Contains(a.Id), cpu, memory is null ? null : (long?)memory.Value,
                    a.MemoryLimitBytes);
            }).ToList(),
            QuickStarts = await FeaturedCardsAsync(6, ct)
        });
    }

    [HttpGet]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(
        Guid? environmentId,
        Guid? templateId,
        AppSourceType? source,
        CancellationToken ct)
    {
        // Legacy dashboard/catalog links used to land here with ?templateId=, but the form ignored
        // it and created an ordinary Git app. Templates now have a reviewable stack deployment
        // flow; keep old bookmarks useful by routing them to it.
        if (templateId is { } selected)
            return Redirect($"/templates/{selected}/deploy");

        await projects.EnsureDefaultEnvironmentAsync(WorkspaceId, ct);
        await PopulateTemplates(ct);
        // Arriving from a project page pre-selects that environment.
        ViewData["EnvironmentId"] = environmentId;
        return View(new CreateAppViewModel
        {
            EnvironmentId = environmentId,
            SourceType = source ?? AppSourceType.GitRepository
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsCreate)]
    public async Task<IActionResult> Create(CreateAppViewModel model, CancellationToken ct)
    {
        // Auto-derive a unique slug from the name (keeps the form to just "name + source").
        var slug = await UniqueSlugAsync(Slugify(string.IsNullOrWhiteSpace(model.Slug) ? model.Name : model.Slug!), ct);

        var usesRepository = model.SourceType is AppSourceType.GitRepository or AppSourceType.Dockerfile
            or AppSourceType.StaticSite or AppSourceType.DockerCompose;

        if (usesRepository
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

        // Keep the count we are about to read stable until the app and its first-hour debit commit.
        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);

        // Enforce the workspace's plan quota before creating anything.
        var check = await quota.CanAddAppAsync(WorkspaceId, model.InstanceSizeKey, excludeAppId: null, ct);
        if (!check.Allowed)
            ModelState.AddModelError(string.Empty, check.Reason ?? "Plan quota exceeded.");
        if (model.Kind == ServiceKind.Cron)
        {
            var cronCheck = await quota.CanAddWorkloadsAsync(WorkspaceId,
                new WorkloadQuotaDelta(CronJobs: 1), ct);
            if (!cronCheck.Allowed)
                ModelState.AddModelError(string.Empty,
                    (IsFa ? cronCheck.ReasonFa : null) ?? cronCheck.Reason ?? "Scheduled-job quota exceeded.");
        }

        // The environment id is a writable form value. Workspace ownership alone is not enough:
        // a project-scoped member must not create an app in another project in the same workspace.
        var environment = await projects.ResolveEnvironmentAsync(WorkspaceId, model.EnvironmentId, ct);
        if (!await access.AllowsAsync(
                new ResourcePlacement(environment.ProjectId, environment.Id), Capabilities.AppsCreate, ct))
        {
            ModelState.AddModelError(nameof(model.EnvironmentId),
                "You do not have permission to create an app in this environment.");
        }

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
            // Only meaningful for a service built from a repository — there is no branch to
            // preview otherwise.
            PreviewsEnabled = model.PreviewsEnabled && usesRepository,
            ContainerPort = model.ContainerPort <= 0 ? 80 : model.ContainerPort,
            DockerfilePath = model.DockerfilePath,
            ComposeFilePath = model.SourceType == AppSourceType.DockerCompose
                ? (string.IsNullOrWhiteSpace(model.ComposeFilePath) ? null : model.ComposeFilePath.Trim())
                : null,
            PrebuiltImage = model.PrebuiltImage,
            GitRef = model.GitRef,
            TemplateId = model.TemplateId,
            InstanceSizeKey = size?.Key,
            MemoryLimitBytes = size?.MemoryBytes ?? 0,
            DiskLimitBytes = size?.DiskBytes ?? 0,
            CpuLimit = size?.CpuCores ?? 0
        };

        if (usesRepository)
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

        // One rule, one place. This used to derive the host here, check reserved names here, and then
        // silently skip the insert when the name was taken — so an app could be created with no
        // address and no explanation. AppAddressAssigner answers all three, and says which happened.
        //
        // Typed vs. derived matters for what a collision means: a blank field gets the slug-derived
        // name, which may be discriminated the way a clone's own name is; a name someone actually
        // typed is a promise made to them, and a collision on it is refused instead — see
        // AppAddressRequestOrigin.
        var origin = string.IsNullOrWhiteSpace(model.Domain)
            ? AppAddressRequestOrigin.Derived
            : AppAddressRequestOrigin.Typed;
        var addressed = await addresses.AssignAsync(app, model.Domain, origin, suffix: null, ct);
        if (addressed.Outcome == AppAddressOutcome.Reserved)
        {
            // model.Domain is null whenever this came from the derived name rather than a typed one —
            // recompute the same host AppAddress.Decide settled on internally, so the message names
            // what actually offended instead of rendering an empty pair of quotes.
            var offendingHost = Harbora.Infrastructure.Deployments.ServicePlan.HostFor(
                app.Kind, model.Domain, app.Slug, await addresses.RootDomainAsync(ct)) ?? model.Domain ?? "";
            ModelState.AddModelError(nameof(model.Domain), ReservedHostRefusal(offendingHost));
            await PopulateTemplates(ct);
            return View(model);
        }
        if (addressed.Outcome == AppAddressOutcome.Taken)
        {
            // The same refusal AddDomain has always given a typed name that collides — not
            // discriminated onto a zone with no DNS record for the mangled name and no wildcard
            // certificate to cover it.
            ModelState.AddModelError(nameof(model.Domain), TakenHostRefusal(model.Domain!));
            await PopulateTemplates(ct);
            return View(model);
        }
        if (addressed.Outcome == AppAddressOutcome.Discriminated)
            TempData["Message"] = IsFa
                ? $"نام درخواستی گرفته شده بود؛ این اپ روی «{addressed.Host}» در دسترس است."
                : $"That name was taken, so this app is reachable at '{addressed.Host}'.";
        else if (addressed.Outcome == AppAddressOutcome.Exhausted)
            // Every discriminated attempt collided too — the enum's own docstring says this is "said
            // out loud rather than skipped", so it is: silence here is the exact defect this branch
            // removed everywhere else.
            TempData["Message"] = IsFa
                ? "چند نام دیگر هم گرفته شده بود، پس این اپ فعلاً بدون آدرس ساخته شد."
                : "Several names were already taken, so this app was created with no address for now.";

        // A template describes more than an image and a port. Until this was applied, an app
        // created from one arrived without the volume it declared — a static site whose content
        // vanished on every redeploy — and without the variables it said it needed.
        string? templateAdvice = null;
        if (model.TemplateId is { } templateId)
            templateAdvice = await ApplyTemplateAsync(app, templateId, ct);

        var governed = await quota.CanAddGovernedResourcesAsync(WorkspaceId,
            new GovernanceQuotaDelta(Domains: app.Domains.Count, Volumes: app.Volumes.Count), ct);
        if (!governed.Allowed)
        {
            ModelState.AddModelError(string.Empty,
                (IsFa ? governed.ReasonFa : null) ?? governed.Reason ?? "Plan quota exceeded.");
            await PopulateTemplates(ct);
            return View(model);
        }

        db.Apps.Add(app);
        try
        {
            await creationBilling.SaveAsync(WorkspaceId,
                [new(Harbora.Domain.Billing.BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], ct);
        }
        catch (Harbora.Infrastructure.Billing.CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            ModelState.AddModelError(string.Empty, IsFa ? ex.ReasonFa : ex.Message);
            await PopulateTemplates(ct);
            return View(model);
        }

        await quotaReservation.CommitAsync(ct);

        // "Give it a repo and it just works": build + deploy right away and show live logs.
        var canDeploy = model.SourceType is AppSourceType.GitRepository
            or AppSourceType.Dockerfile or AppSourceType.DockerCompose
            or AppSourceType.PrebuiltImage or AppSourceType.StaticSite;
        if (model.DeployNow && canDeploy)
        {
            try
            {
                var deploymentId = await deployEngine.QueueDeploymentAsync(
                    new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, app.GitRef), ct);
                return RedirectToAction("Details", "Deployments", new { id = deploymentId });
            }
            catch (QuotaRefusedException ex)
            {
                // The application and its first-hour debit are already committed. Keep it visible
                // and explain that its initial deploy must wait for another deployment to finish.
                TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
                return RedirectToAction(nameof(Details), new { id = app.Id });
            }
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

    /// <param name="revealSecret">
    /// Whether to print the repository's webhook secret. Off unless asked for: it is what proves a
    /// push notification came from the provider, and this page is one an operator has every reason
    /// to be showing somebody while they set a repository up.
    /// </param>
    public async Task<IActionResult> Details(Guid id, [FromQuery] bool revealSecret, CancellationToken ct)
    {
        ViewBag.RevealWebhookSecret = revealSecret;

        // Visibility, not an action capability: a viewer is allowed to read, and gating this on
        // "may you operate it" would lock them out of something the list is still showing them.
        if (!await access.CanSeeAppAsync(id, ct)) return NotFound();

        var app = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Domains)
            .Include(a => a.GitRepository)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // A scheduled job has no container to look at, so its history IS the app: whether it ran,
        // whether it worked, and what it said. Loaded only for cron services — every other kind
        // would pay for a query that returns nothing.
        // The previews this app has spawned. Without this the branches exist only in the app list,
        // indistinguishable from services somebody meant to create.
        if (app.GitRepositoryId is not null)
            ViewBag.Previews = await db.Apps
                .Where(a => a.PreviewOfAppId == app.Id)
                .OrderBy(a => a.PreviewBranch)
                .ToListAsync(ct);

        // The sizes this workspace may move between, so the resize control offers the same list the
        // create form did rather than a free-text box.
        ViewBag.Sizes = await SizeChoicesAsync(app.InstanceSizeKey, ct);

        // The databases this application is wired to, and the ones it could be.
        //
        // The application page said nothing about databases at all: the only way to attach one was
        // to know to open each database in turn and attach the app from there, and nothing here
        // listed what it was already using. So "one app, several databases" was supported by
        // everything except the screen somebody would try it on.
        var siblings = app.EnvironmentId is { } envId
            ? await db.ManagedServices.AsNoTracking()
                .Where(s => s.WorkspaceId == WorkspaceId && s.EnvironmentId == envId)
                .OrderBy(s => s.Name).ToListAsync(ct)
            : [];

        var wiredTo = serviceUsage
            .ConnectionsFor([app], siblings.Select(s => s.ContainerName))
            .TryGetValue(app.Id, out var hosts) ? hosts : [];

        // The databases this application cannot reach, and why.
        //
        // An environment is a private network, so a database in another one cannot be attached —
        // the hostname would not resolve. That was explained and left there: the person could see
        // the refusal and had nowhere to go with it. The move already existed, on a page in
        // Advanced mode that most people never open.
        ViewBag.Elsewhere = app.EnvironmentId is null
            ? new List<AppDatabaseElsewhereViewModel>()
            : await db.ManagedServices.AsNoTracking()
                .Include(s => s.Environment).ThenInclude(e => e!.Project)
                .Where(s => s.WorkspaceId == WorkspaceId && s.EnvironmentId != app.EnvironmentId)
                .OrderBy(s => s.Name)
                .Select(s => new AppDatabaseElsewhereViewModel(
                    s.Id, s.Name, s.Type,
                    s.Environment!.Project!.Name + " · " + s.Environment.Name,
                    s.EnvironmentId!.Value))
                .ToListAsync(ct);

        ViewBag.Databases = siblings
            .Select(s => new AppDatabaseLinkViewModel(
                s.Id, s.Name, s.Type, s.ContainerName,
                wiredTo.Contains(s.ContainerName),
                Harbora.Infrastructure.Services.AttachKeys.PrefixFor(s.Name)))
            .ToList();

        // Protection, read from the routes rather than kept twice: the routes are what Traefik
        // serves, so anything else is a second source of truth that can disagree with the traffic.
        var appRoutes = await db.Routes.Where(r => r.AppId == app.Id).ToListAsync(ct);
        ViewBag.ProtectionAuth = appRoutes.Any(r => r.BasicAuthEnabled && r.BasicAuthUsersEncrypted is not null);
        ViewBag.ProtectionIps = appRoutes.Select(r => r.IpAllowlist).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";
        ViewBag.HasRoutes = appRoutes.Count > 0;

        // Where this app could go next. An app installed from a ready-made template had no way to
        // move to a newer release at all: the version was pinned at creation and nothing offered
        // another, so "update n8n" meant deleting it and starting again.
        if (app.TemplateId is { } templateId)
        {
            var architecture = await db.Servers.Where(s => s.IsLocal)
                .Select(s => s.Architecture).FirstOrDefaultAsync(ct);

            var all = await db.AppTemplateVersions.AsNoTracking()
                .Where(v => v.AppTemplateId == templateId).ToListAsync(ct);

            var current = all.FirstOrDefault(v => v.Id == app.TemplateVersionId);
            ViewBag.CurrentVersion = current;

            // Only forward. Offering an older release as an "update" is how somebody downgrades a
            // database schema by accident.
            ViewBag.Repository = Harbora.Infrastructure.Templates.ImageReference
                .RepositoryOf(current is null ? app.PrebuiltImage : current.ImageRepository);

            ViewBag.UpdateVersions = Harbora.Infrastructure.Templates.VersionSelection
                .Offerable(all, architecture)
                .Where(v => current is null || string.CompareOrdinal(v.Version, current.Version) > 0)
                .Where(v => v.Id != app.TemplateVersionId)
                .ToList();
        }

        // Also for an app that came from an image rather than a template: naming a release tag is
        // useful whether or not Harbora curated the version list.
        ViewBag.Repository ??= Harbora.Infrastructure.Templates.ImageReference.RepositoryOf(app.PrebuiltImage);
        ViewBag.CurrentTag = Harbora.Infrastructure.Templates.ImageReference.TagOf(app.PrebuiltImage);

        if (app.Kind == ServiceKind.Cron)
            ViewBag.CronRuns = await db.CronRuns
                .Where(r => r.AppId == app.Id)
                .OrderByDescending(r => r.StartedAt)
                .Take(20)
                .ToListAsync(ct);

        // The Overview tab, wrapped for the shared shell: _Shell.cshtml is typed to AppTabViewModel,
        // so what reaches View() has to be an instance of it rather than the raw entity Details used
        // to receive directly.
        return View(new AppOverviewViewModel
        {
            Id = app.Id,
            Name = app.Name,
            Slug = app.Slug,
            Kind = app.Kind,
            Status = app.Status,
            CurrentTab = "overview",
            SourceType = app.SourceType,
            GitRepositoryFullName = app.GitRepository?.FullName,
            InstanceSizeKey = app.InstanceSizeKey,
            // Overview no longer loads the Volumes collection (that Include moved to the Volumes tab,
            // which is the whole point of giving it its own route), so the header's "is there a Data
            // button" question is answered the same way Usage answers it: an existence check.
            HasVolumes = await db.Volumes.AnyAsync(v => v.AppId == app.Id, ct),
            App = app
        });
    }

    /// <summary>
    /// Moves an app to a different resource plan.
    ///
    /// The ceiling belongs to the container, so it takes effect when the container is next created —
    /// this writes the intent and says so. Silently doing nothing visible, or restarting somebody's
    /// production app because a dropdown moved, are both worse than saying which it is.
    /// </summary>
    /// <summary>
    /// Put a password and/or an address restriction in front of every domain this app answers on.
    ///
    /// The routing engine has had basic-auth since the designer shipped; what was missing was a way
    /// to reach it without opening a route designer and understanding Traefik middlewares. A staging
    /// site nobody should index, a preview a client is reviewing, an internal tool — all of them
    /// wanted one switch, and all of them shipped to the open internet instead.
    ///
    /// Applied to the app's routes rather than to the app: a route is per-domain, and an app with
    /// three domains must not be protected on one of them.
    /// </summary>
    [HttpPost("/apps/{id:guid}/protection")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SetProtection(
        Guid id, bool basicAuthEnabled, string? user, string? password, string? ipAllowlist, CancellationToken ct)
    {
        var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var allowed = Harbora.Infrastructure.Proxy.AccessList.Parse(ipAllowlist, out var rejected);
        if (rejected.Count > 0)
        {
            // Named, because "invalid allowlist" leaves somebody hunting through fifteen addresses.
            TempData["Error"] = (IsFa ? "این ورودی‌ها آدرس یا رنج معتبر نیستند: " : "These are not valid addresses or ranges: ")
                                + string.Join(", ", rejected);
            return RedirectToAction(nameof(Details), new { id });
        }

        var routes = await db.Routes.Where(r => r.AppId == app.Id).ToListAsync(ct);
        if (routes.Count == 0)
        {
            TempData["Error"] = IsFa
                ? "این اپ هنوز دامنه‌ای ندارد که محافظت شود."
                : "This application has no domain to protect yet.";
            return RedirectToAction(nameof(Details), new { id });
        }

        foreach (var route in routes)
        {
            route.BasicAuthEnabled = basicAuthEnabled;

            // A blank password with auth already on means "leave the credentials alone" — the form
            // never renders the stored one back, so re-typing it to change an IP range would be the
            // only way to keep it.
            if (basicAuthEnabled && !string.IsNullOrWhiteSpace(password))
                route.BasicAuthUsersEncrypted = protector.Protect(
                    Harbora.Infrastructure.Proxy.Htpasswd.Line(
                        string.IsNullOrWhiteSpace(user) ? "admin" : user!.Trim(), password!));
            else if (!basicAuthEnabled)
                route.BasicAuthUsersEncrypted = null;

            route.IpAllowlist = Harbora.Infrastructure.Proxy.AccessList.Format(allowed);
        }

        await db.SaveChangesAsync(ct);

        var applied = await proxy.ApplyAllAsync(app.WorkspaceId, ct);

        await audit.LogAsync("app.protection_changed", "app",
            $"{app.Name}: auth={basicAuthEnabled}, ips={allowed.Count}", ClientIp, ct: ct);

        // The proxy's own verdict, not an assumption. A rolled-back apply means the rows changed
        // and the traffic did not — the one outcome nobody must be told is "saved".
        TempData[applied.Success ? "Message" : "Error"] = applied.Success
            ? (IsFa ? "محافظت اعمال شد." : "Protection applied.")
            : (IsFa ? "پیکربندی پروکسی اعمال نشد: " : "The proxy configuration was not applied: ") + applied.Error;

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Gives an application somewhere to keep files.
    ///
    /// Volumes could only arrive from a template, so an application built from an image or a
    /// repository had nowhere to put anything that survives a deploy — and the Data browser, which
    /// only appears when there is a volume, was unreachable for most of the platform. "There is no
    /// file access" was true, and this is why.
    /// </summary>
    [HttpPost("/apps/{id:guid}/volumes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AddVolume(Guid id, string mountPath, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var refusal = Harbora.Infrastructure.Storage.MountPath.Check(mountPath);
        if (refusal != Harbora.Infrastructure.Storage.MountPathRefusal.None)
        {
            TempData["Error"] = ExplainMount(refusal);
            return RedirectToAction(nameof(Volumes), new { id });
        }

        var normalised = Harbora.Infrastructure.Storage.MountPath.Normalise(mountPath)!;

        // Compared on the normalised form, so "/data" and "/data/" cannot both be added and then
        // collide at deploy time, where the message is about a duplicate bind rather than about
        // what somebody typed.
        if (app.Volumes.Any(v => v.MountPath == normalised))
        {
            TempData["Error"] = IsFa
                ? $"«{normalised}» از قبل هست."
                : $"{normalised} is already mounted.";
            return RedirectToAction(nameof(Volumes), new { id });
        }

        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);
        var quotaCheck = await quota.CanAddGovernedResourcesAsync(WorkspaceId,
            new GovernanceQuotaDelta(Volumes: 1), ct);
        if (!quotaCheck.Allowed)
        {
            TempData["Error"] = (IsFa ? quotaCheck.ReasonFa : null) ?? quotaCheck.Reason;
            return RedirectToAction(nameof(Volumes), new { id });
        }

        app.Volumes.Add(new Volume
        {
            Name = Harbora.Infrastructure.Storage.MountPath.VolumeNameFor(app.Slug, normalised),
            MountPath = normalised
        });
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);
        await audit.LogAsync("app.volume_added", "app", $"{app.Name}:{normalised}", ClientIp, ct: ct);

        // Says what it does not do. The volume is a row until the next deployment creates the
        // container that mounts it, and a person who uploads a file before then would be writing
        // into something the running container cannot see.
        TempData["Message"] = IsFa
            ? $"«{normalised}» اضافه شد. با استقرار بعدی به کانتینر وصل می‌شود."
            : $"{normalised} was added. It is attached to the container on the next deployment.";
        return RedirectToAction(nameof(Volumes), new { id });
    }

    /// <summary>
    /// Stops mounting a directory. The data is kept unless it is explicitly asked for.
    /// </summary>
    [HttpPost("/apps/{id:guid}/volumes/{volumeId:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> RemoveVolume(Guid id, Guid volumeId, bool deleteData, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var volume = app.Volumes.FirstOrDefault(v => v.Id == volumeId);
        if (volume is null) return NotFound();

        var name = volume.Name;
        var path = volume.MountPath;
        app.Volumes.Remove(volume);
        await db.SaveChangesAsync(ct);

        // Two different acts, and the second is not undoable. Forgetting the mount leaves the data
        // on the host where it can be mounted again; deleting it is gone.
        if (deleteData)
        {
            var docker = await engines.ResolveAsync(app.ServerId, ct);
            try { await docker.RemoveVolumeAsync(name, ct); }
            catch (Exception e) { logger.LogWarning(e, "Volume {Volume} was unmounted but not deleted.", name); }
        }

        await audit.LogAsync(
            deleteData ? "app.volume_deleted" : "app.volume_detached", "app",
            $"{app.Name}:{path}", ClientIp, ct: ct);

        TempData["Message"] = deleteData
            ? (IsFa ? $"«{path}» و داده‌هایش حذف شد." : $"{path} and its data were deleted.")
            : (IsFa
                ? $"«{path}» دیگر وصل نمی‌شود. داده‌ها روی سرور ماندند."
                : $"{path} is no longer mounted. The data is still on the server.");

        return RedirectToAction(nameof(Volumes), new { id });
    }

    private string ExplainMount(Harbora.Infrastructure.Storage.MountPathRefusal refusal) => (refusal, IsFa) switch
    {
        (Harbora.Infrastructure.Storage.MountPathRefusal.Missing, true) => "مسیر را بنویسید، مثلاً /data",
        (Harbora.Infrastructure.Storage.MountPathRefusal.Missing, false) => "Type a path, for example /data",
        (Harbora.Infrastructure.Storage.MountPathRefusal.NotAbsolute, true) =>
            "مسیر باید با / شروع شود؛ مسیر نسبی به پوشهٔ کاری ایمیج حساب می‌شود که معلوم نیست کجاست.",
        (Harbora.Infrastructure.Storage.MountPathRefusal.NotAbsolute, false) =>
            "The path must start with /. A relative one is resolved against the image's working directory, which is not visible from here.",
        (Harbora.Infrastructure.Storage.MountPathRefusal.Unsafe, true) => "این مسیر نویسه‌های غیرمجاز دارد.",
        (Harbora.Infrastructure.Storage.MountPathRefusal.Unsafe, false) => "That path contains something a path cannot contain.",
        (Harbora.Infrastructure.Storage.MountPathRefusal.TooLong, true) => "مسیر خیلی بلند است.",
        (Harbora.Infrastructure.Storage.MountPathRefusal.TooLong, false) => "That path is too long.",
        (_, true) =>
            "این پوشه مال خود ایمیج است. سوار کردن والیوم خالی روی آن، محتوای ایمیج را با هیچ جایگزین می‌کند و کانتینر بالا نمی‌آید.",
        (_, false) =>
            "That directory belongs to the image. Mounting an empty volume over it replaces the image's own files with nothing, and the container does not start."
    };

    [HttpPost("/apps/{id:guid}/size")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> Resize(Guid id, string? instanceSizeKey, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // Excluding this app from the count, or moving from small to small would be measured as
        // asking for a second app's worth of memory and refused.
        var check = await quota.CanAddAppAsync(WorkspaceId, instanceSizeKey, excludeAppId: app.Id, ct);
        if (!check.Allowed)
        {
            TempData["Error"] = check.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        var size = string.IsNullOrWhiteSpace(instanceSizeKey)
            ? null
            : await db.InstanceSizes.FirstOrDefaultAsync(s => s.Key == instanceSizeKey, ct);

        // A tier now comes with disk, so moving down to one smaller than what this app already
        // stores has to be refused here. Nothing is deleted to make it fit — shrinking a tier is
        // not consent to lose data, and a resize that silently could not hold what was there would
        // be discovered when a write failed.
        if (size is { DiskBytes: > 0 })
        {
            var stored = await AppDiskUsageAsync(app.Id, ct);
            if (Harbora.Infrastructure.Tenancy.InstanceDisk.Explain(size.DiskBytes, stored) is { } tooSmall)
            {
                TempData["Error"] = tooSmall;
                return RedirectToAction(nameof(Details), new { id });
            }
        }

        app.InstanceSizeKey = size?.Key;
        app.MemoryLimitBytes = size?.MemoryBytes ?? 0;
        app.CpuLimit = size?.CpuCores ?? 0;
        app.DiskLimitBytes = size?.DiskBytes ?? 0;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.resized", "app", $"{app.Name}={size?.Key ?? "unlimited"}", ClientIp, ct: ct);

        var fa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        TempData["Message"] = fa
            ? $"اندازه روی «{size?.Name ?? "بدون سقف"}» تنظیم شد. با استقرار بعدی اعمال می‌شود."
            : $"Size set to {size?.Name ?? "no limit"}. It applies on the next deployment.";

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Moves an app to another version of the template it came from.
    ///
    /// The version's pinned digest becomes the image and a deployment is queued, so the update goes
    /// through the same health gate and rollback as any other release — an update that bypassed them
    /// would be the one deploy on the platform with no way back.
    /// </summary>
    [HttpPost("/apps/{id:guid}/version")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> UpdateVersion(Guid id, Guid versionId, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsDeploy, ct)) return Forbid();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        if (app.TemplateId is not { } templateId)
        {
            TempData["Error"] = "This app was not installed from a template.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var version = await db.AppTemplateVersions.AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == versionId && v.AppTemplateId == templateId, ct);

        if (version is null)
        {
            TempData["Error"] = "That version does not belong to this app's template.";
            return RedirectToAction(nameof(Details), new { id });
        }

        // Checked again here, not only when the list was drawn: a version can be withdrawn between
        // somebody opening the page and pressing the button.
        var architecture = await db.Servers.Where(s => s.IsLocal)
            .Select(s => s.Architecture).FirstOrDefaultAsync(ct);

        if (Harbora.Infrastructure.Templates.VersionSelection.Refuse(version, architecture) is { } refusal)
        {
            TempData["Error"] = refusal.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        var image = Harbora.Infrastructure.Templates.VersionSelection.PinnedImage(version);
        if (image is null)
        {
            TempData["Error"] = "That version has no pinned image digest.";
            return RedirectToAction(nameof(Details), new { id });
        }

        app.PrebuiltImage = image;
        app.TemplateVersionId = version.Id;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.version_updated", "app", $"{app.Name}={version.Version}", ClientIp, ct: ct);

        Guid deploymentId;
        try
        {
            deploymentId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, app.GitRef), ct);
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        return RedirectToAction("Details", "Deployments", new { id = deploymentId });
    }

    /// <summary>
    /// Moves an app to a release tag the person names, from the repository it already pulls from.
    ///
    /// The tag is resolved to a digest before anything is stored. Deploying <c>repo:tag</c> as
    /// written would undo the whole point of pinning — the same "version" would install different
    /// software on different days — and a tag that does not exist would fail at pull time, after the
    /// page had already said the update was under way.
    /// </summary>
    [HttpPost("/apps/{id:guid}/tag")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> UpdateTag(
        Guid id, string? tag, [FromServices] IContainerRegistry registry, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsDeploy, ct)) return Forbid();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var fa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

        if (!Harbora.Infrastructure.Templates.ImageReference.IsUsableTag(tag))
        {
            TempData["Error"] = fa
                ? "این تگ معتبر نیست. فقط حروف، رقم، نقطه، خط تیره و زیرخط."
                : "That is not a usable tag. Letters, digits, dots, dashes and underscores only.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var repository = Harbora.Infrastructure.Templates.ImageReference.RepositoryOf(app.PrebuiltImage);
        if (app.TemplateVersionId is { } versionId)
            repository = await db.AppTemplateVersions.AsNoTracking()
                .Where(v => v.Id == versionId).Select(v => v.ImageRepository).FirstOrDefaultAsync(ct)
                ?? repository;

        if (string.IsNullOrWhiteSpace(repository))
        {
            TempData["Error"] = fa
                ? "این برنامه از یک ایمیج ساخته نشده، پس مخزنی برای گرفتن تگ ندارد."
                : "This app was not built from an image, so there is no repository to take a tag from.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var digest = await registry.ResolveDigestAsync(repository, tag!.Trim(), ct);
        if (digest is null)
        {
            // Refused rather than stored unpinned. "Could not check" and "does not exist" are both
            // reasons not to hand somebody a release nobody verified.
            TempData["Error"] = fa
                ? $"تگ «{tag}» در {repository} پیدا نشد یا رجیستری پاسخ نداد. چیزی تغییر نکرد."
                : $"Tag \"{tag}\" was not found in {repository}, or the registry did not answer. Nothing changed.";
            return RedirectToAction(nameof(Details), new { id });
        }

        app.PrebuiltImage = $"{repository}@{digest}";

        // No longer one of the curated versions. Saying so is more honest than leaving a link to a
        // version this app is not on any more.
        app.TemplateVersionId = null;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.tag_updated", "app", $"{app.Name}={repository}:{tag}", ClientIp, ct: ct);

        Guid deploymentId;
        try
        {
            deploymentId = await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, app.GitRef), ct);
        }
        catch (QuotaRefusedException ex)
        {
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        return RedirectToAction("Details", "Deployments", new { id = deploymentId });
    }

    /// <summary>The sizes this workspace's plan allows, with the current one preselected.</summary>
    private async Task<List<SelectListItem>> SizeChoicesAsync(string? current, CancellationToken ct)
    {
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId)
                .Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } planId
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == planId, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);

        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return (await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct))
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new SelectListItem(
                Harbora.Infrastructure.Tenancy.InstanceSizeLabel.For(
                    s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes,
                    s.RunningRatePerHourMinor, creationBilling.Currency), s.Key,
                string.Equals(s.Key, current, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Turns branch previews on or off. Turning them off leaves the existing ones alone — they are
    /// running services, and deleting somebody's work because a switch moved is not a decision a
    /// checkbox should make. They expire on their own.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> TogglePreviews(Guid id, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsDeploy, ct)) return NotFound();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null || app.GitRepositoryId is null) return NotFound();

        app.PreviewsEnabled = !app.PreviewsEnabled;
        await db.SaveChangesAsync(ct);

        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDeploy)]
    public async Task<IActionResult> Deploy(Guid id, string? gitRef, CancellationToken ct)
    {
        // The capability this action is authorised with, asked again against this particular
        // project: a member scoped away from production must not be able to deploy it.
        if (!await MayAsync(id, Capabilities.AppsDeploy, ct)) return NotFound();

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
            // Rollback is only ever reached from the deployment list, which now lives on its own
            // tab rather than on Overview — same reasoning as AddVolume/RemoveVolume redirecting to
            // Volumes: land back where the history (and this error) is actually shown.
            return RedirectToAction(nameof(Deployments), new { id });
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
            return RedirectToAction(nameof(Deployments), new { id });
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
        try { await ops.RestartAsync(id, ct); }
        catch (QuotaRefusedException ex)
        {
            // The billing gate refusing a workspace with no balance, in the same shape this
            // controller already uses for a rollback that cannot be coalesced: the reason where a
            // quota refusal appears, rather than a 500 for a decision the platform made on purpose.
            // Shown in the reader's own language — this is a customer already locked out of their
            // app, the one place on this panel that must not read English-only.
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
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
        try { await ops.StartAsync(id, ct); }
        catch (QuotaRefusedException ex)
        {
            // Same shape as Restart, just above: shown in the reader's own language rather than the
            // provider's, because this is the message a customer sees on their way to fixing it.
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        TempData["Message"] = "Started.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDelete)]
    public async Task<IActionResult> Delete(Guid id, bool removeVolumes, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsDelete, ct)) return NotFound();
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
        if (!await access.CanSeeAppAsync(id, ct)) return NotFound();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        return View(app);
    }

    /// <summary>
    /// The tail, filtered before it is sent. Done here rather than in the browser because the line
    /// that explains an outage is usually not the one on screen.
    /// </summary>
    [HttpGet("/apps/{id:guid}/logs/data")]
    public async Task<IActionResult> LogsData(
        Guid id, int tail = 200, string? search = null, bool problems = false, CancellationToken ct = default)
    {
        if (!await access.CanSeeAppAsync(id, ct)) return NotFound();

        var text = await ops.GetLogsAsync(id, tail, ct);
        if (string.IsNullOrWhiteSpace(search) && !problems) return Content(text, "text/plain");

        var lines = Harbora.Infrastructure.Deployments.LogFilter.Apply(text, search, problems);

        // Said plainly, because an empty pane looks the same as a broken one.
        return Content(lines.Count == 0
            ? "No lines match that filter."
            : string.Join('\n', lines), "text/plain");
    }

    /// <summary>Downloads the tail as a file — the second thing anyone does is send it to someone.</summary>
    [HttpGet("/apps/{id:guid}/logs/download")]
    public async Task<IActionResult> LogsDownload(
        Guid id, int tail = 2000, string? search = null, bool problems = false, CancellationToken ct = default)
    {
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var text = await ops.GetLogsAsync(id, tail, ct);
        var lines = Harbora.Infrastructure.Deployments.LogFilter.Apply(text, search, problems);

        return File(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', lines)), "text/plain",
            Harbora.Infrastructure.Deployments.LogFilter.FileName(app.Slug, DateTimeOffset.UtcNow));
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
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();
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
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();
        var app = await db.Apps.Include(a => a.Domains).FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();
        host = (host ?? "").Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(host)) { TempData["Error"] = "Host is required."; return RedirectToAction(nameof(Details), new { id }); }
        if (await db.Domains.AnyAsync(d => d.Host == host, ct)) { TempData["Error"] = "This domain is already in use."; return RedirectToAction(nameof(Details), new { id }); }

        // A uniqueness check is not an ownership check. The platform's own host names are not in
        // db.Domains — nothing ever inserted them — so until this guard they were free to claim,
        // and the node channel's host is the one where taking it costs more than it looks: see
        // ReservedHosts for why a second router on that SNI name turns mTLS off instead of adding
        // a route.
        if (IsReservedHost(host)) { TempData["Error"] = ReservedHostRefusal(host); return RedirectToAction(nameof(Details), new { id }); }

        await using var quotaReservation = await quota.AcquireCreationLockAsync(WorkspaceId, ct);
        var quotaCheck = await quota.CanAddGovernedResourcesAsync(WorkspaceId,
            new GovernanceQuotaDelta(Domains: 1), ct);
        if (!quotaCheck.Allowed)
        {
            TempData["Error"] = (IsFa ? quotaCheck.ReasonFa : null) ?? quotaCheck.Reason;
            return RedirectToAction(nameof(Details), new { id });
        }

        app.Domains.Add(new DomainName { Host = host, SslEnabled = ssl, ForceHttps = ssl, IsPrimary = app.Domains.Count == 0 });
        await db.SaveChangesAsync(ct);
        await quotaReservation.CommitAsync(ct);
        TempData["Message"] = "Domain added. Redeploy to route it.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/domains/{domainId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteDomain(Guid id, Guid domainId, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();
        var domain = await db.Domains.FirstOrDefaultAsync(d => d.Id == domainId && d.AppId == id, ct);
        if (domain is not null)
        {
            db.Domains.Remove(domain);
            var routes = await db.Routes.Where(r => r.AppId == id && r.Host == domain.Host).ToListAsync(ct);
            db.Routes.RemoveRange(routes);

            // Deleting the primary must not leave a live custom domain marked nobody's primary:
            // Overview, the applications list and the backfill's "addressless" filter all key off
            // IsPrimary, and an app with a domain that is nobody's primary reads — wrongly — as
            // having none at all.
            var survivorPrimaryAlready = await db.Domains
                .AnyAsync(d => d.AppId == id && d.Id != domainId && d.IsPrimary, ct);
            if (!survivorPrimaryAlready)
            {
                var survivor = await db.Domains.Where(d => d.AppId == id && d.Id != domainId)
                    .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefaultAsync(ct);
                if (survivor is not null) survivor.IsPrimary = true;
            }

            await db.SaveChangesAsync(ct);
        }

        TempData["Message"] = "Domain removed.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Whether the caller may act on this app. Ownership is not the whole question any more: a
    /// member scoped to projects owns the workspace's apps in the sense that their tenant does, and
    /// still must not touch a project nobody put them on.
    /// </summary>
    private Task<bool> OwnsAsync(Guid appId, CancellationToken ct) =>
        access.CanTouchAppAsync(appId, Capabilities.AppsOperate, ct);

    /// <summary>The same question for an action that is not day-2 operations.</summary>
    private Task<bool> MayAsync(Guid appId, string capability, CancellationToken ct) =>
        access.CanTouchAppAsync(appId, capability, ct);

    private async Task PopulateTemplates(CancellationToken ct)
    {
        // Every project's environments, so a service can be created where it belongs rather than
        // always landing in the default one.
        var environmentQuery = db.Environments.Where(e => e.WorkspaceId == WorkspaceId);
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
            environmentQuery = environmentQuery.Where(e => visible.Contains(e.ProjectId));

        ViewBag.Environments = await environmentQuery
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
        var quickStarts = await LoadTemplateCardsAsync(ct);
        // The operator's chosen order, the same one the dashboard uses. Taking the first six by
        // category then name meant the admin could choose which ready apps come first and this page
        // would still show a different six.
        ViewBag.QuickStarts = (await FeaturedCardsAsync(6, ct))
            .Concat(quickStarts.Where(t => t.IsManagedService).Take(4)).ToList();

        ViewBag.Servers = await db.Servers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name)
            .Select(s => new SelectListItem(s.IsLocal ? s.Name + " (local)" : s.Name, s.Id.ToString())).ToListAsync(ct);

        // Offer only the instance sizes this workspace's plan allows.
        var plan = await db.Workspaces.Where(w => w.Id == WorkspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct) is { } pid
            ? await db.Plans.FirstOrDefaultAsync(p => p.Id == pid, ct)
            : await db.Plans.FirstOrDefaultAsync(p => p.IsDefault, ct);
        var allowed = plan is null || string.IsNullOrWhiteSpace(plan.AllowedSizeKeys)
            ? null
            : plan.AllowedSizeKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The platform default, preselected. Without one, every create form started at "no ceiling",
        // which is the option nobody picks on purpose and the one that costs the most.
        var defaultSize = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.DefaultInstanceSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        ViewBag.DefaultSize = defaultSize;
        ViewBag.PreviewsDefault = string.Equals(
            await db.Settings.IgnoreQueryFilters()
                .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.PreviewsDefault)
                .Select(s => s.Value).FirstOrDefaultAsync(ct),
            "true", StringComparison.OrdinalIgnoreCase);

        var sizes = await db.InstanceSizes.Where(s => s.IsEnabled).OrderBy(s => s.SortOrder).ToListAsync(ct);
        ViewBag.Sizes = sizes
            .Where(s => allowed is null || allowed.Contains(s.Key))
            .Select(s => new SelectListItem(
                Harbora.Infrastructure.Tenancy.InstanceSizeLabel.For(
                    s.Name, s.CpuCores, s.MemoryBytes, s.DiskBytes,
                    s.RunningRatePerHourMinor, creationBilling.Currency), s.Key,
                string.Equals(s.Key, defaultSize, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// The ready apps to put in front of somebody, in the order an operator chose on
    /// /admin/settings. Falls back to the catalogue's own order when nobody has chosen.
    /// </summary>
    /// <summary>
    /// What this app's own volumes are measured to hold, and how much was never measured.
    ///
    /// Both halves, because they are answers to different questions: a workspace with nine
    /// unmeasured volumes has an unknown total, not a small one. <see cref="InstanceDisk"/> decides
    /// what to do about that; this only reports it.
    /// </summary>
    private async Task<Harbora.Infrastructure.Tenancy.DiskUsage> AppDiskUsageAsync(Guid appId, CancellationToken ct)
    {
        var volumes = await db.Volumes.AsNoTracking()
            .Where(v => v.AppId == appId)
            .Select(v => v.StorageBytes)
            .ToListAsync(ct);

        return new Harbora.Infrastructure.Tenancy.DiskUsage(
            volumes.Where(b => b is not null).Sum(b => b!.Value),
            volumes.Count(b => b is null));
    }

    private async Task<List<TemplateCatalogItemViewModel>> FeaturedCardsAsync(int count, CancellationToken ct)
    {
        var cards = (await LoadTemplateCardsAsync(ct)).Where(c => !c.IsManagedService).ToList();

        var featured = Harbora.Infrastructure.Templates.FeaturedTemplates.Resolve(
            Harbora.Infrastructure.Templates.FeaturedTemplates.Parse(
                await db.Settings.IgnoreQueryFilters()
                    .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.FeaturedTemplates)
                    .Select(s => s.Value).FirstOrDefaultAsync(ct)),
            cards.Select(c => c.Template.Key).ToList(),
            count);

        return featured.Select(key => cards.First(c => c.Template.Key == key)).ToList();
    }

    private async Task<IReadOnlyList<TemplateCatalogItemViewModel>> LoadTemplateCardsAsync(CancellationToken ct)
    {
        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        var templates = await db.AppTemplates.AsNoTracking()
            .Where(t => t.IsEnabled)
            .OrderBy(t => t.Category).ThenBy(t => t.Name)
            .ToListAsync(ct);

        return templates
            .Where(t => Harbora.Infrastructure.Templates.TemplateCatalog.IsVisibleTo(t, WorkspaceId))
            .Select(t => TemplateCatalogItemViewModel.Create(t, isFa))
            .Where(t => t is not null)
            .Cast<TemplateCatalogItemViewModel>()
            .OrderByDescending(t => t.Manifest.Featured)
            .ThenBy(t => t.Name)
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
