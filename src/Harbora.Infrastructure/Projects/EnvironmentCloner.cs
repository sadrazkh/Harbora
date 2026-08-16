using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Projects;

/// <summary>What a clone attempt produced, in terms a page can act on.</summary>
public sealed record CloneOutcome(bool Ok, string Reason, Guid? EnvironmentId, ClonePlan? Plan)
{
    public static CloneOutcome Refused(string reason, ClonePlan? plan = null) => new(false, reason, null, plan);
    public static CloneOutcome Created(Guid id, ClonePlan plan) => new(true, "", id, plan);
}

/// <summary>
/// Copying an environment: production's shape, without production's data or its traffic.
///
/// Making staging by hand means recreating a dozen services and getting one of them subtly wrong —
/// usually the one whose password nobody changed, which is how a staging deploy ends up writing to
/// production's database. So the two things this does most carefully are: every managed service
/// gets a <b>new password</b>, and every variable an attach owns is rewritten against the copy
/// rather than carried over.
///
/// <see cref="ClonePlan"/> decides every name first, and the whole package is quota-checked as one
/// thing — half a copy is worse than none, and finding out at the eleventh service that the plan
/// only had room for ten is exactly how half a copy happens.
///
/// Everything is written in a single <c>SaveChanges</c>, so a failure part-way leaves nothing
/// behind to clean up. That is deliberate in place of a compensating-delete path, which is itself
/// code that can fail at the moment things are already going wrong.
/// </summary>
public sealed class EnvironmentCloner(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    IQuotaService quota,
    ISchedulerService scheduler,
    ISecretProtector protector,
    ISystemClock clock,
    Billing.ResourceCreationBilling creationBilling,
    ILogger<EnvironmentCloner> log,
    AppAddressAssigner addresses)
{
    /// <summary>
    /// Works out what copying <paramref name="sourceEnvironmentId"/> would create, without creating
    /// it. The confirmation screen shows this, so nobody presses the button on a guess.
    /// </summary>
    public async Task<ClonePlan?> PlanAsync(
        Guid workspaceId, Guid sourceEnvironmentId, string desiredName, CancellationToken ct)
    {
        var source = await db.Environments
            .FirstOrDefaultAsync(e => e.Id == sourceEnvironmentId && e.WorkspaceId == workspaceId, ct);
        if (source is null) return null;

        var apps = await db.Apps
            .Where(a => a.EnvironmentId == source.Id)
            .Select(a => new
            {
                a.Id, a.Name, a.Slug, a.InstanceSizeKey, a.MemoryLimitBytes, a.CpuLimit, a.Kind,
                Domains = a.Domains.Count,
                Volumes = a.Volumes.Select(v => new { v.MountPath, v.ReadOnly, v.SizeLimitBytes }).ToList()
            })
            .OrderBy(a => a.Slug)
            .ToListAsync(ct);

        var services = await db.ManagedServices
            .Where(s => s.EnvironmentId == source.Id)
            .Select(s => new
            {
                s.Id, s.Name, s.Type, s.InstanceSizeKey, s.MemoryLimitBytes, s.CpuLimit, s.DatabaseName
            })
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

        var takenEnvironmentSlugs = await db.Environments
            .Where(e => e.ProjectId == source.ProjectId).Select(e => e.Slug).ToListAsync(ct);
        // Platform-wide, not just this workspace's: app slugs are unique across every workspace now
        // (HarboraDbContext: HasIndex(x => x.Slug).IsUnique()), and ClonePlan.Of dedupes the copy's
        // slug against exactly this set — a workspace-scoped set would let a clone land on a slug
        // another workspace already holds and fail the unique index instead of picking "-2".
        var takenAppSlugs = await db.Apps
            .IgnoreQueryFilters().Select(a => a.Slug).ToListAsync(ct);
        var takenContainers = await db.ManagedServices
            .Where(s => s.WorkspaceId == workspaceId).Select(s => s.ContainerName).ToListAsync(ct);

        return ClonePlan.Of(new CloneRequest(
            desiredName,
            source.ProjectId,
            takenEnvironmentSlugs,
            takenAppSlugs,
            takenContainers,
            apps.Select(a => new CloneSourceApp(
                a.Id, a.Name, a.Slug, a.InstanceSizeKey, a.MemoryLimitBytes, a.CpuLimit, a.Domains, a.Kind,
                a.Volumes.Select(v => new CloneSourceVolume(v.MountPath, v.ReadOnly, v.SizeLimitBytes))
                    .ToList())).ToList(),
            services.Select(s => new CloneSourceService(
                s.Id, s.Name, s.InstanceSizeKey, s.MemoryLimitBytes, s.CpuLimit,
                // Whether this engine names a database is already recorded on the row: a service
                // created without one has an empty name, and inventing one for the copy would give
                // it a database its engine does not have.
                !string.IsNullOrEmpty(s.DatabaseName))).ToList()));
    }

    public async Task<CloneOutcome> CloneAsync(
        Guid workspaceId, Guid sourceEnvironmentId, string desiredName, CancellationToken ct)
    {
        // Ownership is checked once, inside PlanAsync, which refuses an environment this workspace
        // does not own by returning null. A second check here would be a second place for the two
        // to disagree, and only one of them would be the one that ran.
        var plan = await PlanAsync(workspaceId, sourceEnvironmentId, desiredName, ct);
        if (plan is null) return CloneOutcome.Refused("That environment is not available.");

        if (plan.ResourceCount == 0)
            return CloneOutcome.Refused("There is nothing in that environment to copy.", plan);

        await using var quotaReservation = await quota.AcquireCreationLockAsync(workspaceId, ct);

        if (await QuotaRefusalAsync(workspaceId, plan, ct) is { } refusal)
            return CloneOutcome.Refused(refusal, plan);

        // Placement for the whole package, asked for once per item so a node that fills up half way
        // stops the copy before any of it is written.
        var placements = new Dictionary<Guid, Guid>();
        foreach (var app in plan.Apps)
        {
            var placed = await scheduler.PlaceAsync(app.MemoryLimitBytes, app.CpuLimit, null, ct);
            if (!placed.Ok || placed.ServerId is not { } server)
                return CloneOutcome.Refused(placed.Reason ?? "No server has capacity for this copy.", plan);
            placements[app.SourceId] = server;
        }
        foreach (var service in plan.Services)
        {
            var placed = await scheduler.PlaceAsync(service.MemoryLimitBytes, service.CpuLimit, null, ct);
            if (!placed.Ok || placed.ServerId is not { } server)
                return CloneOutcome.Refused(placed.Reason ?? "No server has capacity for this copy.", plan);
            placements[service.SourceId] = server;
        }

        var now = clock.UtcNow;

        var environment = new Environment
        {
            WorkspaceId = workspaceId,
            ProjectId = plan.ProjectId,
            Name = plan.EnvironmentName,
            Slug = plan.EnvironmentSlug,
            // Never the default and never protected: a copy inherits neither of the two flags that
            // say "this one is the real one".
            IsDefault = false,
            IsProtected = false,
            CreatedAt = now
        };
        db.Environments.Add(environment);

        // --- services first: the applications' variables are rewritten against them ---
        var created = new List<(ManagedService Row, string Name)>();
        foreach (var spec in plan.Services)
        {
            var origin = await db.ManagedServices.FirstAsync(s => s.Id == spec.SourceId, ct);

            var copy = new ManagedService
            {
                WorkspaceId = workspaceId,
                Environment = environment,
                ServerId = placements[spec.SourceId],
                Name = spec.Name,
                Type = origin.Type,
                Version = origin.Version,
                Status = ServiceStatus.Provisioning,
                ContainerName = spec.ContainerName,
                VolumeName = spec.VolumeName,
                DatabaseName = spec.DatabaseName,
                InternalPort = origin.InternalPort,
                Username = origin.Username,
                TlsEnabled = origin.TlsEnabled,
                InstanceSizeKey = spec.InstanceSizeKey,
                MemoryLimitBytes = spec.MemoryLimitBytes,
                DiskLimitBytes = origin.DiskLimitBytes,
                CpuLimit = spec.CpuLimit,
                // A fresh one, always. Copying the password would make the copy able to reach the
                // original's database with the original's credentials, which is the failure this
                // whole feature would otherwise ship with.
                EncryptedPassword = protector.Protect(Harbora.Infrastructure.Services.ServiceCredentials.Generate()),
                CreatedAt = now
            };

            // Deliberately not copied: StorageBytes/StorageMeasuredAt and RunningImage. They
            // describe the original's container, and a copy that has never run must not report a
            // measured size or an image it is not running.

            db.ManagedServices.Add(copy);
            created.Add((copy, spec.Name));
        }

        // Which variables an attach owns, so they are not carried over stale. Read from the source
        // services, since the copies do not exist yet and write the same names.
        var attachKeys = new List<(string Name, IReadOnlyCollection<string> Keys)>();
        foreach (var spec in plan.Services)
        {
            try
            {
                var wanted = await engine.BuildAttachEnvAsync(spec.SourceId, ct);
                attachKeys.Add((spec.Name, wanted.Keys.ToList()));
            }
            catch (Exception e)
            {
                // If we cannot tell which names belong to the attach, carrying every variable over
                // would hand the copy the original's credentials. Refusing is the safe direction.
                log.LogWarning(e, "The attach variables of service {Service} could not be read.", spec.SourceId);
                return CloneOutcome.Refused(
                    "The database connection settings of the original could not be read, so the copy was not made.",
                    plan);
            }
        }
        var owned = ClonePlan.AttachOwnedKeys(attachKeys);

        // --- then the applications ---
        //
        // Which services each one was attached to is read from the ORIGINAL's variables, here,
        // before they are filtered. Asking the copy afterwards answers "attached to nothing" every
        // time — the keys that prove the attach are precisely the ones just left out — and the
        // copy comes up with no database configured at all.
        var attachments = new List<(App Copy, List<string> ServiceNames)>();

        foreach (var spec in plan.Apps)
        {
            var origin = await db.Apps
                .Include(a => a.EnvironmentVariables)
                .Include(a => a.Volumes)
                .FirstAsync(a => a.Id == spec.SourceId, ct);

            var copy = new App
            {
                WorkspaceId = workspaceId,
                Environment = environment,
                ServerId = placements[spec.SourceId],
                Name = origin.Name,
                Slug = spec.Slug,
                Kind = origin.Kind,
                SourceType = origin.SourceType,
                Status = AppStatus.Created,
                ReleaseCommand = origin.ReleaseCommand,
                CronExpression = origin.CronExpression,
                Command = origin.Command,
                GitRepositoryId = origin.GitRepositoryId,
                GitRef = origin.GitRef,
                AutoDeployOnPush = origin.AutoDeployOnPush,
                DeployOnTagPattern = origin.DeployOnTagPattern,
                DockerfilePath = origin.DockerfilePath,
                ComposeFilePath = origin.ComposeFilePath,
                BuildContextPath = origin.BuildContextPath,
                BuildCommand = origin.BuildCommand,
                PrebuiltImage = origin.PrebuiltImage,
                ContainerPort = origin.ContainerPort,
                DesiredReplicas = origin.DesiredReplicas,
                HealthCheckPath = origin.HealthCheckPath,
                InstanceSizeKey = spec.InstanceSizeKey,
                MemoryLimitBytes = spec.MemoryLimitBytes,
                DiskLimitBytes = origin.DiskLimitBytes,
                CpuLimit = spec.CpuLimit,
                TemplateId = origin.TemplateId,
                TemplateVersionId = origin.TemplateVersionId,
                CreatedAt = now

                // Deliberately not copied: PreviewsEnabled and the preview columns (a copy of a
                // service should not start spawning environments of its own), ActiveDeploymentId
                // and NextRunAt (they name the original's history), and PublishedHostPort (it names
                // a port on the original's node).
            };

            attachments.Add((copy, plan.Services
                .Where(s => ClonePlan.IsAttachedTo(
                    origin.EnvironmentVariables.Select(v => v.Key), s.Name))
                .Select(s => s.Name)
                .ToList()));

            foreach (var variable in origin.EnvironmentVariables)
            {
                if (owned.Contains(variable.Key)) continue;

                copy.EnvironmentVariables.Add(new EnvironmentVariable
                {
                    Key = variable.Key,
                    Value = variable.Value,
                    IsSecret = variable.IsSecret,
                    AvailableAtBuild = variable.AvailableAtBuild,
                    CreatedAt = now
                });
            }

            foreach (var volume in spec.Volumes)
            {
                var originVolume = origin.Volumes.First(v => v.MountPath == volume.MountPath);

                copy.Volumes.Add(new Volume
                {
                    Name = volume.Name,
                    MountPath = volume.MountPath,
                    ReadOnly = originVolume.ReadOnly,
                    SizeLimitBytes = originVolume.SizeLimitBytes,
                    // Empty, and with no measurement. The copy's volume is a new docker volume with
                    // nothing in it; reporting the original's size would be a number about somebody
                    // else's data.
                    CreatedAt = now
                });
            }

            // A cloned app used to arrive with no address at all — the one creation path that had no
            // rule rather than a wrong one. Its slug differs from the original's (spec.Slug), so this
            // does not contend with the app it was copied from.
            await addresses.AssignAsync(copy, requested: null, AppAddressRequestOrigin.Derived, suffix: null, ct);

            db.Apps.Add(copy);
        }

        var billable = created.Select(x => new Billing.CreatedBillableResource(
                Domain.Billing.BilledResourceType.Service,
                x.Row.Id, x.Row.Name, x.Row.InstanceSizeKey, x.Row.ServerId))
            .Concat(attachments.Select(x => new Billing.CreatedBillableResource(
                Domain.Billing.BilledResourceType.App,
                x.Copy.Id, x.Copy.Name, x.Copy.InstanceSizeKey, x.Copy.ServerId)))
            .ToList();
        try
        {
            await creationBilling.SaveAsync(workspaceId, billable, ct);
        }
        catch (Billing.CreationPaymentRequiredException ex)
        {
            db.ChangeTracker.Clear();
            var reason = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa"
                ? ex.ReasonFa
                : ex.Message;
            return CloneOutcome.Refused(reason, plan);
        }

        await quotaReservation.CommitAsync(ct);

        // Only now, with rows that exist: provisioning a database whose row was rolled back leaves
        // a container nothing owns.
        foreach (var (row, _) in created)
            await engine.QueueProvisionAsync(row.Id, ct);

        // The attach is re-run against the copies, so the copy's applications get the copy's
        // hostnames and the copy's new passwords.
        await ReattachAsync(created, attachments, ct);

        return CloneOutcome.Created(environment.Id, plan);
    }

    /// <summary>
    /// Rewrites the attach variables inside the new environment, matching each copied application
    /// to the copies of the services it was attached to in the original.
    /// </summary>
    private async Task ReattachAsync(
        IReadOnlyList<(ManagedService Row, string Name)> created,
        IReadOnlyList<(App Copy, List<string> ServiceNames)> attachments,
        CancellationToken ct)
    {
        foreach (var (row, name) in created)
        {
            var wants = attachments.Where(a => a.ServiceNames.Contains(name)).Select(a => a.Copy).ToList();
            if (wants.Count == 0) continue;

            IReadOnlyDictionary<string, string> wanted;
            try { wanted = await engine.BuildAttachEnvAsync(row.Id, ct); }
            catch (Exception e)
            {
                // The copy exists and the page says so; a variable that could not be written shows
                // up as a missing setting on the next deploy, which is a far better failure than
                // one invented to fill the gap.
                log.LogWarning(e, "The copy of service {Service} could not be attached.", row.Id);
                continue;
            }

            foreach (var app in wants)
            {
                // Nothing in the copy holds the shared names yet — they were left out of the
                // copy for exactly this reason — so the first service to be attached claims them,
                // the same order the original's first attach took.
                var existing = app.EnvironmentVariables.ToDictionary(
                    v => v.Key, v => (string?)null, StringComparer.Ordinal);
                var final = Harbora.Infrastructure.Services.AttachKeys.For(wanted, existing, name);

                foreach (var (key, value) in final)
                {
                    var variable = app.EnvironmentVariables.FirstOrDefault(v => v.Key == key);
                    if (variable is null)
                    {
                        app.EnvironmentVariables.Add(new EnvironmentVariable
                        {
                            Key = key, Value = protector.Protect(value), IsSecret = true,
                            CreatedAt = clock.UtcNow
                        });
                    }
                    else
                    {
                        variable.Value = protector.Protect(value);
                        variable.IsSecret = true;
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The plan quota, answered for the whole package rather than item by item.
    ///
    /// Asking per item would let a copy get eleven services into a ten-service plan by asking eleven
    /// separate questions, each of which was true when it was asked.
    /// </summary>
    private async Task<string?> QuotaRefusalAsync(Guid workspaceId, ClonePlan plan, CancellationToken ct)
    {
        // No root domain configured means AssignAsync hands out NoRootDomain to every copy regardless
        // of kind, so none of them consumes a domain — counting one per addressable copy anyway would
        // refuse a clone for a limit it would not actually have touched.
        var rootDomain = await addresses.RootDomainAsync(ct);
        var governed = await quota.CanAddGovernedResourcesAsync(workspaceId,
            new GovernanceQuotaDelta(
                Environments: 1,
                // One address per copy that can have one. App copies get an address now — before that
                // they arrived with none, so leaving domains out of this estimate was correct then and
                // lets a workspace clone straight past its domain limit today. Counted here with the
                // rest rather than asked per app, for the reason this method's own docstring gives.
                Domains: string.IsNullOrWhiteSpace(rootDomain)
                    ? 0
                    : plan.Apps.Count(a => Deployments.ServicePlan.CanHaveDomains(a.Kind)),
                Volumes: plan.Apps.Sum(a => a.Volumes.Count)), ct);
        if (!governed.Allowed) return governed.Reason;

        // A suspended workspace, or one whose size keys are not allowed on its plan, is refused by
        // the ordinary single-resource check — asked once here so the reason it gives is the reason
        // shown, rather than a sentence written twice.
        foreach (var app in plan.Apps)
        {
            var check = await quota.CanAddAppAsync(workspaceId, app.InstanceSizeKey, null, ct);
            if (!check.Allowed) return check.Reason;
        }
        foreach (var service in plan.Services)
        {
            var check = await quota.CanAddServiceAsync(workspaceId, service.InstanceSizeKey, ct);
            if (!check.Allowed) return check.Reason;
        }

        var sourceAppIds = plan.Apps.Select(a => a.SourceId).ToList();
        var cronJobs = await db.Apps.AsNoTracking()
            .CountAsync(a => sourceAppIds.Contains(a.Id) && a.Kind == ServiceKind.Cron, ct);
        var aggregate = await quota.CanAddWorkloadsAsync(workspaceId,
            new WorkloadQuotaDelta(
                Apps: plan.Apps.Count,
                Services: plan.Services.Count,
                MemoryBytes: plan.MemoryBytes,
                CpuCores: plan.CpuCores,
                CronJobs: cronJobs), ct);
        if (!aggregate.Allowed) return aggregate.Reason;

        return null;
    }
}
