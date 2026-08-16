using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Templates;

public sealed record TemplateDeployRequest(
    Guid WorkspaceId,
    Guid UserId,
    Guid TemplateId,
    string ProjectName,
    string ResourceName,
    string? RepositoryUrl,
    string? GitRef,
    IReadOnlyDictionary<string, string> Variables,
    bool DeployNow,

    /// <summary>
    /// The resource plan for everything this template creates — the app and the databases beside
    /// it. Optional and last so existing callers keep working; null means the platform default.
    /// </summary>
    string? InstanceSizeKey = null,

    /// <summary>
    /// The version the person picked, or null to take the recommended one. Optional and last so
    /// every existing caller keeps working unchanged.
    /// </summary>
    Guid? VersionId = null);

public sealed record TemplateDeployResult(
    Guid ProjectId,
    Guid? AppId,
    Guid? ServiceId,
    Guid? DeploymentId,
    int DependencyCount);

/// <summary>
/// Materialises a catalog entry into a real project. A stack template is not a shortcut to the app
/// form: it creates the project boundary, private environment, backing services, reference
/// variables, volumes and the deployment jobs as one product operation.
/// </summary>
public sealed class TemplateDeploymentService(
    HarboraDbContext db,
    ProjectService projects,
    IQuotaService quota,
    ISecretProtector protector,
    IManagedServiceEngine managedServices,
    IDeploymentEngine deployments,
    Billing.ResourceCreationBilling creationBilling,
    AppAddressAssigner addresses)
{
    public async Task<TemplateDeployResult> DeployAsync(TemplateDeployRequest request, CancellationToken ct)
    {
        var template = await db.AppTemplates.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct)
            ?? throw new InvalidOperationException("Template not found.");

        if (!TemplateCatalog.IsVisibleTo(template, request.WorkspaceId))
            throw new InvalidOperationException("This template is not available to this workspace.");

        if (!TemplateManifest.TryParse(template.ManifestJson, out var manifest, out var errors))
            throw new InvalidOperationException(string.Join(" ", errors));

        var dependencyTypes = manifest!.Requires.Select(ParseServiceType).ToList();
        if (manifest.Service is { Length: > 0 }) dependencyTypes.Add(ParseServiceType(manifest.Service));

        if (manifest.Source?.Equals("git", StringComparison.OrdinalIgnoreCase) == true
            && string.IsNullOrWhiteSpace(request.RepositoryUrl))
            throw new InvalidOperationException("This starter needs a Git repository URL.");

        await using var quotaReservation = await quota.AcquireCreationLockAsync(request.WorkspaceId, ct);

        if (manifest.Service is null)
        {
            var appQuota = await quota.CanAddAppAsync(request.WorkspaceId, null, null, ct);
            if (!appQuota.Allowed) throw new InvalidOperationException(appQuota.Reason ?? "Application quota exceeded.");
        }

        foreach (var _ in dependencyTypes)
        {
            var serviceQuota = await quota.CanAddServiceAsync(request.WorkspaceId, request.InstanceSizeKey, ct);
            if (!serviceQuota.Allowed) throw new InvalidOperationException(serviceQuota.Reason ?? "Service quota exceeded.");
        }

        // Resolved once and applied to everything this template creates. Without it a stack
        // template made an app with a ceiling and two databases with none.
        var size = string.IsNullOrWhiteSpace(request.InstanceSizeKey)
            ? null
            : await db.InstanceSizes.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == request.InstanceSizeKey, ct);

        var appCount = manifest.Service is null ? 1 : 0;
        var workloadCount = appCount + dependencyTypes.Count;
        var aggregateQuota = await quota.CanAddWorkloadsAsync(request.WorkspaceId,
            new WorkloadQuotaDelta(
                Apps: appCount,
                Services: dependencyTypes.Count,
                MemoryBytes: (size?.MemoryBytes ?? 0) * workloadCount,
                CpuCores: (size?.CpuCores ?? 0) * workloadCount), ct);
        if (!aggregateQuota.Allowed)
            throw new InvalidOperationException(aggregateQuota.Reason ?? "Plan quota exceeded.");

        var server = await db.Servers.Where(s => s.IsLocal)
            .Select(s => new { s.Id, s.Architecture }).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No local server is configured.");
        var serverId = server.Id;

        // Resolved here rather than trusting the list the form drew. That page was rendered a while
        // ago and a version can be withdrawn in between; somebody with an old link or a scripted
        // call asks for the id directly and never sees the list at all.
        var version = await ResolveVersionAsync(template.Id, request.VersionId, server.Architecture, ct);

        var (project, environment) = await projects.PrepareAsync(
            request.WorkspaceId,
            string.IsNullOrWhiteSpace(request.ProjectName) ? template.Name : request.ProjectName,
            request.ProjectName,
            ct);

        var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var createdServices = new List<ManagedService>();

        foreach (var (type, index) in dependencyTypes.Select((type, index) => (type, index)))
        {
            var alias = manifest.Service is not null && dependencyTypes.Count == 1
                ? manifest.Service
                : manifest.Requires[index];
            var definition = ServiceCatalog.All[type];
            var serviceSlug = ProjectService.Slugify($"{project.Slug}-{alias}");
            var password = ServiceCredentials.Generate();
            var database = definition.HasDatabaseName ? serviceSlug.Replace('-', '_') : string.Empty;
            var container = $"harbora-svc-{serviceSlug}";
            if (await db.ManagedServices.AnyAsync(s => s.ContainerName == container, ct))
                container += $"-{Guid.NewGuid():N}"[..7];

            var service = new ManagedService
            {
                WorkspaceId = request.WorkspaceId,
                EnvironmentId = environment.Id,
                ServerId = serverId,
                Name = manifest.Service is not null
                    ? request.ResourceName
                    : $"{request.ResourceName} {definition.DisplayName}",
                Type = type,
                Version = definition.Versions[0],
                Status = ServiceStatus.Provisioning,
                ContainerName = container,
                VolumeName = $"{container}-data",
                InternalPort = definition.Port,
                Username = "harbora",
                DatabaseName = database,
                EncryptedPassword = protector.Protect(password),
                InstanceSizeKey = size?.Key,
                MemoryLimitBytes = size?.MemoryBytes ?? 0,
                DiskLimitBytes = size?.DiskBytes ?? 0,
                CpuLimit = size?.CpuCores ?? 0
            };

            var credentials = new ServiceCreds(container, definition.Port, service.Username, password, database);
            foreach (var pair in TemplateReferences.For(alias, credentials)) references[pair.Key] = pair.Value;

            db.ManagedServices.Add(service);
            createdServices.Add(service);
        }

        // A managed database/cache template ends here. It still gets a real project and environment
        // so it can later be expanded with applications without moving networks.
        if (manifest.Service is not null)
        {
            await creationBilling.SaveAsync(request.WorkspaceId,
                createdServices.Select(s => new Billing.CreatedBillableResource(
                    Domain.Billing.BilledResourceType.Service, s.Id, s.Name, s.InstanceSizeKey,
                    s.ServerId)).ToList(), ct);
            foreach (var service in createdServices) await managedServices.QueueProvisionAsync(service.Id, ct);
            return new TemplateDeployResult(project.Id, null, createdServices.Single().Id, null, 0);
        }

        var appSlug = await UniqueAppSlugAsync(request.WorkspaceId, request.ResourceName, ct);
        var app = new App
        {
            WorkspaceId = request.WorkspaceId,
            EnvironmentId = environment.Id,
            ServerId = serverId,
            Name = string.IsNullOrWhiteSpace(request.ResourceName) ? template.Name : request.ResourceName.Trim(),
            Slug = appSlug,
            SourceType = AppSourceType.Template,
            TemplateId = template.Id,
            TemplateVersionId = version?.Id,

            // The version's pinned digest wins over the manifest's image. The manifest names a tag,
            // and a tag is a moving pointer: two people deploying "the same" template a month apart
            // otherwise get different software with nothing recording the difference.
            PrebuiltImage = version is null
                ? manifest.Image
                : VersionSelection.PinnedImage(version) ?? manifest.Image,
            GitRef = string.IsNullOrWhiteSpace(request.GitRef) ? "main" : request.GitRef.Trim(),
            ContainerPort = manifest.Port ?? 80,
            InstanceSizeKey = size?.Key,
            MemoryLimitBytes = size?.MemoryBytes ?? 0,
                DiskLimitBytes = size?.DiskBytes ?? 0,
            CpuLimit = size?.CpuCores ?? 0,
            HealthCheckPath = string.IsNullOrWhiteSpace(manifest.HealthPath) ? "/" : manifest.HealthPath,
            Status = AppStatus.Created
        };

        if (manifest.Source?.Equals("git", StringComparison.OrdinalIgnoreCase) == true)
        {
            var provider = new GitProvider
            {
                WorkspaceId = request.WorkspaceId,
                Name = "Custom",
                Type = GitProviderType.Custom,
                ApiBaseUrl = string.Empty
            };
            app.GitRepository = new GitRepository
            {
                Provider = provider,
                FullName = RepositoryName(request.RepositoryUrl!),
                CloneUrl = request.RepositoryUrl!.Trim(),
                DefaultBranch = app.GitRef!,
                WebhookSecret = Guid.NewGuid().ToString("N")
            };
        }

        var setup = TemplateSetup.Prepare(manifest, () => ServiceCredentials.Generate());
        foreach (var variable in setup.Variables)
        {
            var raw = variable.Value;
            if (variable.NeedsAValue)
                request.Variables.TryGetValue(variable.Key, out raw);

            if (string.IsNullOrWhiteSpace(raw) && variable.NeedsAValue)
                throw new InvalidOperationException($"{variable.Key} needs a value before this template can deploy.");

            var resolved = TemplateReferences.Resolve(raw ?? string.Empty, references, out var missing);
            if (missing.Count > 0)
                throw new InvalidOperationException($"{variable.Key} refers to an unknown service value: {string.Join(", ", missing)}.");

            app.EnvironmentVariables.Add(new EnvironmentVariable
            {
                Key = variable.Key,
                Value = variable.Secret ? protector.Protect(resolved) : resolved,
                IsSecret = variable.Secret
            });
        }

        foreach (var mount in setup.VolumeMounts)
        {
            var suffix = ProjectService.Slugify(mount.Trim('/'));
            app.Volumes.Add(new Volume
            {
                Name = $"harbora-vol-{appSlug}-{(suffix.Length == 0 ? "data" : suffix)}",
                MountPath = mount
            });
        }

        // Was a hand-built $"{appSlug}.{rootDomain}" with no kind check, no reserved-host check and no
        // collision check — three ways to hand somebody a hostname that answers nothing.
        await addresses.AssignAsync(app, requested: null, AppAddressRequestOrigin.Derived, suffix: null, ct);

        var governed = await quota.CanAddGovernedResourcesAsync(request.WorkspaceId,
            new GovernanceQuotaDelta(
                Domains: app.Domains.Count,
                Volumes: app.Volumes.Count), ct);
        if (!governed.Allowed)
            throw new InvalidOperationException(governed.Reason ?? "Plan quota exceeded.");

        db.Apps.Add(app);
        var createdResources = createdServices.Select(s => new Billing.CreatedBillableResource(
                Domain.Billing.BilledResourceType.Service, s.Id, s.Name, s.InstanceSizeKey, s.ServerId))
            .Append(new Billing.CreatedBillableResource(
                Domain.Billing.BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey, app.ServerId))
            .ToList();
        await creationBilling.SaveAsync(request.WorkspaceId, createdResources, ct);
        await quotaReservation.CommitAsync(ct);

        // The durable queue is FIFO. Dependencies are therefore provisioned before the application
        // that consumes them is built and health-checked.
        foreach (var service in createdServices) await managedServices.QueueProvisionAsync(service.Id, ct);

        Guid? deploymentId = null;
        if (request.DeployNow)
            deploymentId = await deployments.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, request.UserId, app.GitRef), ct);

        return new TemplateDeployResult(project.Id, app.Id, null, deploymentId, createdServices.Count);
    }

    /// <summary>
    /// The version to deploy, or null when this template has none and the manifest's own image is
    /// what runs.
    ///
    /// An explicit choice is checked and refused with its reason. No choice takes the recommended
    /// one — and if a template has versions but none of them is offerable, that is a refusal too,
    /// not a silent fall back to the manifest: the operator published versions precisely so the
    /// manifest's floating tag would stop being what customers get.
    /// </summary>
    private async Task<Harbora.Domain.Templates.AppTemplateVersion?> ResolveVersionAsync(
        Guid templateId, Guid? versionId, string? nodeArchitecture, CancellationToken ct)
    {
        var versions = await db.AppTemplateVersions.AsNoTracking()
            .Where(v => v.AppTemplateId == templateId)
            .ToListAsync(ct);

        if (versions.Count == 0)
        {
            // Asking for a version of a template that has none is a stale link or a typo, not a
            // reason to quietly deploy something else.
            if (versionId is not null)
                throw new InvalidOperationException("That version does not belong to this template.");
            return null;
        }

        if (versionId is { } chosen)
        {
            var version = versions.FirstOrDefault(v => v.Id == chosen)
                ?? throw new InvalidOperationException("That version does not belong to this template.");

            if (VersionSelection.Refuse(version, nodeArchitecture) is { } refusal)
                throw new InvalidOperationException(refusal.Reason);

            return version;
        }

        return VersionSelection.Default(versions, nodeArchitecture)
            ?? throw new InvalidOperationException(
                "No version of this template can be deployed on this server yet.");
    }

    public static ManagedServiceType ParseServiceType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "postgres" or "postgresql" => ManagedServiceType.PostgreSql,
        "mysql" => ManagedServiceType.MySql,
        "mariadb" or "maria-db" => ManagedServiceType.MariaDb,
        "redis" => ManagedServiceType.Redis,
        "mongo" or "mongodb" => ManagedServiceType.MongoDb,
        _ => throw new InvalidOperationException($"The template requires unsupported service \"{value}\".")
    };

    /// <summary>
    /// Platform-wide, not per-workspace: app slugs are unique across the whole platform, so this has
    /// to check every workspace's apps or two stacks named "api" deployed from two different
    /// workspaces would both derive the same slug and the second one would fail the unique index
    /// instead of quietly landing on "api-2".
    /// </summary>
    private async Task<string> UniqueAppSlugAsync(Guid workspaceId, string name, CancellationToken ct)
    {
        var basis = ProjectService.Slugify(name);
        if (basis.Length == 0) basis = "app";
        var candidate = basis;
        for (var n = 2; await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Slug == candidate, ct); n++)
            candidate = $"{basis}-{n}";
        return candidate;
    }

    private static string RepositoryName(string cloneUrl)
    {
        var value = cloneUrl.Trim().TrimEnd('/');
        var name = value[(value.LastIndexOf('/') + 1)..];
        return name.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
