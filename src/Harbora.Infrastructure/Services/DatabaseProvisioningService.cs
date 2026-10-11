using System.Text.Json;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// What creating a managed database needs to be told. The create form's fields, minus every MVC
/// type, plus who is asking — so the panel and the in-panel agent describe one request the same way.
/// </summary>
/// <param name="WorkspaceId">The caller's own workspace. Must be the signed-in user's: placement and
/// project access are judged for the signed-in user, so a request naming any other workspace is
/// refused rather than answered for the wrong tenant.</param>
/// <param name="UserId">The signed-in user, under the same rule.</param>
/// <param name="Version">Empty takes the catalogue's first (newest offered) version.</param>
/// <param name="EnvironmentId">Null, or an id that is not this workspace's, uses the default environment.</param>
/// <param name="ServerId">Null lets the scheduler choose the host with the most room.</param>
/// <param name="IpAddress">Recorded on the audit entry; null when there is no request to take it from.</param>
public sealed record DatabaseCreateRequest(
    Guid WorkspaceId,
    Guid UserId,
    string Name,
    ManagedServiceType Type,
    string? Version = null,
    Guid? EnvironmentId = null,
    string? InstanceSizeKey = null,
    Guid? ServerId = null,
    string? RedisEvictionPolicy = null,
    int? RedisMaxMemoryMb = null,
    string? IpAddress = null);

/// <summary>A database that now exists as a row and has been queued for provisioning — not yet running.</summary>
public sealed record DatabaseCreateResult(
    Guid ServiceId,
    string Name,
    string Slug,
    string ContainerName,
    ManagedServiceType Type,
    string Version,
    Guid EnvironmentId,
    Guid ServerId);

/// <summary>A database that is gone, and what became of its data.</summary>
/// <param name="VolumeName">Where the data still is when <paramref name="DataDeleted"/> is false.</param>
public sealed record DatabaseRemoveResult(Guid ServiceId, string Name, string VolumeName, bool DataDeleted);

/// <summary>
/// Creating and removing a managed database. Both used to be written inline in
/// <c>DatabasesController</c>, which meant only a form post could do either and creating one left no
/// audit entry; round six moved them here (phase 0b) so the panel and the in-panel agent's tools run
/// the same checks and leave the same trace. Each mutation has a validate method beside it that
/// answers "would this be refused, and why" without changing anything.
/// </summary>
public interface IDatabaseProvisioningService
{
    /// <summary>
    /// The refusals <see cref="CreateAsync"/> would give right now, with no side effects: no rows, no
    /// billing, no provisioning, no lock, and — unlike the create path — no default environment
    /// conjured for a workspace that has none. Billing is not asked: the first-hour prepayment has no
    /// read-only form, so a wallet that cannot pay is only found out by <see cref="CreateAsync"/>.
    /// </summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(DatabaseCreateRequest request, CancellationToken ct);

    /// <summary>
    /// Re-checks everything under the workspace's creation lock, then saves the database with its
    /// first-hour charge, audits <c>database.created</c> and queues the provision. Nothing is saved
    /// when it refuses.
    /// </summary>
    Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(DatabaseCreateRequest request, CancellationToken ct);

    /// <summary>
    /// What makes removing this database impossible — apps still attached to it, replicas still
    /// streaming from it — or that it is not the caller's to remove. The typed-name confirmation is
    /// not here: that is a question for the person, asked by whoever is talking to them.
    /// </summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveAsync(Guid serviceId, CancellationToken ct);

    /// <summary>
    /// Re-checks <see cref="ValidateRemoveAsync"/>, removes the container (and the volume when
    /// <paramref name="deleteData"/>), and audits <c>database.removed</c> — only once the row is
    /// confirmed gone.
    /// </summary>
    Task<ServiceOutcome<DatabaseRemoveResult>> RemoveAsync(
        Guid serviceId, bool deleteData, string? ipAddress, CancellationToken ct);
}

