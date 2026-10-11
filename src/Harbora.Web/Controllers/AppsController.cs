using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Jobs;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Networking;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Storage;
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
    ICurrentUser currentUser,
    Harbora.Infrastructure.Billing.ResourceCreationBilling creationBilling,
    Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Deployments.HarboraRuntimeOptions> runtimeOptions,
    // F5 (2026-08-21 functions-and-services plan): the same options DeploymentPipeline reads to
    // build S3_ENDPOINT, so this page's effective-env preview can never disagree with what a
    // deploy would actually inject.
    Microsoft.Extensions.Options.IOptions<Harbora.Infrastructure.Storage.ObjectStorageOptions> storageOptions,
    AppAddressAssigner addresses,
    Harbora.Modules.Backup.Infrastructure.BackupSnapshotService backupSnapshots,
    Harbora.Infrastructure.Monitoring.LifecycleHistory lifecycle,
    // C2 (2026-08-22 config-delivery plan): resolves a config-override rule's value and, for the
    // pre-deploy validation action, reads the deployed app's own file through the same container
    // seam DeploymentPipeline applies overrides through.
    IConfigOverrideResolver configOverrides,
    // Round six, phase 0a: creating an app, and changing its variables, domains and volumes, live in
    // these services now so the AI agent's tools run the same code these actions do. What stays in
    // the actions is the HTTP shape: the form, the status code, the banner.
    IAppProvisioningService provisioning,
    IAppConfigurationService configuration) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    /// <summary>Where the app services are acting: this request's workspace and address. The person is
    /// the signed-in user, whom the services read themselves.</summary>
    private AppActor Actor => new(WorkspaceId, ClientIp);

    /// <summary>A refusal in the language this page is being shown in.</summary>
    private string Say(ServiceRefusal refusal) => IsFa ? refusal.MessageFa : refusal.Message;

    private string Say(IReadOnlyList<ServiceRefusal> refusals) => string.Join(" ", refusals.Select(Say));

    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var query = db.Apps.Where(a => a.WorkspaceId == WorkspaceId);

        // A list that shows what the buttons refuse is worse than a shorter list. Null means "every
        // project", which is the common case and must not be confused with an empty list.
        //
        // 5.1: a member granted one named app must still see it here even when nothing makes its
        // project visible — VisibleProjectIdsAsync deliberately excludes an app-scoped grant from
        // opening the whole project (see its own remarks), so this unions the two id sets rather than
        // filtering by project alone, which would either hide their one app or leak every sibling
        // app in the same project depending on which way the gap was closed.
        if (await access.VisibleProjectIdsAsync(ct) is { } visible)
        {
            var grantedAppIds = await access.GrantedAppIdsAsync(ct);
            query = query.Where(a => visible.Contains(a.Environment!.ProjectId) || grantedAppIds.Contains(a.Id));
        }

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
                    a.WorkspaceId, a.Slug, activeNumbers[a.ActiveDeploymentId!.Value]));
        var resourceRefs = metricRefs.Values.Distinct().ToList();
        var metrics = resourceRefs.Count == 0
            ? new List<MonitoringMetric>()
            : await db.MonitoringMetrics.AsNoTracking()
                .Where(m => m.ResourceRef != null && resourceRefs.Contains(m.ResourceRef)
                            && (m.Name == "cpu.percent" || m.Name == "mem.used"))
                .OrderByDescending(m => m.Timestamp).Take(2_000).ToListAsync(ct);

        // A second, time-windowed query for the HEALTH · 1H sparkline — the query above only keeps
        // each app's single latest reading, which answers "what is it now" but has nothing to draw a
        // trend from.
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var cpuHistory = resourceRefs.Count == 0
            ? new List<MonitoringMetric>()
            : await db.MonitoringMetrics.AsNoTracking()
                .Where(m => m.ResourceRef != null && resourceRefs.Contains(m.ResourceRef)
                            && m.Name == "cpu.percent" && m.Timestamp >= since)
                .OrderBy(m => m.Timestamp)
                .Take(20_000)
                .ToListAsync(ct);
        var cpuHistoryByRef = cpuHistory
            .GroupBy(m => m.ResourceRef!)
            .ToDictionary(g => g.Key, g => g.Select(m => (m.Timestamp, m.Value)));

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
                var cpuSeries = metricRef is not null && cpuHistoryByRef.TryGetValue(metricRef, out var points)
                    ? Harbora.Infrastructure.Monitoring.MetricBucketing.Bucket(points, since, DateTimeOffset.UtcNow, 10)
                    : [];
                return new ApplicationRowViewModel(
                    a.Id, a.Name, a.Slug, a.SourceType, a.Kind, a.Status,
                    a.Environment?.Project?.Name ?? "—", a.Environment?.Name ?? "—",
                    a.Domains.FirstOrDefault(d => d.IsPrimary)?.Host,
                    a.InstanceSizeKey, deployment?.Status, deployment?.Number,
                    deployment?.FinishedAt ?? deployment?.CreatedAt,
                    deployment?.CommitSha is { Length: > 0 } sha ? sha[..Math.Min(7, sha.Length)] : null,
                    operable.Contains(a.Id), cpu, memory is null ? null : (long?)memory.Value,
                    a.MemoryLimitBytes,
                    deployment?.StartedAt is { } started && deployment?.FinishedAt is { } finished
                        ? finished - started : null,
                    cpuSeries);
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
        var request = ToCreateRequest(model);

        // Model validation has already refused the form ([Required] on the name, a number that does
        // not parse). Nothing may be created then — but the person still sees every other problem at
        // once, as before, so ask the service for its reasons without letting it do anything.
        if (!ModelState.IsValid)
        {
            AddRefusals(await provisioning.ValidateCreateAsync(request, ct));
            await PopulateTemplates(ct);
            return View(model);
        }

        var outcome = await provisioning.CreateAsync(request, ct);
        if (!outcome.Ok)
        {
            AddRefusals(outcome.Refusals);
            await PopulateTemplates(ct);
            return View(model);
        }

        var created = outcome.Value!;
        if (created.AddressNotice is { } notice)
            TempData["Message"] = IsFa ? notice.MessageFa : notice.Message;

        // "Give it a repo and it just works": the first deployment's live logs.
        if (created.DeploymentId is { } deploymentId)
            return RedirectToAction("Details", "Deployments", new { id = deploymentId });

        // The application and its first-hour debit are committed either way; only its first deploy did
        // not start, and the page it lands on says why.
        if (created.DeployRefusal is { } refused)
        {
            TempData["Error"] = Say(refused);
            return RedirectToAction(nameof(Details), new { id = created.AppId });
        }

        if (created.TemplateAdvice is not null) TempData["Message"] = created.TemplateAdvice;
        return RedirectToAction(nameof(Details), new { id = created.AppId });
    }

    private AppCreateRequest ToCreateRequest(CreateAppViewModel model) => new()
    {
        WorkspaceId = WorkspaceId,
        UserId = currentUser.UserId ?? Guid.Empty,
        ClientIp = ClientIp,
        Name = model.Name,
        Slug = model.Slug,
        SourceType = model.SourceType,
        CloneUrl = model.CloneUrl,
        GitRef = model.GitRef,
        GitToken = model.GitToken,
        DockerfilePath = model.DockerfilePath,
        ComposeFilePath = model.ComposeFilePath,
        RootDirectory = model.RootDirectory,
        PrebuiltImage = model.PrebuiltImage,
        ContainerPort = model.ContainerPort,
        Domain = model.Domain,
        TemplateId = model.TemplateId,
        EnvironmentId = model.EnvironmentId,
        Kind = model.Kind,
        ReleaseCommand = model.ReleaseCommand,
        CronExpression = model.CronExpression,
        PreviewsEnabled = model.PreviewsEnabled,
        Command = model.Command,
        ServerId = model.ServerId,
        InstanceSizeKey = model.InstanceSizeKey,
        DeployNow = model.DeployNow,
        Trigger = DeploymentTrigger.Manual
    };

    /// <summary>
    /// Puts the service's refusals on the form, each against the field it names (the request's
    /// property names are the view model's). A field model validation has already flagged keeps its own
    /// message: the service repeats "a name is required" for callers that have no form, and the form
    /// showed it once.
    /// </summary>
    private void AddRefusals(IReadOnlyList<ServiceRefusal> refusals)
    {
        var flagged = ModelState.Where(entry => entry.Value is { Errors.Count: > 0 })
            .Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var refusal in refusals)
        {
            var field = refusal.Field ?? string.Empty;
            if (field.Length > 0 && flagged.Contains(field)) continue;
            ModelState.AddModelError(field, Say(refusal));
        }
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
            // Sub-project 9 (2026-08-20 platform-options plan): the attached groups' own current
            // entries, so this page can render the exact same merge BuildEnv assembles for a container.
            .Include(a => a.ConfigGroups).ThenInclude(cg => cg.ConfigGroup!).ThenInclude(g => g.Entries)
            // F5 (2026-08-21 functions-and-services plan): the same shape, one attachment kind later.
            .Include(a => a.StorageBuckets).ThenInclude(sb => sb.StorageBucket)
            // F6 (same plan): the same shape again, one attachment kind later still.
            .Include(a => a.EmailProviders).ThenInclude(ep => ep.EmailProvider)
            // C1 (2026-08-22 config-delivery plan): the same shape again — also read by
            // ServiceUsageService.ConnectionsFor below, for the "Databases" panel's Attached flag.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService)
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.Database)
            // 3.2 (round-2 market-gaps plan): a running read replica rides along with its primary's
            // own attachment — see AttachedReplicaEnv's own doc, and DeploymentPipeline.cs's matching
            // Include for why this is not a second, independent attachment.
            .Include(a => a.ManagedServices).ThenInclude(ms => ms.ManagedService!).ThenInclude(m => m.Replicas)
            // 1.8 (2026-09 market-gaps round two): the same shape again — also read below to build
            // the attach-panel and the effective-env provenance for a SENTRY_DSN row.
            .Include(a => a.ErrorTrackingProviders).ThenInclude(et => et.ErrorTrackingProvider)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // The effective environment, with provenance — the same assembly+merge DeploymentPipeline.BuildEnv
        // and the CLI's `harbora env pull` (ApiV1Controller.Env) call, so this page can never show a
        // value a deploy would not actually inject. Ciphertext stays ciphertext: this page always
        // masks a secret entry regardless of its value (see the env-row partial below), so nothing
        // here ever needs to decrypt one.
        ViewBag.EffectiveEnv = Harbora.Infrastructure.Apps.EffectiveEnvironmentBuilder.Compute(
            app, protector, storageOptions.Value.CustomerEndpoint);

        ViewBag.AttachedStorageBuckets = app.StorageBuckets
            .OrderBy(sb => sb.AttachOrder)
            .Select(sb => new AppStorageBucketRow(
                sb.StorageBucketId, sb.StorageBucket?.Name ?? "", sb.AttachOrder, sb.HasUnpublishedChanges))
            .ToList();

        ViewBag.AttachedEmailProviders = app.EmailProviders
            .OrderBy(ep => ep.AttachOrder)
            .Select(ep => new AppEmailProviderRow(
                ep.EmailProviderId, ep.EmailProvider?.Name ?? "", ep.AttachOrder, ep.HasUnpublishedChanges))
            .ToList();

        ViewBag.AttachedErrorTrackingProviders = app.ErrorTrackingProviders
            .OrderBy(et => et.AttachOrder)
            .Select(et => new AppErrorTrackingProviderRow(
                et.ErrorTrackingProviderId, et.ErrorTrackingProvider?.Name ?? "", et.AttachOrder, et.HasUnpublishedChanges))
            .ToList();

        ViewBag.AttachedConfigGroups = app.ConfigGroups
            .OrderBy(cg => cg.AttachOrder)
            .Select(cg => new AppConfigGroupRow(
                cg.ConfigGroupId, cg.ConfigGroup?.Name ?? "", cg.AttachOrder,
                cg.HasUnpublishedChanges, cg.ConfigGroup?.Entries.Count ?? 0))
            .ToList();

        var attachedIds = app.ConfigGroups.Select(cg => cg.ConfigGroupId).ToHashSet();
        ViewBag.AvailableConfigGroups = await db.ConfigGroups
            .Where(g => g.WorkspaceId == WorkspaceId && !attachedIds.Contains(g.Id))
            .OrderBy(g => g.Name)
            .ToListAsync(ct);

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

        // The resize control is the SizePicker view component now, which resolves the tiers this
        // workspace may move between itself — pinned to the host the app already runs on, because a
        // resize does not move a workload. Nothing reads a size list off ViewBag here any more.

        // The databases this application is wired to, and the ones it could be.
        //
        // The application page said nothing about databases at all: the only way to attach one was
        // to know to open each database in turn and attach the app from there, and nothing here
        // listed what it was already using. So "one app, several databases" was supported by
        // everything except the screen somebody would try it on.
        var siblings = await db.ManagedServices.AsNoTracking()
            .Where(s => s.WorkspaceId == WorkspaceId && s.EnvironmentId == app.EnvironmentId)
            .OrderBy(s => s.Name).ToListAsync(ct);

        var wiredTo = serviceUsage
            .ConnectionsFor([app], siblings.Select(s => s.ContainerName))
            .TryGetValue(app.Id, out var hosts) ? hosts : [];

        // The databases this application cannot reach, and why.
        //
        // An environment is a private network, so a database in another one cannot be attached —
        // the hostname would not resolve. That was explained and left there: the person could see
        // the refusal and had nowhere to go with it. The move already existed, on a page in
        // Advanced mode that most people never open.
        ViewBag.Elsewhere = await db.ManagedServices.AsNoTracking()
            .Include(s => s.Environment).ThenInclude(e => e!.Project)
            .Where(s => s.WorkspaceId == WorkspaceId && s.EnvironmentId != app.EnvironmentId)
            .OrderBy(s => s.Name)
            .Select(s => new AppDatabaseElsewhereViewModel(
                s.Id, s.Name, s.Type,
                s.Environment!.Project!.Name + " · " + s.Environment.Name,
                s.EnvironmentId))
            .ToListAsync(ct);

        // C1 (2026-08-22 config-delivery plan): an explicit AppManagedService join also counts as
        // attached (ServiceUsageService.Uses checks it too, but wiredTo above was built from the
        // env-var heuristic alone before that join existed) — and where one exists, its own Alias is
        // the real prefix this database writes under, not just AttachKeys' guess from the service's
        // name.
        //
        // A lookup, not a dictionary: D1 (2026-08-25 shared-databases plan) lets one app attach to
        // two different logical databases on the same instance, which is two AppManagedService rows
        // sharing one ManagedServiceId. A dictionary keyed on that threw "an item with the same key
        // has already been added" the moment a customer actually used the capability D1 shipped —
        // this page has 500'd on that ever since D1 landed with no UI to keep it from happening.
        var explicitByService = app.ManagedServices
            .Where(ms => ms.ManagedService is not null)
            .ToLookup(ms => ms.ManagedServiceId);

        // D3 (2026-08-25 shared-databases plan): the logical databases inside each sibling instance,
        // so an instance holding more than its own default can offer a per-database picker here too
        // — the app-side half of "attach to a logical database, from either side". Instances with
        // one logical database or none at all (still the common case) render exactly as before D1.
        var siblingIds = siblings.Select(s => s.Id).ToList();
        var siblingDatabases = await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => siblingIds.Contains(d.ManagedServiceId))
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .ToListAsync(ct);
        var databasesByService = siblingDatabases.ToLookup(d => d.ManagedServiceId);

        ViewBag.Databases = siblings
            .Select(s =>
            {
                var explicitLinks = explicitByService[s.Id].ToList();
                var wired = wiredTo.Contains(s.ContainerName);
                var firstAlias = explicitLinks.FirstOrDefault()?.Alias;

                var logicalRows = databasesByService[s.Id]
                    .Select(d => new AppLogicalDatabaseLinkViewModel(
                        d.Id, d.Name, d.IsDefault,
                        Attached: explicitLinks.Any(ms => ms.ManagedServiceDatabaseId == d.Id)
                                  || (d.IsDefault && (wired || explicitLinks.Any(ms => ms.ManagedServiceDatabaseId == null)))))
                    .ToList();

                return new AppDatabaseLinkViewModel(
                    s.Id, s.Name, s.Type, s.ContainerName,
                    Attached: wired || explicitLinks.Count > 0,
                    Prefix: firstAlias is not null
                        ? $"{firstAlias}_"
                        : Harbora.Infrastructure.Services.AttachKeys.PrefixFor(s.Name),
                    LogicalDatabases: logicalRows);
            })
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

        // ---- specifics: size, placement and the version actually live (B3 Task 1) ----
        //
        // A missing InstanceSize is not "zero cores": the key can point at a row that was since
        // removed, and the panel then does not know this app's limits — that has to render as
        // unknown, so the lookup is left null rather than defaulted.
        var instanceSize = app.InstanceSizeKey is null
            ? null
            : await db.InstanceSizes.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == app.InstanceSizeKey, ct);

        var server = await db.Servers.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == app.ServerId, ct);

        // Same rule the Usage tab reads the serving container by: the deployment currently active,
        // falling back to the most recent one that succeeded — Deploy only ever sets
        // ActiveDeploymentId alongside DeploymentStatus.Succeeded, so the fallback is never a weaker
        // answer than the primary lookup, only a cheaper path to the same one.
        var latestDeployment = app.ActiveDeploymentId is { } activeDeploymentId
            ? await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == activeDeploymentId, ct)
            : null;
        latestDeployment ??= await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.Status == DeploymentStatus.Succeeded)
            .OrderByDescending(d => d.Number)
            .FirstOrDefaultAsync(ct);

        var containerName = latestDeployment is null
            ? Harbora.Infrastructure.Deployments.DeploymentPlanning.LegacyContainerName(app.Slug)
            : Harbora.Infrastructure.Deployments.DeploymentPlanning.ContainerName(app.WorkspaceId, app.Slug, latestDeployment.Number);

        // ---- specifics: how the container is actually doing right now (B3 Task 3) ----
        //
        // An enrichment, not the page: TryInspectAsync below swallows a throw, a timeout, and a
        // remote node's "no inspect verb yet" the same way, so Docker being slow or busy never turns
        // this into a 500 for a page that otherwise has everything it needs.
        var liveContainer = await TryInspectAsync(app.ServerId, containerName, ct);

        // "If 2 of 3 are up, the panel must say that rather than showing green" — a single
        // TryInspectAsync of replica 1 cannot answer that, so this asks for all of them at once.
        // Only for an app actually running more than one: a one-replica app's status is exactly what
        // liveContainer already reports, so a second Docker round trip would buy nothing.
        var desiredReplicas = app.DesiredReplicas ?? 1;
        var runningReplicas = desiredReplicas > 1 && latestDeployment is not null
            ? await TryRunningReplicaCountAsync(
                app.ServerId, app.WorkspaceId, app.Slug, latestDeployment.Number, desiredReplicas, ct)
            : null;

        // ---- uptime percent + restart history (M3) ----
        //
        // Read from the app.up/app.restarts series LifecycleHistory maintains — a real window, not a
        // live-only number. An app with nothing collected against this container name (never
        // deployed, or between deployments) reports unknown for both, never a fabricated 100%/0.
        var lifecycleSince = DateTimeOffset.UtcNow.AddDays(-30);
        var uptimePercent30d = await lifecycle.UptimePercentAsync(app.ServerId, containerName, lifecycleSince, DateTimeOffset.UtcNow, ct);
        var restartCount30d = await lifecycle.RestartCountAsync(app.ServerId, containerName, lifecycleSince, DateTimeOffset.UtcNow, ct);

        // ---- what "Back up now" would capture (sub-project E, Task 2) ----
        //
        // Loaded here, not assumed: the card names these volumes rather than just knowing there are
        // some, so the query that used to be a bare AnyAsync now keeps the rows.
        var backupVolumes = await db.Volumes.AsNoTracking()
            .Where(v => v.AppId == app.Id)
            .OrderBy(v => v.MountPath)
            .ToListAsync(ct);

        var hasBackupRepository = await db.BackupRepositories.AsNoTracking()
            .AnyAsync(r => r.WorkspaceId == WorkspaceId && r.IsEnabled, ct);

        // ---- outside-in uptime check (2.1, 2026-09 market-gaps round two) ----
        var uptimeCheck = await db.UptimeChecks.AsNoTracking()
            .FirstOrDefaultAsync(c => c.AppId == app.Id && c.WorkspaceId == WorkspaceId, ct);
        var recentUptimeResults = await db.UptimeCheckResults.AsNoTracking()
            .Where(r => r.AppId == app.Id && r.WorkspaceId == WorkspaceId)
            .OrderByDescending(r => r.CheckedAt)
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
            // Overview no longer loads the Volumes collection as an Include (that moved to the
            // Volumes tab, which is the whole point of giving it its own route) — but the instant
            // backup card below needs the rows anyway, so the header's "is there a Data button"
            // question is answered from the same list rather than a second existence query.
            HasVolumes = backupVolumes.Count > 0,
            App = app,
            InstanceSize = instanceSize,
            Replicas = desiredReplicas,
            RunningReplicas = runningReplicas,
            ContainerPort = app.ContainerPort,
            Server = server,
            ContainerName = containerName,
            LatestDeployment = latestDeployment,
            LiveContainer = liveContainer,
            UptimePercent30d = uptimePercent30d,
            RestartCount30d = restartCount30d,
            UptimeCheck = uptimeCheck,
            RecentUptimeResults = recentUptimeResults,
            BackupVolumes = backupVolumes,
            HasBackupRepository = hasBackupRepository
        });
    }

    /// <summary>
    /// Asks the app's engine what its container is doing right now, tolerating everything short of
    /// the caller's own cancellation.
    ///
    /// <para>
    /// The specifics card is an enrichment of the app page, not the page itself: an engine that
    /// throws, hangs, or (a remote node, today, always) simply has no inspect command to answer with
    /// must not turn "how is this app doing" into a 500 for "can I see this app at all". Every one of
    /// those outcomes collapses to null here, which the view already knows how to render as unknown.
    /// </para>
    /// </summary>
    private async Task<ContainerDetail?> TryInspectAsync(Guid serverId, string containerName, CancellationToken ct)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(5));
            var docker = await engines.ResolveAsync(serverId, bounded.Token);
            return await docker.InspectAsync(containerName, bounded.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our own bound fired, not the request being aborted — the slow engine this method
            // exists to survive.
            logger.LogWarning(
                "Inspecting container {Container} for app specifics timed out.", containerName);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not inspect container {Container} for app specifics.", containerName);
            return null;
        }
    }

    /// <summary>
    /// How many of a replicated app's containers are reporting "running" right now — the multi-container
    /// counterpart of <see cref="TryInspectAsync"/>, and just as tolerant of an engine that cannot
    /// answer: a throw, a timeout, or a remote node with no inspect verb all collapse to null, never
    /// to zero, which the view already knows to render as unknown rather than as every replica down.
    /// </summary>
    private async Task<int?> TryRunningReplicaCountAsync(
        Guid serverId, Guid workspaceId, string slug, int deploymentNumber, int desiredReplicas,
        CancellationToken ct)
    {
        try
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bounded.CancelAfter(TimeSpan.FromSeconds(5));
            var docker = await engines.ResolveAsync(serverId, bounded.Token);

            var names = Harbora.Infrastructure.Deployments.DeploymentPlanning
                .ReplicaContainerNames(workspaceId, slug, deploymentNumber, desiredReplicas)
                .ToHashSet(StringComparer.Ordinal);

            var containers = await docker.ListContainersAsync(
                Harbora.Infrastructure.Deployments.DeploymentPlanning.AppLabel, bounded.Token);

            return containers.Count(c =>
                names.Contains(c.Name) && c.State.Equals("running", StringComparison.OrdinalIgnoreCase));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Listing replica containers for app {Slug} timed out.", slug);
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "Could not list replica containers for app {Slug}.", slug);
            return null;
        }
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
        // 5.1 (per-app grants, HARBORA-0035): found by AppScopeCensusTests.
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

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
            $"{app.Name}: auth={basicAuthEnabled}, ips={allowed.Count}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        // The proxy's own verdict, not an assumption. A rolled-back apply means the rows changed
        // and the traffic did not — the one outcome nobody must be told is "saved".
        TempData[applied.Success ? "Message" : "Error"] = applied.Success
            ? (IsFa ? "محافظت اعمال شد." : "Protection applied.")
            : (IsFa ? "پیکربندی پروکسی اعمال نشد: " : "The proxy configuration was not applied: ") + applied.Error;

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// 2.1 (2026-09 market-gaps round two): creates or updates this app's outside-in HTTP check —
    /// interval, path, expected status, optional body match. One row per app (see <c>UptimeCheck</c>'s
    /// own doc for why it is not on <c>App</c> directly), so this both creates the first-ever check and
    /// edits an existing one through the same action, the same "no separate create/edit route" shape
    /// <c>SetProtection</c> above already gives per-app configuration that is either present or not.
    /// </summary>
    [HttpPost("/apps/{id:guid}/uptime-check")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AlertsManage)]
    public async Task<IActionResult> SaveUptimeCheck(
        Guid id, bool isEnabled, string? path, int expectedStatus, string? bodyContains,
        int intervalSeconds, int timeoutSeconds, CancellationToken ct)
    {
        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var check = await db.UptimeChecks.FirstOrDefaultAsync(c => c.AppId == id && c.WorkspaceId == WorkspaceId, ct);
        if (check is null)
        {
            check = new UptimeCheck { WorkspaceId = WorkspaceId, AppId = id };
            db.UptimeChecks.Add(check);
        }

        check.IsEnabled = isEnabled;
        check.Path = string.IsNullOrWhiteSpace(path) ? "/" : path.Trim();
        check.ExpectedStatus = expectedStatus is >= 100 and <= 599 ? expectedStatus : 200;
        check.BodyContains = string.IsNullOrWhiteSpace(bodyContains) ? null : bodyContains.Trim();
        check.IntervalSeconds = Math.Clamp(intervalSeconds <= 0 ? 60 : intervalSeconds, 30, 24 * 60 * 60);
        check.TimeoutSeconds = Math.Clamp(timeoutSeconds <= 0 ? 10 : timeoutSeconds, 1, 60);
        // A change takes effect on the very next tick, not whenever whatever interval was configured
        // before this edit happens to next elapse.
        check.NextCheckAt = null;

        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.uptime_check_saved", "app",
            $"{app.Name}: enabled={isEnabled}, path={check.Path}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa ? "بررسی دوره‌ای ذخیره شد." : "Uptime check saved.";
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
        // The service asks this too; asked here as well because it decides this action's answer (403).
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var outcome = await configuration.AddVolumeAsync(Actor, id, mountPath, ct);
        if (!outcome.Ok)
        {
            TempData["Error"] = Say(outcome.Refusals);
            return RedirectToAction(nameof(Volumes), new { id });
        }

        // Says what it does not do. The volume is a row until the next deployment creates the
        // container that mounts it, and a person who uploads a file before then would be writing
        // into something the running container cannot see.
        var added = outcome.Value!.MountPath;
        TempData["Message"] = IsFa
            ? $"«{added}» اضافه شد. با استقرار بعدی به کانتینر وصل می‌شود."
            : $"{added} was added. It is attached to the container on the next deployment.";
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
        // Both asked again by the service; asked here because they decide this action's answer — 403
        // for an app the caller may not touch, 404 for a volume that is not this app's.
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();
        if (!await db.Volumes.AnyAsync(v => v.Id == volumeId && v.AppId == id, ct)) return NotFound();

        var outcome = await configuration.RemoveVolumeAsync(Actor, id, AppItemRef.ById(volumeId), deleteData, ct);
        if (!outcome.Ok)
        {
            TempData["Error"] = Say(outcome.Refusals);
            return RedirectToAction(nameof(Volumes), new { id });
        }

        // What actually happened, not what was asked for — see AppConfigurationService.RemoveVolumeAsync.
        var (path, name, _, dataDeleted) = outcome.Value!;
        if (dataDeleted)
        {
            TempData["Message"] = IsFa ? $"«{path}» و داده‌هایش حذف شد." : $"{path} and its data were deleted.";
        }
        else if (deleteData)
        {
            // Asked for, and not done — named as what it is rather than folded into the "kept" message.
            TempData["Error"] = IsFa
                ? $"«{path}» از اپ برداشته شد، اما حذف دادهٔ آن ناموفق بود. داده‌اش هنوز روی سرور در والیوم «{name}» است."
                : $"{path} was unmounted, but deleting its data failed. The data is still on the server in volume \"{name}\".";
        }
        else
        {
            TempData["Message"] = IsFa
                ? $"«{path}» دیگر وصل نمی‌شود. داده‌ها روی سرور ماندند."
                : $"{path} is no longer mounted. The data is still on the server.";
        }

        return RedirectToAction(nameof(Volumes), new { id });
    }

    /// <summary>
    /// Toggles <see cref="Volume.Protected"/> — HARBORA-0033's only way to set the flag every
    /// destructive delete path now checks. Deliberately not gated on anything the volume is doing
    /// (mounted, backed up, whatever): turning protection on or off changes no data by itself, only
    /// what the next delete is allowed to do.
    /// </summary>
    [HttpPost("/apps/{id:guid}/volumes/{volumeId:guid}/protect")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SetVolumeProtection(Guid id, Guid volumeId, bool protect, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.Include(a => a.Volumes)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var volume = app.Volumes.FirstOrDefault(v => v.Id == volumeId);
        if (volume is null) return NotFound();

        volume.Protected = protect;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync(
            protect ? "app.volume_protected" : "app.volume_unprotected", "app",
            $"{app.Name}:{volume.MountPath}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = protect
            ? (IsFa ? $"«{volume.MountPath}» محافظت شد. تا خاموش نشود، داده‌اش حذف نمی‌شود." : $"{volume.MountPath} is now protected. Its data cannot be deleted until this is turned off.")
            : (IsFa ? $"محافظت «{volume.MountPath}» خاموش شد." : $"Protection for {volume.MountPath} was turned off.");

        return RedirectToAction(nameof(Volumes), new { id });
    }

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

        await audit.LogAsync("app.resized", "app", $"{app.Name}={size?.Key ?? "unlimited"}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        var fa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        TempData["Message"] = fa
            ? $"اندازه روی «{size?.Name ?? "بدون سقف"}» تنظیم شد. با استقرار بعدی اعمال می‌شود."
            : $"Size set to {size?.Name ?? "no limit"}. It applies on the next deployment.";

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Changes how many containers this app's next deployment starts.
    ///
    /// <para>
    /// Written to the row and nothing more — the same shape <see cref="Resize"/> just above already
    /// uses for a resource change that only takes effect once something is actually released again.
    /// A live add/remove of running containers outside <c>DeploymentPipeline</c> would mean a second,
    /// less-tested path doing the pipeline's own job — starting containers, health-checking them,
    /// rewiring the proxy, reverting on failure — and drifting from it is exactly how this platform
    /// has stranded ports and containers before. Scaling down still frees everything it should: the
    /// next deploy's cutover keeps only the new, lower replica count and retires the rest, and its
    /// remote-node port release already matches by deployment number regardless of how many replicas
    /// held one.
    /// </para>
    /// </summary>
    [HttpPost("/apps/{id:guid}/replicas")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SetReplicas(Guid id, int replicas, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        if (replicas < 1)
        {
            TempData["Error"] = IsFa ? "تعداد تکرار باید حداقل ۱ باشد." : "Replicas must be at least 1.";
            return RedirectToAction(nameof(Details), new { id });
        }

        var current = app.DesiredReplicas ?? 1;
        if (replicas > current)
        {
            // Only the EXTRA replicas need to fit: the ones already running are already counted in
            // the workspace's committed usage (QuotaService.SnapshotAsync reads DesiredReplicas off
            // this same row), so checking the whole new total here would double-count what this app
            // already holds and refuse a scale-up that does fit.
            var extra = replicas - current;
            var delta = new Harbora.Application.Abstractions.WorkloadQuotaDelta(
                MemoryBytes: app.MemoryLimitBytes * extra, CpuCores: app.CpuLimit * extra);
            var check = await quota.CanAddWorkloadsAsync(WorkspaceId, delta, ct);
            if (!check.Allowed)
            {
                TempData["Error"] = (IsFa ? check.ReasonFa : null) ?? check.Reason;
                return RedirectToAction(nameof(Details), new { id });
            }
        }

        app.DesiredReplicas = replicas;
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.replicas-set", "app", $"{app.Name}={replicas}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? $"تعداد تکرار روی {replicas} تنظیم شد. با استقرار بعدی اعمال می‌شود."
            : $"Replicas set to {replicas}. It applies on the next deployment.";

        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Sets or clears the command a long-running service starts with — the only correction there has
    /// ever been for a buildpack that guessed wrong, short of committing a Dockerfile to the
    /// repository (which is exactly what the buildpack exists to avoid). Written to the row and
    /// nothing more, the same "applies on the next deployment" shape <see cref="Resize"/> and
    /// <see cref="SetReplicas"/> already use — <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/>
    /// is the only thing that ever starts a container.
    ///
    /// <para>
    /// The view withholds this control for a <c>Cron</c> or <c>ReleaseTask</c> app
    /// (<c>ServicePlan.IsLongRunning</c>): the first already has its own <see cref="App.Command"/>,
    /// and neither ever starts a long-running container this could describe. That is a rendering
    /// choice, not an authorization boundary, so a direct post is still honoured rather than 404ed.
    /// </para>
    /// </summary>
    [HttpPost("/apps/{id:guid}/start-command")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> SetStartCommand(Guid id, string? startCommand, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // Whitespace is not a value anyone typed on purpose — the same rule Create already applies
        // to ReleaseCommand, so a stray space does not become a one-character command that fails a
        // container's shell just as loudly as a real, deliberate mistake.
        app.StartCommand = string.IsNullOrWhiteSpace(startCommand) ? null : startCommand.Trim();
        await db.SaveChangesAsync(ct);

        await audit.LogAsync("app.start-command-set", "app",
            $"{app.Name}={app.StartCommand ?? "(cleared)"}", ClientIp, workspaceId: WorkspaceId, ct: ct);

        TempData["Message"] = IsFa
            ? "دستور راه‌اندازی ذخیره شد. با استقرار بعدی اعمال می‌شود."
            : "Start command saved. It applies on the next deployment.";

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

        await audit.LogAsync("app.version_updated", "app", $"{app.Name}={version.Version}", ClientIp, workspaceId: WorkspaceId, ct: ct);

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
        catch (CapacityRefusedException ex)
        {
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (LowDiskRefusedException ex)
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

        await audit.LogAsync("app.tag_updated", "app", $"{app.Name}={repository}:{tag}", ClientIp, workspaceId: WorkspaceId, ct: ct);

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
        catch (CapacityRefusedException ex)
        {
            TempData["Error"] = (IsFa ? ex.ReasonFa : null) ?? ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (LowDiskRefusedException ex)
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
    public async Task<IActionResult> Deploy(Guid id, string? gitRef, bool forceRebuild, CancellationToken ct)
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
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, currentUser.UserId ?? Guid.Empty, gitRef ?? app.GitRef,
                    ForceRebuild: forceRebuild), ct);
        }
        catch (InvalidOperationException ex)
        {
            // e.g. a rollback is mid-flight — surface the reason instead of a 500.
            TempData["Error"] = ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        await audit.LogAsync("app.deploy", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);

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
        // 5.1 (per-app grants, HARBORA-0035): neither this nor Rollback below checked scope — found
        // by AppScopeCensusTests. A member scoped to projects could preview, and then run, a
        // rollback on any app in the workspace.
        if (!await MayAsync(id, Capabilities.AppsDeploy, ct)) return NotFound();

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
        // 5.1: re-checked here too, not only on the confirmation screen above — the same reason the
        // method's own comment three lines down re-checks CanRollback instead of trusting the page.
        if (!await MayAsync(id, Capabilities.AppsDeploy, ct)) return NotFound();

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
            metadataJson: $"{{\"toDeploymentId\":\"{deploymentId}\"}}", workspaceId: WorkspaceId, ct: ct);
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
        // 5.1 (per-app grants, HARBORA-0035): found by AppScopeCensusTests — a member scoped to
        // projects could run any cron app in the workspace on demand regardless of scope.
        if (!await MayAsync(id, Capabilities.AppsOperate, ct)) return NotFound();

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

        await jobs.EnqueueAsync(JobKind.CronRun, app.Id, app.WorkspaceId, ct);
        await audit.LogAsync("app.cron.run", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
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

    /// <summary>
    /// Turns maintenance mode on: the app's own hosts start answering with the panel's themed 503
    /// instead of the container, which keeps running untouched. An immediate operational act, not a
    /// draft — <see cref="IAppOperationsService.SetMaintenanceModeAsync"/> applies through the proxy
    /// before this even returns, so there is nothing here to publish later.
    ///
    /// <para>
    /// Honesty on failure: a refused apply comes back as <c>result.Success == false</c>, and
    /// <c>App.MaintenanceMode</c> was never written — the error banner is the only trace, exactly the
    /// pattern the route designer's own apply-result banner established (never "Maintenance enabled"
    /// for an apply that failed).
    /// </para>
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> EnableMaintenance(
        Guid id, string? maintenanceMessage, string? maintenanceMessageFa, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await ops.SetMaintenanceModeAsync(id, true, maintenanceMessage, maintenanceMessageFa, ct);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.maintenance.on", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa
            ? "حالت تعمیر روشن شد؛ بازدیدکنندگان صفحه تعمیر را می‌بینند."
            : "Maintenance mode turned on; visitors now see the maintenance page.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Turns maintenance mode off: the app's own routes go back to the real container.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> DisableMaintenance(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await ops.SetMaintenanceModeAsync(id, false, null, null, ct);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.maintenance.off", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa ? "حالت تعمیر خاموش شد." : "Maintenance mode turned off.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Turns per-app rate limiting on (or reconfigures it while already on) — every route this app
    /// owns starts answering 429 past the configured rate, through
    /// <see cref="IAppOperationsService.SetRateLimitAsync"/>. Same honesty rule as maintenance mode
    /// just above: a refused apply never reaches <c>App.RateLimitEnabled</c>, so the error banner is
    /// the only trace, never a flag reading "on" over a router nobody updated.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> SetRateLimit(Guid id, int rateLimitAverage, int rateLimitBurst, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await ops.SetRateLimitAsync(id, true, rateLimitAverage, rateLimitBurst, ct);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.ratelimit.on", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa
            ? $"محدودیت نرخ فعال شد: حداکثر {rateLimitAverage} درخواست در دقیقه (انفجار {rateLimitBurst})."
            : $"Rate limiting turned on: up to {rateLimitAverage} requests/minute (burst {rateLimitBurst}).";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>Turns per-app rate limiting off: the app's routes go back to unlimited. The last
    /// configured average/burst are kept so turning it back on starts from where it left off.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> DisableRateLimit(Guid id, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await ops.SetRateLimitAsync(id, false, 0, 0, ct);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.ratelimit.off", "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = IsFa ? "محدودیت نرخ خاموش شد." : "Rate limiting turned off.";
        return RedirectToAction(nameof(Details), new { id });
    }

    /// <summary>
    /// Sets (or, at 0, turns off) how many days of this app's own container output stay searchable
    /// after the container that wrote them is gone (2.2, 2026-09 log-retention plan), through
    /// <see cref="IAppOperationsService.SetLogRetentionAsync"/>. Lives on the Logs tab rather than
    /// Details, alongside the search it feeds.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsOperate)]
    public async Task<IActionResult> SetLogRetention(Guid id, int days, CancellationToken ct)
    {
        if (!await OwnsAsync(id, ct)) return NotFound();

        var result = await ops.SetLogRetentionAsync(id, days, ct);
        if (!result.Success)
        {
            TempData["Error"] = result.Error;
            return RedirectToAction(nameof(Logs), new { id });
        }

        await audit.LogAsync(days > 0 ? "app.logretention.set" : "app.logretention.off",
            "app", id.ToString(), ClientIp, workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = days > 0
            ? (IsFa ? $"نگه‌داری لاگ روی {days} روز تنظیم شد." : $"Log retention set to {days} day(s).")
            : (IsFa ? "نگه‌داری لاگ خاموش شد؛ لاگ‌های ذخیره‌شده حذف شدند." : "Log retention turned off; stored logs were deleted.");
        return RedirectToAction(nameof(Logs), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsDelete)]
    public async Task<IActionResult> Delete(Guid id, bool removeVolumes, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsDelete, ct)) return NotFound();

        try
        {
            await ops.DeleteAsync(id, removeVolumes, ct);
        }
        catch (VolumeProtectedException ex)
        {
            // HARBORA-0033. Refused before anything happened — the app, its container and every one
            // of its volumes are exactly as they were a moment ago, so this redirects back to the app
            // rather than to the (still populated) list.
            TempData["Error"] = IsFa ? ex.ReasonFa : ex.Message;
            return RedirectToAction(nameof(Details), new { id });
        }

        await audit.LogAsync("app.delete", "app", id.ToString(), ClientIp,
            metadataJson: $"{{\"removeVolumes\":{removeVolumes.ToString().ToLowerInvariant()}}}",
            workspaceId: WorkspaceId, ct: ct);
        TempData["Message"] = removeVolumes
            ? (IsFa ? "اپ و داده‌های والیوم‌هایش حذف شدند." : "The app and its volume data were deleted.")
            : (IsFa ? "اپ حذف شد. داده‌های والیوم‌هایش روی سرور ماندند." : "App deleted. Its volume data is still on the server.");
        return RedirectToAction(nameof(Index));
    }

    // ---- logs ----

    [HttpGet("/apps/{id:guid}/logs")]
    public async Task<IActionResult> Logs(Guid id, CancellationToken ct)
    {
        if (!await access.CanSeeAppAsync(id, ct)) return NotFound();

        var app = await db.Apps.FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        // 2.2 (2026-09 log-retention plan): how far back this app's persisted history actually
        // reaches right now, shown next to the retention setting itself rather than only inside a
        // search result — an operator turning this on wants to see it start accumulating without
        // having to run a search first.
        if (app.LogRetentionDays > 0)
        {
            ViewBag.LogRetentionEarliestAt = await db.AppLogLines.AsNoTracking()
                .Where(l => l.AppId == id)
                .OrderBy(l => l.Timestamp)
                .Select(l => (DateTimeOffset?)l.Timestamp)
                .FirstOrDefaultAsync(ct);
        }

        return View(app);
    }

    /// <summary>
    /// The tail, filtered before it is sent. Done here rather than in the browser because the line
    /// that explains an outage is usually not the one on screen.
    ///
    /// <para>
    /// Reports what it actually covered on the way out — <c>X-Log-Lines-Scanned</c> and, when a time
    /// window was asked for, <c>X-Log-Time-Window-Honored</c> — as headers rather than folded into the
    /// body, so the plain-text body stays exactly what it always was (what <c>LogsDownload</c> below
    /// sends when someone downloads "whatever is on screen") while the page can still say what a
    /// search covered without guessing from an empty pane, which looks the same whether nothing
    /// matched or nothing was actually searched.
    /// </para>
    /// </summary>
    [HttpGet("/apps/{id:guid}/logs/data")]
    public async Task<IActionResult> LogsData(
        Guid id, int tail = 200, string? search = null, bool problems = false, int minutes = 0,
        CancellationToken ct = default)
    {
        if (!await access.CanSeeAppAsync(id, ct)) return NotFound();

        var window = minutes > 0 ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;

        // Unfiltered live tail: untouched by this feature, byte for byte — the common case, polled
        // every few seconds by auto-refresh, stays exactly as cheap and as simple as it always was.
        if (window is null && string.IsNullOrWhiteSpace(search) && !problems)
        {
            var raw = await ops.GetLogsAsync(id, tail, ct);
            Response.Headers["X-Log-Lines-Scanned"] = CountLines(raw).ToString();
            return Content(raw, "text/plain");
        }

        var result = await ops.SearchLogsAsync([id], search, problems, window, tail <= 0 ? 200 : tail, ct);
        var coverage = result.Coverage[0];

        Response.Headers["X-Log-Lines-Scanned"] = coverage.LinesScanned.ToString();
        if (coverage.TimeWindowRequested)
            Response.Headers["X-Log-Time-Window-Honored"] = coverage.TimeWindowHonored ? "true" : "false";

        // 2.2 (2026-09 log-retention plan): how far back this search actually reached, and whether the
        // disk budget — not the configured day count — is why it does not reach further. Only sent
        // when retention is on for this app; a header for a search that never consulted a persisted
        // store at all would be a "0" printed where the truth is "not measured".
        if (coverage.RetentionEnabled)
        {
            Response.Headers["X-Log-Retention-Enabled"] = "true";
            Response.Headers["X-Log-Budget-Capped"] = coverage.BudgetCapped ? "true" : "false";
            if (coverage.ReachedBackTo is { } r)
                Response.Headers["X-Log-Reached-Back-To"] = r.UtcDateTime.ToString("O");
        }

        if (!coverage.Reached) return Content(string.Empty, "text/plain");

        var lines = result.Hits.Select(h => h.Line).ToList();
        // Said plainly, because an empty pane looks the same as a broken one.
        return Content(lines.Count == 0
            ? "No lines match that filter."
            : string.Join('\n', lines), "text/plain");
    }

    private static int CountLines(string? raw) =>
        string.IsNullOrEmpty(raw) ? 0 : raw.Replace("\r\n", "\n").Split('\n').Length;

    /// <summary>Downloads the tail as a file — the second thing anyone does is send it to someone.</summary>
    [HttpGet("/apps/{id:guid}/logs/download")]
    public async Task<IActionResult> LogsDownload(
        Guid id, int tail = 2000, string? search = null, bool problems = false, int minutes = 0,
        CancellationToken ct = default)
    {
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return NotFound();

        var window = minutes > 0 ? TimeSpan.FromMinutes(minutes) : (TimeSpan?)null;
        List<string> lines;
        if (window is null)
        {
            var text = await ops.GetLogsAsync(id, tail, ct);
            lines = Harbora.Infrastructure.Deployments.LogFilter.Apply(text, search, problems).ToList();
        }
        else
        {
            var result = await ops.SearchLogsAsync([id], search, problems, window, tail, ct);
            lines = result.Hits.Select(h => h.Line).ToList();
        }

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
    //
    // The access checks in these four actions are asked again by IAppConfigurationService, which is
    // the guarantee for every caller. They stay here because they decide these actions' own answers
    // (404 for the env and domain-delete forms, 403 for adding a domain), as they always have.

    [HttpPost("/apps/{id:guid}/env")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AddEnv(Guid id, string key, string? value, bool isSecret, bool availableAtBuild, CancellationToken ct)
    {
        // 5.1 (per-app grants, HARBORA-0035): DeleteEnv right below already checks this; AddEnv —
        // the door that writes a SECRET onto an app — did not, so a member scoped to projects could
        // add or overwrite an environment variable on any app in the workspace regardless of scope.
        // Found by AppScopeCensusTests.
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var outcome = await configuration.SetEnvAsync(Actor, id, [new AppEnvVarInput(key, value, isSecret, availableAtBuild)], ct);
        if (!outcome.Ok) { TempData["Error"] = Say(outcome.Refusals); return RedirectToAction(nameof(Details), new { id }); }

        TempData["Message"] = IsFa ? "متغیر ذخیره شد. با استقرار بعدی اعمال می‌شود." : "Variable saved. Redeploy to apply.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/env/{envId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteEnv(Guid id, Guid envId, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        // A variable that is not there any more (a second click, another tab) is said to be not there,
        // rather than "removed" for a removal that did not happen.
        var outcome = await configuration.DeleteEnvAsync(Actor, id, AppItemRef.ById(envId), ct);
        if (!outcome.Ok) { TempData["Error"] = Say(outcome.Refusals); return RedirectToAction(nameof(Details), new { id }); }

        TempData["Message"] = IsFa ? "متغیر حذف شد. با استقرار بعدی اعمال می‌شود." : "Variable removed. Redeploy to apply.";
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---- domains ----

    [HttpPost("/apps/{id:guid}/domains")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> AddDomain(Guid id, string host, bool ssl, CancellationToken ct)
    {
        if (!await access.CanTouchAppAsync(id, Capabilities.AppsEnv, ct)) return Forbid();

        // Uniqueness across the platform, the platform's own host names, the plan's domain ceiling —
        // all in AppConfigurationService.AddDomainAsync, the one path that attaches a host to an
        // existing app without going through AppAddressAssigner.
        var outcome = await configuration.AddDomainAsync(Actor, id, host, ssl, ct);
        if (!outcome.Ok) { TempData["Error"] = Say(outcome.Refusals); return RedirectToAction(nameof(Details), new { id }); }

        TempData["Message"] = IsFa ? "دامنه اضافه شد. با استقرار بعدی مسیردهی می‌شود." : "Domain added. Redeploy to route it.";
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("/apps/{id:guid}/domains/{domainId:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.AppsEnv)]
    public async Task<IActionResult> DeleteDomain(Guid id, Guid domainId, CancellationToken ct)
    {
        if (!await MayAsync(id, Capabilities.AppsEnv, ct)) return NotFound();

        var outcome = await configuration.RemoveDomainAsync(Actor, id, AppItemRef.ById(domainId), ct);
        if (!outcome.Ok) { TempData["Error"] = Say(outcome.Refusals); return RedirectToAction(nameof(Details), new { id }); }

        TempData["Message"] = IsFa ? "دامنه حذف شد." : "Domain removed.";
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

        // The server list and the size list used to be built here for two dropdowns on the form. The
        // SizePicker view component asks those questions itself now — including which servers this
        // workspace's plan may place on and which tiers it allows — so building them again here would
        // be six queries a request whose answers nothing reads.

        // The platform default, still needed: it is what the chooser opens on. Without one, every
        // create form started at "no ceiling", which is the option nobody picks on purpose and the one
        // that costs the most.
        var defaultSize = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.DefaultInstanceSize)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        ViewBag.DefaultSize = defaultSize;
        ViewBag.PreviewsDefault = string.Equals(
            await db.Settings.IgnoreQueryFilters()
                .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.PreviewsDefault)
                .Select(s => s.Value).FirstOrDefaultAsync(ct),
            "true", StringComparison.OrdinalIgnoreCase);
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
}
