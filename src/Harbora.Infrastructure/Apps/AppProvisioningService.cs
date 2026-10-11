using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Git;
using Harbora.Domain.Networking;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Templates;
using Harbora.Shared;
using Microsoft.EntityFrameworkCore;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// See <see cref="IAppProvisioningService"/>. The checks run in the order the form always ran them, so
/// a form showing several problems at once lists them in the order it did before.
///
/// <para>
/// Validation and creation share every rule below; the differences are only the ones "no side effects"
/// forces. Validation does not take the creation lock, reads the environment without creating the
/// default one (<see cref="PeekEnvironmentAsync"/>), and asks the address and template questions of an
/// app that is never attached to the context. Creation takes the lock before its first check and runs
/// all of them inside it.
/// </para>
/// </summary>
public sealed class AppProvisioningService(
    HarboraDbContext db,
    IQuotaService quota,
    ISchedulerService scheduler,
    ISecretProtector protector,
    IAuditLogger audit,
    IDeploymentEngine deployments,
    ProjectService projects,
    ProjectAccessService access,
    AppAddressAssigner addresses,
    ResourceCreationBilling creationBilling) : IAppProvisioningService
{
    public async Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(AppCreateRequest request, CancellationToken ct)
    {
        var refusals = new List<ServiceRefusal>();
        var slug = await CheckFormAsync(request, refusals, ct);
        var size = await SizeAsync(request.InstanceSizeKey, ct);
        var environment = await PeekEnvironmentAsync(request.WorkspaceId, request.EnvironmentId, ct);

        // The create would make this workspace's default project and environment first, and
        // ProjectService refuses that past the plan's ceilings — ask the same question it asks.
        if (environment is null)
        {
            var check = await quota.CanAddGovernedResourcesAsync(request.WorkspaceId,
                new GovernanceQuotaDelta(Projects: 1, Environments: 1), ct);
            if (!check.Allowed) refusals.Add(AppRefusals.Quota(check, "Plan quota exceeded.", "سقف پلن پر شده است."));
        }

        var placed = await CheckWorkspaceAsync(request, size, environment, refusals, ct);
        if (refusals.Count > 0) return refusals;

        // Never added to the context: AppAddressAssigner and the template only fill in its collections,
        // and nothing else in this request can save an object EF is not tracking.
        var app = Shape(request, slug, size, environment?.Id ?? Guid.Empty, placed.ServerId!.Value);
        var address = await AssignAddressAsync(app, request, refusals, ct);
        if (address is null) return refusals;

        if (placed.Template is { } template) ApplyTemplate(app, template);
        await CheckGovernedAsync(request.WorkspaceId, app, refusals, ct);
        return refusals;
    }

    public async Task<ServiceOutcome<AppCreateResult>> CreateAsync(AppCreateRequest request, CancellationToken ct)
    {
        // Taken before the first check, not after the cheap ones: every answer below — the slug is
        // free, the plan has room, the typed host is not in use — is then read while no other creation
        // in this workspace can change it, and stays true until the app and its first-hour debit
        // commit. A validate that ran earlier proves nothing here; the world may have moved since.
        await using var quotaReservation = await quota.AcquireCreationLockAsync(request.WorkspaceId, ct);

        var refusals = new List<ServiceRefusal>();
        var slug = await CheckFormAsync(request, refusals, ct);
        var size = await SizeAsync(request.InstanceSizeKey, ct);

        // The environment id is a writable form value. Workspace ownership alone is not enough: a
        // project-scoped member must not create an app in another project in the same workspace —
        // CheckWorkspaceAsync asks that of whatever this resolves to.
        Environment? environment;
        try
        {
            environment = await projects.ResolveEnvironmentAsync(request.WorkspaceId, request.EnvironmentId, ct);
        }
        catch (QuotaRefusedException ex)
        {
            // Only reachable for a workspace with no environment at all, whose default project would
            // cross the plan's ceilings. Nothing was saved: ProjectService refuses before it builds the
            // project. It used to escape the form as an unhandled exception; it is now one refusal
            // among the others, and the rest of the checks still run so all of them are reported.
            refusals.Add(AppRefusals.Quota(new QuotaCheck(false, ex.Message, ex.ReasonFa),
                "Plan quota exceeded.", "سقف پلن پر شده است."));
            environment = null;
        }

        var placed = await CheckWorkspaceAsync(request, size, environment, refusals, ct);
        if (refusals.Count > 0) return ServiceOutcome<AppCreateResult>.Refused(refusals);

        // Not null here: a refused default environment added a refusal above.
        var app = Shape(request, slug, size, environment!.Id, placed.ServerId!.Value);

        // One rule, one place. This used to derive the host inline, check reserved names inline, and
        // then silently skip the insert when the name was taken — so an app could be created with no
        // address and no explanation. AppAddressAssigner answers all three, and says which happened.
        var address = await AssignAddressAsync(app, request, refusals, ct);
        if (address is null) return ServiceOutcome<AppCreateResult>.Refused(refusals);

        // A template describes more than an image and a port. Until this was applied, an app created
        // from one arrived without the volume it declared — a static site whose content vanished on
        // every redeploy — and without the variables it said it needed.
        string? templateAdvice = null;
        if (placed.Template is { } template) templateAdvice = ApplyTemplate(app, template);

        await CheckGovernedAsync(request.WorkspaceId, app, refusals, ct);
        if (refusals.Count > 0) return ServiceOutcome<AppCreateResult>.Refused(refusals);

        if (UsesRepository(request.SourceType))
        {
            app.GitRepository = new GitRepository
            {
                Provider = new GitProvider
                {
                    WorkspaceId = request.WorkspaceId,
                    Name = "Custom",
                    Type = GitProviderType.Custom,
                    ApiBaseUrl = string.Empty,
                    EncryptedCredential = string.IsNullOrWhiteSpace(request.GitToken) ? null : protector.Protect(request.GitToken)
                },
                FullName = DeriveRepoName(request.CloneUrl!),
                CloneUrl = request.CloneUrl!,
                DefaultBranch = request.GitRef ?? "main",
                WebhookSecret = Guid.NewGuid().ToString("N")
            };
        }

        // Attached only now, after every refusal above: a refused create leaves nothing tracked behind
        // it for some later SaveChangesAsync in the same request — the agent's own bookkeeping, an audit
        // entry — to persist by accident. The repository and its provider come along as the graph.
        db.Apps.Add(app);
        try
        {
            await creationBilling.SaveAsync(request.WorkspaceId,
                [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey, app.ServerId)], ct);
        }
        catch (CreationPaymentRequiredException ex)
        {
            Forget(app);
            return ServiceOutcome<AppCreateResult>.Refused(new ServiceRefusal(ex.Message, ex.ReasonFa));
        }

        await quotaReservation.CommitAsync(ct);

        // The same "{app}:{what}" target shape as every other app.* entry these services write, the
        // slug being the "what" here because it is the one name that is unique; the id is in metadata.
        await audit.LogAsync("app.created", "app", $"{app.Name}:{app.Slug}", request.ClientIp,
            metadataJson: JsonSerializer.Serialize(new
            {
                appId = app.Id,
                name = app.Name,
                slug = app.Slug,
                sourceType = app.SourceType.ToString(),
                kind = app.Kind.ToString(),
                environmentId = app.EnvironmentId,
                serverId = app.ServerId,
                instanceSize = app.InstanceSizeKey,
                templateId = app.TemplateId,
                host = address.Value.Host
            }),
            workspaceId: request.WorkspaceId, ct: ct);

        // "Give it a repo and it just works": build + deploy right away.
        Guid? deploymentId = null;
        ServiceRefusal? deployRefusal = null;
        if (request.DeployNow && CanDeploy(request.SourceType))
        {
            try
            {
                deploymentId = await deployments.QueueDeploymentAsync(
                    new DeploymentRequest(app.Id, request.Trigger, request.UserId, app.GitRef), ct);
            }
            // The application and its first-hour debit are already committed. Each of these only
            // means its first deploy did not start: a deployment ceiling, P7's node re-check at queue
            // time, or a disk too full to build on.
            catch (QuotaRefusedException ex) { deployRefusal = new(ex.Message, ex.ReasonFa ?? ex.Message); }
            catch (CapacityRefusedException ex) { deployRefusal = new(ex.Message, ex.ReasonFa ?? ex.Message); }
            catch (LowDiskRefusedException ex) { deployRefusal = new(ex.Message, ex.ReasonFa ?? ex.Message); }
        }

        return ServiceOutcome<AppCreateResult>.Success(new AppCreateResult(
            app.Id, app.Name, app.Slug, address.Value.Host, AddressNotice(address.Value),
            templateAdvice, deploymentId, deployRefusal));
    }

    /// <summary>
    /// The rules that need nothing but the request: name, slug, source, root directory, schedule.
    /// Returns the slug the app will get.
    /// </summary>
    private async Task<string> CheckFormAsync(AppCreateRequest request, List<ServiceRefusal> refusals, CancellationToken ct)
    {
        // The form's [Required] says this too; the service says it for callers that have no form.
        if (string.IsNullOrWhiteSpace(request.Name)) refusals.Add(AppRefusals.NameRequired);

        // Left blank, the slug is auto-derived from the name and auto-suffixed the way it always was.
        // Typed by hand, it is a deliberate choice and is refused rather than silently renamed if the
        // platform-wide namespace already holds it — see AppRefusals.TakenSlug for why the message has
        // to say so.
        string slug;
        if (string.IsNullOrWhiteSpace(request.Slug))
        {
            slug = await UniqueSlugAsync(Slugify(request.Name), ct);
        }
        else
        {
            slug = Slugify(request.Slug);
            if (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Slug == slug, ct))
                refusals.Add(AppRefusals.TakenSlug(slug));
        }

        if (UsesRepository(request.SourceType) && string.IsNullOrWhiteSpace(request.CloneUrl))
            refusals.Add(AppRefusals.CloneUrlRequired);

        if (request.SourceType == AppSourceType.PrebuiltImage && string.IsNullOrWhiteSpace(request.PrebuiltImage))
            refusals.Add(AppRefusals.ImageRequired);

        // Refused by name at the point it is typed — a "../other" or an absolute path is not a
        // sub-directory of anything that got uploaded or cloned, and the build stage would either
        // resolve outside the source tree or never find the folder at all.
        var rootDirectoryRejection = AppRootDirectory.Validate(request.RootDirectory);
        if (rootDirectoryRejection != PathRejection.None)
            refusals.Add(AppRefusals.RootDirectory(request.RootDirectory, rootDirectoryRejection));

        // Checked here so an unreadable schedule is a refusal rather than a job that silently never
        // runs and is only noticed weeks later.
        if (request.Kind == ServiceKind.Cron
            && !CronSchedule.TryParse(request.CronExpression, out _, out var cronError))
            refusals.Add(AppRefusals.CronExpression(cronError!));

        // A schedule with nothing to run is the quietest possible failure: the job fires on time, the
        // image starts and exits, and the history fills with successful runs that did nothing.
        if (request.Kind == ServiceKind.Cron && string.IsNullOrWhiteSpace(request.Command))
            refusals.Add(AppRefusals.CronCommandRequired);

        return slug;
    }

    /// <summary>The instance size (drives container limits and scheduling), or none.</summary>
    private async Task<InstanceSize?> SizeAsync(string? key, CancellationToken ct) =>
        string.IsNullOrWhiteSpace(key)
            ? null
            : await db.InstanceSizes.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key, ct);

    private sealed record Placed(Guid? ServerId, TemplateManifest? Template);

    /// <summary>
    /// What the workspace and the platform have to say: plan quota, the environment's placement
    /// access, a node with room, and the template. <paramref name="environment"/> is null only when
    /// validating for a workspace that has no environment yet — the create would make its default one.
    /// </summary>
    private async Task<Placed> CheckWorkspaceAsync(
        AppCreateRequest request, InstanceSize? size, Environment? environment,
        List<ServiceRefusal> refusals, CancellationToken ct)
    {
        // Enforce the workspace's plan quota before creating anything.
        var check = await quota.CanAddAppAsync(request.WorkspaceId, request.InstanceSizeKey, excludeAppId: null, ct);
        if (!check.Allowed)
            refusals.Add(AppRefusals.Quota(check, "Plan quota exceeded.", "سقف پلن پر شده است."));
        if (request.Kind == ServiceKind.Cron)
        {
            var cronCheck = await quota.CanAddWorkloadsAsync(request.WorkspaceId,
                new WorkloadQuotaDelta(CronJobs: 1), ct);
            if (!cronCheck.Allowed)
                refusals.Add(AppRefusals.Quota(cronCheck,
                    "Scheduled-job quota exceeded.", "سقف کارهای زمان‌بندی‌شده پر شده است."));
        }

        // A workspace with no environment at all gets a default project made for it by the create;
        // nobody holds a grant on a project that does not exist yet, so the question for it is the one
        // ProjectAccess asks of anything outside every project.
        var placement = environment is null
            ? new ResourcePlacement(null, null)
            : new ResourcePlacement(environment.ProjectId, environment.Id);
        if (!await access.AllowsAsync(placement, Capabilities.AppsCreate, ct))
            refusals.Add(AppRefusals.EnvironmentNotAllowed);

        // Place on a node with capacity: honour an explicit choice (guarded) or auto-schedule.
        var needMem = size?.MemoryBytes ?? 0;
        var needCpu = size?.CpuCores ?? 0;
        var placed = request.ServerId is { } chosen && await db.Servers.AnyAsync(s => s.Id == chosen, ct)
            ? await scheduler.CheckAsync(chosen, needMem, needCpu, ct)
            : await scheduler.PlaceAsync(needMem, needCpu, await PlanPoolAsync(request.WorkspaceId, ct), ct);
        if (!placed.Ok)
            refusals.Add(AppRefusals.Capacity(placed));

        TemplateManifest? manifest = null;
        if (request.TemplateId is { } templateId)
        {
            // The same visibility rule the catalogue and the form's template list use: applying a
            // template another tenant wrote and nobody reviewed would run their image inside this
            // one's private network. The form never posts this field; a request that names a template
            // it cannot see, or one whose manifest does not parse, is refused rather than creating a
            // plain app and calling it a template.
            var template = await db.AppTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId, ct);
            if (template is null || !TemplateCatalog.IsVisibleTo(template, request.WorkspaceId))
                refusals.Add(AppRefusals.TemplateUnavailable);
            else if (!TemplateManifest.TryParse(template.ManifestJson, out manifest, out _))
                refusals.Add(AppRefusals.TemplateUnreadable(template.Name));
        }

        return new Placed(placed.ServerId, manifest);
    }

    /// <summary>
    /// The environment <see cref="ProjectService.ResolveEnvironmentAsync"/> would resolve, read without
    /// its one side effect: when the workspace has no environment at all that method creates the default
    /// project, and validation must not. Null in exactly that case.
    /// </summary>
    private async Task<Environment?> PeekEnvironmentAsync(Guid workspaceId, Guid? requested, CancellationToken ct)
    {
        if (requested is { } wanted)
        {
            var owned = await db.Environments.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == wanted && e.WorkspaceId == workspaceId, ct);
            if (owned is not null) return owned;
        }

        return await db.Environments.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>The app row, built exactly as the form always built it, not yet attached to anything.</summary>
    private static App Shape(AppCreateRequest request, string slug, InstanceSize? size, Guid environmentId, Guid serverId) => new()
    {
        WorkspaceId = request.WorkspaceId,
        EnvironmentId = environmentId,
        ServerId = serverId,
        Name = request.Name,
        Slug = slug,
        SourceType = request.SourceType,
        Kind = request.Kind,
        ReleaseCommand = string.IsNullOrWhiteSpace(request.ReleaseCommand) ? null : request.ReleaseCommand.Trim(),
        CronExpression = request.Kind == ServiceKind.Cron && !string.IsNullOrWhiteSpace(request.CronExpression)
            ? request.CronExpression.Trim()
            : null,
        Command = request.Kind == ServiceKind.Cron && !string.IsNullOrWhiteSpace(request.Command)
            ? request.Command.Trim()
            : null,
        // Only meaningful for a service built from a repository — there is no branch to preview
        // otherwise.
        PreviewsEnabled = request.PreviewsEnabled && UsesRepository(request.SourceType),
        ContainerPort = request.ContainerPort <= 0 ? 80 : request.ContainerPort,
        DockerfilePath = request.DockerfilePath,
        ComposeFilePath = request.SourceType == AppSourceType.DockerCompose
            ? (string.IsNullOrWhiteSpace(request.ComposeFilePath) ? null : request.ComposeFilePath.Trim())
            : null,
        // Normalised, not stored raw: forward slashes, no leading "./", no surrounding slashes, "." for
        // the repository root — the one shape every downstream reader (DeploymentPipeline, harbora
        // doctor, the backup metadata snapshot) can trust without re-parsing.
        BuildContextPath = AppRootDirectory.IsRepositoryRoot(request.RootDirectory)
            ? AppRootDirectory.RepositoryRoot
            : AppRootDirectory.Normalise(request.RootDirectory),
        PrebuiltImage = request.PrebuiltImage,
        GitRef = request.GitRef,
        TemplateId = request.TemplateId,
        InstanceSizeKey = size?.Key,
        MemoryLimitBytes = size?.MemoryBytes ?? 0,
        DiskLimitBytes = size?.DiskBytes ?? 0,
        CpuLimit = size?.CpuCores ?? 0
    };

    /// <summary>
    /// Gives the app its address, or adds the refusal and returns null. Typed vs. derived matters for
    /// what a collision means: a blank field gets the slug-derived name, which may be discriminated the
    /// way a clone's own name is; a name someone actually typed is a promise made to them, and a
    /// collision on it is refused instead — see <see cref="AppAddressRequestOrigin"/>.
    /// </summary>
    private async Task<AppAddressDecision?> AssignAddressAsync(
        App app, AppCreateRequest request, List<ServiceRefusal> refusals, CancellationToken ct)
    {
        var origin = string.IsNullOrWhiteSpace(request.Domain)
            ? AppAddressRequestOrigin.Derived
            : AppAddressRequestOrigin.Typed;
        var addressed = await addresses.AssignAsync(app, request.Domain, origin, suffix: null, ct);

        if (addressed.Outcome == AppAddressOutcome.Reserved)
        {
            // request.Domain is null whenever this came from the derived name rather than a typed one —
            // recompute the same host AppAddress.Decide settled on internally, so the message names
            // what actually offended instead of rendering an empty pair of quotes.
            var offendingHost = ServicePlan.HostFor(
                app.Kind, request.Domain, app.Slug, await addresses.RootDomainAsync(ct)) ?? request.Domain ?? "";
            refusals.Add(AppRefusals.ReservedHost(offendingHost, nameof(AppCreateRequest.Domain)));
            return null;
        }

        if (addressed.Outcome == AppAddressOutcome.Taken)
        {
            // Not discriminated onto a zone with no DNS record for the mangled name and no wildcard
            // certificate to cover it — refused, with the wording the domains form has always used.
            refusals.Add(AppRefusals.TakenHost(request.Domain!));
            return null;
        }

        return addressed;
    }

    /// <summary>What to tell somebody about an address that is not the one they would expect.</summary>
    private static AppNotice? AddressNotice(AppAddressDecision addressed) => addressed.Outcome switch
    {
        AppAddressOutcome.Discriminated => new(
            $"That name was taken, so this app is reachable at '{addressed.Host}'.",
            $"نام درخواستی گرفته شده بود؛ این اپ روی «{addressed.Host}» در دسترس است."),
        // Every discriminated attempt collided too — the enum's own docstring says this is "said out
        // loud rather than skipped", so it is: silence here is the exact defect AppAddressAssigner
        // removed everywhere else.
        AppAddressOutcome.Exhausted => new(
            "Several names were already taken, so this app was created with no address for now.",
            "چند نام دیگر هم گرفته شده بود، پس این اپ فعلاً بدون آدرس ساخته شد."),
        _ => null
    };

    /// <summary>
    /// Gives the app what its template declares: the volumes its data lives in, and the variables it
    /// needs — secrets generated, plain ones left for a person and named in the advice returned.
    /// </summary>
    private string? ApplyTemplate(App app, TemplateManifest manifest)
    {
        var plan = TemplateSetup.Prepare(manifest, () => ServiceCredentials.Generate());

        foreach (var mount in plan.VolumeMounts)
            app.Volumes.Add(new Volume
            {
                // Named after the app, not the template: two apps from one template must not share a
                // volume and therefore each other's data.
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

        if (!string.IsNullOrWhiteSpace(manifest.Image)) app.PrebuiltImage ??= manifest.Image;
        if (manifest.Port is { } port && app.ContainerPort <= 0) app.ContainerPort = port;

        return TemplateSetup.Advice(plan);
    }

    /// <summary>The address and the template's volumes count against the plan's governance ceilings as one batch.</summary>
    private async Task CheckGovernedAsync(Guid workspaceId, App app, List<ServiceRefusal> refusals, CancellationToken ct)
    {
        var governed = await quota.CanAddGovernedResourcesAsync(workspaceId,
            new GovernanceQuotaDelta(Domains: app.Domains.Count, Volumes: app.Volumes.Count), ct);
        if (!governed.Allowed)
            refusals.Add(AppRefusals.Quota(governed, "Plan quota exceeded.", "سقف پلن پر شده است."));
    }

    /// <summary>
    /// Stops tracking exactly what this create added — the app graph and the ledger lines the money gate
    /// may have staged for it — after the gate refused. Narrower than <c>ChangeTracker.Clear()</c> on
    /// purpose: the caller may be tracking work of its own in the same request (the agent's plan), and
    /// silently detaching that would make its next save a no-op that reports success.
    /// </summary>
    private void Forget(App app)
    {
        foreach (var entry in db.ChangeTracker.Entries().ToList())
        {
            var ours = entry.Entity switch
            {
                App a => ReferenceEquals(a, app),
                GitRepository r => ReferenceEquals(r, app.GitRepository),
                GitProvider p => ReferenceEquals(p, app.GitRepository?.Provider),
                DomainName d => app.Domains.Contains(d),
                Volume v => app.Volumes.Contains(v),
                EnvironmentVariable v => app.EnvironmentVariables.Contains(v),
                BillingLedgerEntry l => l.ResourceId == app.Id && entry.State == EntityState.Added,
                _ => false
            };
            if (ours) entry.State = EntityState.Detached;
        }
    }

    private static bool UsesRepository(AppSourceType source) => source is AppSourceType.GitRepository
        or AppSourceType.Dockerfile or AppSourceType.StaticSite or AppSourceType.DockerCompose;

    private static bool CanDeploy(AppSourceType source) => source is AppSourceType.GitRepository
        or AppSourceType.Dockerfile or AppSourceType.DockerCompose
        or AppSourceType.PrebuiltImage or AppSourceType.StaticSite;

    /// <summary>The node pool this workspace's plan restricts placement to (null = any pool).</summary>
    private async Task<string?> PlanPoolAsync(Guid workspaceId, CancellationToken ct)
    {
        var planId = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.PlanId).FirstOrDefaultAsync(ct);
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

    /// <summary>
    /// Platform-wide, not per-workspace: app slugs are unique across the whole platform, so the
    /// auto-derived candidate has to be checked against every workspace or two apps named "api" in
    /// different workspaces would both auto-derive to the same slug and the second insert would fail on
    /// the unique index instead of quietly picking "api-2".
    /// </summary>
    private async Task<string> UniqueSlugAsync(string baseSlug, CancellationToken ct)
    {
        var slug = baseSlug;
        for (var n = 2; await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Slug == slug, ct); n++)
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