/// <inheritdoc />
public sealed partial class DatabaseProvisioningService(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    IQuotaService quota,
    ISchedulerService scheduler,
    ISecretProtector protector,
    ProjectService projects,
    ProjectAccessService access,
    ResourceCreationBilling creationBilling,
    IAuditLogger audit,
    ICurrentUser currentUser) : IDatabaseProvisioningService
{
    public const string ContainerPrefix = "harbora-svc-";

    private static readonly ServiceRefusal NotTheCaller = new(
        "This request names a workspace or user other than the one signed in.",
        "این درخواست به فضای کاری یا کاربری غیر از کاربرِ واردشده اشاره می‌کند.");

    private static readonly ServiceRefusal NameRequired = new(
        "A name is required.", "نام لازم است.", nameof(DatabaseCreateRequest.Name));

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(
        DatabaseCreateRequest request, CancellationToken ct)
    {
        if (!IsTheCaller(request)) return [NotTheCaller];

        var environment = await PeekEnvironmentAsync(request.WorkspaceId, request.EnvironmentId, ct);
        var (_, refusals) = await PlanAsync(request, environment, ct);
        return refusals;
    }

    public async Task<ServiceOutcome<DatabaseCreateResult>> CreateAsync(
        DatabaseCreateRequest request, CancellationToken ct)
    {
        if (!IsTheCaller(request)) return ServiceOutcome<DatabaseCreateResult>.Refused(NotTheCaller);

        // Held from the quota check through the save, so two creates cannot both pass a check that
        // only one of them fits under. Disposing it without a commit rolls back anything this request
        // wrote — including a default environment ResolveEnvironmentAsync had to create.
        await using var quotaReservation = await quota.AcquireCreationLockAsync(request.WorkspaceId, ct);

        var environment = await projects.ResolveEnvironmentAsync(request.WorkspaceId, request.EnvironmentId, ct);
        var (plan, refusals) = await PlanAsync(request, environment, ct);
        if (plan is null) return ServiceOutcome<DatabaseCreateResult>.Refused(refusals);

        var service = new ManagedService
        {
            WorkspaceId = request.WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = plan.ServerId,
            Name = request.Name,
            Type = request.Type,
            Version = plan.Version,
            Status = ServiceStatus.Provisioning,
            ContainerName = ContainerPrefix + plan.Slug,
            VolumeName = $"{ContainerPrefix}{plan.Slug}-data",
            InternalPort = plan.Entry.InternalPort,
            Username = "harbora",
            DatabaseName = plan.Entry.HasDatabaseName ? plan.Slug.Replace('-', '_') : string.Empty,
            EncryptedPassword = protector.Protect(ServiceCredentials.Generate())
        };

        // The chosen plan becomes the container's ceiling. Recorded on the row rather than read at
        // provision time so it keeps what was agreed, and a tier later withdrawn from the catalogue
        // does not silently un-limit a running database.
        if (plan.Size is { } size)
        {
            service.InstanceSizeKey = size.Key;
            service.MemoryLimitBytes = size.MemoryBytes;
            service.DiskLimitBytes = size.DiskBytes;
            service.CpuLimit = size.CpuCores;
        }

        if (request.Type == ManagedServiceType.Redis)
        {
            service.RedisEvictionPolicy = string.IsNullOrWhiteSpace(request.RedisEvictionPolicy)
                ? null : request.RedisEvictionPolicy.Trim();
            service.RedisMaxMemoryBytes = plan.RedisMaxMemoryBytes;
        }

        db.ManagedServices.Add(service);

        // D1 (2026-08-25 shared-databases plan): the instance's own admin database, materialised as
        // its first logical database so the very first attachment already has one to point at.
        if (ManagedServiceDatabase.DefaultFor(service) is { } defaultDatabase)
            db.ManagedServiceDatabases.Add(defaultDatabase);

        try
        {
            await creationBilling.SaveAsync(request.WorkspaceId,
                [new(Harbora.Domain.Billing.BilledResourceType.Service,
                    service.Id, service.Name, service.InstanceSizeKey, service.ServerId)], ct);
        }
        catch (CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            return ServiceOutcome<DatabaseCreateResult>.Refused(new ServiceRefusal(ex.Message, ex.ReasonFa));
        }

        await quotaReservation.CommitAsync(ct);

        // After the commit, so the entry never outlives a creation that rolled back; before the
        // provision is queued, so a queueing failure cannot leave a created, billed database with no
        // record of who made it.
        await audit.LogAsync("database.created", "service", service.Id.ToString(), request.IpAddress,
            metadataJson: JsonSerializer.Serialize(new
            {
                name = service.Name,
                type = service.Type.ToString(),
                version = service.Version,
                environment = service.EnvironmentId,
                server = service.ServerId,
                size = service.InstanceSizeKey
            }),
            workspaceId: request.WorkspaceId, ct: ct);

        await engine.QueueProvisionAsync(service.Id, ct);

        return ServiceOutcome<DatabaseCreateResult>.Success(new DatabaseCreateResult(
            service.Id, service.Name, plan.Slug, service.ContainerName, service.Type, service.Version,
            service.EnvironmentId, service.ServerId));
    }

    public async Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveAsync(Guid serviceId, CancellationToken ct) =>
        (await CheckRemovalAsync(serviceId, ct)).Refusals;

    public async Task<ServiceOutcome<DatabaseRemoveResult>> RemoveAsync(
        Guid serviceId, bool deleteData, string? ipAddress, CancellationToken ct)
    {
        var (service, refusals) = await CheckRemovalAsync(serviceId, ct);
        if (service is null || refusals.Count > 0) return ServiceOutcome<DatabaseRemoveResult>.Refused(refusals);

        await engine.RemoveAsync(serviceId, deleteData, ct);

        // The engine returns quietly when it cannot find the row, and a removal that did not happen
        // must not be audited or reported as one.
        if (await db.ManagedServices.AsNoTracking().AnyAsync(s => s.Id == serviceId, ct))
        {
            return ServiceOutcome<DatabaseRemoveResult>.Refused(new ServiceRefusal(
                $"{service.Name} is still there after the removal; nothing was removed.",
                $"«{service.Name}» پس از حذف هنوز وجود دارد؛ چیزی حذف نشد.",
                DatabaseRefusalFields.Service));
        }

        await audit.LogAsync("database.removed", "service", serviceId.ToString(), ipAddress,
            metadataJson: JsonSerializer.Serialize(new
            {
                name = service.Name,
                type = service.Type.ToString(),
                deleteData,
                volume = deleteData ? null : service.VolumeName
            }),
            workspaceId: service.WorkspaceId, ct: ct);

        return ServiceOutcome<DatabaseRemoveResult>.Success(
            new DatabaseRemoveResult(service.Id, service.Name, service.VolumeName, deleteData));
    }

    /// <summary>
    /// The container name's stem: <c>harbora-svc-{slug}</c>. Public because a read replica is named
    /// by the same rule, from the controller that creates one.
    /// </summary>
    public static string Slugify(string name)
    {
        var slug = NonSlug().Replace(name.Trim().ToLowerInvariant(), "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "svc-" + Guid.NewGuid().ToString("N")[..6] : slug;
    }

    /// <summary>
    /// "2 apps: api, worker" — or past three, "5 apps: api, worker, cron and 2 more". The
    /// <c>ProjectsController.Delete</c> refusal idiom, in both languages because a refusal carries both.
    /// </summary>
    public static string NamedList(IReadOnlyList<string> names, bool isFa)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(isFa ? "، " : ", ", names.Take(shown)) +
              (isFa ? $" و {names.Count - shown} مورد دیگر" : $" and {names.Count - shown} more")
            : string.Join(isFa ? "، " : ", ", names);

        return isFa ? $"{names.Count} اپ: {listed}" : $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }

    private bool IsTheCaller(DatabaseCreateRequest request) =>
        currentUser.WorkspaceId == request.WorkspaceId && currentUser.UserId == request.UserId;

    /// <summary>
    /// The environment <see cref="ProjectService.ResolveEnvironmentAsync"/> would pick, found without
    /// creating anything: the requested one when it is this workspace's, otherwise the default. Null
    /// only for a workspace with no environment at all, where the create path would make one.
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

    private sealed record CreatePlan(
        ServiceCatalogEntry Entry, string Slug, string Version, InstanceSize? Size, Guid ServerId,
        long RedisMaxMemoryBytes);

    /// <summary>
    /// Every check creation makes, in the order the create form always made them. The first group is
    /// reported together, the way the form showed them; each check after it stops at its own refusal,
    /// because it depends on everything before it having passed.
    /// </summary>
    /// <param name="environment">Null only for a validate on a workspace with no environment yet.</param>
    private async Task<(CreatePlan? Plan, IReadOnlyList<ServiceRefusal> Refusals)> PlanAsync(
        DatabaseCreateRequest request, Environment? environment, CancellationToken ct)
    {
        var refusals = new List<ServiceRefusal>();

        // The form's own [Required] catches this before the panel ever gets here; the agent has no
        // model binding to do it.
        if (string.IsNullOrWhiteSpace(request.Name)) refusals.Add(NameRequired);

        // The operator's list, not the shipped one — the form offered theirs, and validating
        // against a different list refuses the option the page just showed.
        var catalog = await ServiceCatalogReader.EffectiveAsync(db, engine, ct);
        var entry = catalog.FirstOrDefault(c => c.Type == request.Type);
        if (entry is null)
        {
            refusals.Add(new ServiceRefusal("Unknown service type.", "نوع سرویس ناشناخته است.",
                nameof(DatabaseCreateRequest.Type)));
        }
        else if (!string.IsNullOrWhiteSpace(request.Version) &&
                 !entry.Versions.Contains(request.Version, StringComparer.OrdinalIgnoreCase))
        {
            refusals.Add(new ServiceRefusal(
                "The selected database version is not supported.",
                "نسخهٔ انتخاب‌شدهٔ دیتابیس پشتیبانی نمی‌شود.",
                nameof(DatabaseCreateRequest.Version)));
        }

        var check = await quota.CanAddServiceAsync(request.WorkspaceId, request.InstanceSizeKey, ct);
        if (!check.Allowed)
        {
            refusals.Add(new ServiceRefusal(
                check.Reason ?? "Plan quota exceeded.",
                check.ReasonFa ?? check.Reason ?? "سهمیهٔ پلن پر شده است."));
        }

        // A workspace with no environment yet would get its default project created by the real
        // create; no grant can name a project that does not exist, which is exactly the answer a
        // placement with no project gets.
        var placement = environment is null
            ? new ResourcePlacement(null, null)
            : new ResourcePlacement(environment.ProjectId, environment.Id);
        if (!await access.AllowsAsync(placement, Capabilities.DatabasesManage, ct))
        {
            refusals.Add(new ServiceRefusal(
                "You do not have permission to create a database in this environment.",
                "اجازهٔ ساختن دیتابیس در این محیط را ندارید.",
                nameof(DatabaseCreateRequest.EnvironmentId)));
        }

        if (refusals.Count > 0 || entry is null) return (null, refusals);

        var slug = Slugify(request.Name);
        if (await db.ManagedServices.AsNoTracking()
                .AnyAsync(s => s.WorkspaceId == request.WorkspaceId && s.ContainerName == ContainerPrefix + slug, ct))
        {
            return (null, [new ServiceRefusal(
                "A service with this name already exists.",
                "سرویسی با این نام از قبل وجود دارد.",
                nameof(DatabaseCreateRequest.Name))]);
        }

        // The chosen tier, resolved before placement because placement needs to know how much this
        // database is asking for.
        var size = string.IsNullOrWhiteSpace(request.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.AsNoTracking().FirstOrDefaultAsync(s => s.Key == request.InstanceSizeKey, ct);

        // Placed the way an application is. This once read `IsLocal` and nothing else, so a fleet
        // could have a dozen nodes and every database still landed on the control plane's own
        // machine — the panel's host filled up while the nodes it was scheduling applications onto
        // sat empty, and there was no way to say otherwise.
        var placed = request.ServerId is { } chosenServer && await db.Servers.AnyAsync(s => s.Id == chosenServer, ct)
            ? await scheduler.CheckAsync(chosenServer, size?.MemoryBytes ?? 0, size?.CpuCores ?? 0, ct)
            : await scheduler.PlaceAsync(size?.MemoryBytes ?? 0, size?.CpuCores ?? 0, null, ct);

        if (!placed.Ok)
        {
            return (null, [new ServiceRefusal(
                placed.Reason ?? "No server has capacity for this database.",
                placed.ReasonFa ?? placed.Reason ?? "هیچ سروری برای این دیتابیس ظرفیت ندارد.")]);
        }

        // 1.6 (redis-eviction): chosen at creation rather than left to arrive at whatever Redis
        // defaults to unasked — see RedisMemoryPolicy's own doc for why that default is wrong for
        // half of all users either way. Refused by name rather than clamped, the same rule the
        // Details-page change uses, so creation and the later change agree on what is allowed.
        var redisMaxMemoryBytes = 0L;
        if (request.Type == ManagedServiceType.Redis)
        {
            redisMaxMemoryBytes = request.RedisMaxMemoryMb is > 0 ? (long)request.RedisMaxMemoryMb * 1024 * 1024 : 0L;
            var containerLimit = size?.MemoryBytes ?? 0L;
            if (RedisMemoryPolicy.WhyRefused(request.RedisEvictionPolicy, redisMaxMemoryBytes, containerLimit, isFa: false)
                is { } redisRefusal)
            {
                return (null, [new ServiceRefusal(
                    redisRefusal,
                    RedisMemoryPolicy.WhyRefused(request.RedisEvictionPolicy, redisMaxMemoryBytes, containerLimit, isFa: true)
                        ?? redisRefusal,
                    nameof(DatabaseCreateRequest.RedisEvictionPolicy))]);
            }
        }

        var version = string.IsNullOrWhiteSpace(request.Version)
            ? entry.Versions[0]
            : entry.Versions.First(v => string.Equals(v, request.Version, StringComparison.OrdinalIgnoreCase));

        return (new CreatePlan(entry, slug, version, size, placed.ServerId!.Value, redisMaxMemoryBytes), []);
    }

    private async Task<(ManagedService? Service, IReadOnlyList<ServiceRefusal> Refusals)> CheckRemovalAsync(
        Guid serviceId, CancellationToken ct)
    {
        // Ownership is not the whole question: a member scoped to projects belongs to the tenant that
        // owns this database and still must not touch a project nobody put them on.
        if (!await access.CanTouchServiceAsync(serviceId, Capabilities.DatabasesManage, ct))
            return (null, [DatabaseRefusals.ServiceNotFound]);

        var service = await db.ManagedServices.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serviceId && s.WorkspaceId == currentUser.WorkspaceId, ct);
        if (service is null) return (null, [DatabaseRefusals.ServiceNotFound]);

        var refusals = new List<ServiceRefusal>();

        // C1 (2026-08-22 config-delivery plan): the AppStorageBucket/StorageController.Delete idiom
        // — a database with apps still explicitly attached is refused by name, before EF's own
        // DeleteBehavior.Restrict on AppManagedService.ManagedServiceId would ever turn this into a
        // raw constraint violation. Apps still wired the pre-2026-08-22 way (a materialized
        // EnvironmentVariable copy, no join row) are not caught here — the confirmation page's
        // advisory list still names those, but this hard refusal is authoritative only for what it
        // can enforce at the database level.
        var attachedTo = await db.AppManagedServices.AsNoTracking()
            .Where(ms => ms.ManagedServiceId == serviceId)
            .Select(ms => ms.App!.Name)
            .ToListAsync(ct);

        if (attachedTo.Count > 0)
        {
            refusals.Add(new ServiceRefusal(
                $"This database is still attached to {NamedList(attachedTo, isFa: false)}. Detach it from every app first, then delete it.",
                $"این دیتابیس هنوز به {NamedList(attachedTo, isFa: true)} متصل است. برای حذف، ابتدا آن را از همه‌ی اپ‌ها جدا کنید.",
                DatabaseRefusalFields.Service));
        }

        // 3.2 (round-2 market-gaps plan): the same idiom one relationship over — a primary with a
        // live replica must not be deleted out from under it. The ManagedService.PrimaryManagedServiceId
        // FK is Restrict at the database (HarboraDbContext) as the backstop; this is the named refusal
        // an operator actually reads, before that ever becomes a raw constraint violation.
        var replicaNames = await db.ManagedServices.AsNoTracking()
            .Where(r => r.PrimaryManagedServiceId == serviceId).Select(r => r.Name).ToListAsync(ct);

        if (replicaNames.Count > 0)
        {
            refusals.Add(new ServiceRefusal(
                $"This database still has {NamedList(replicaNames, isFa: false)} replicating from it. Delete the " +
                "replica(s) first, then delete the primary.",
                $"این دیتابیس هنوز رپلیکای {NamedList(replicaNames, isFa: true)} را دارد. برای حذف، ابتدا آن‌ها را حذف کنید.",
                DatabaseRefusalFields.Service));
        }

        return (service, refusals);
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
