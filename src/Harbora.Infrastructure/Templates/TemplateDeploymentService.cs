using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Git;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
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
    bool DeployNow);

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
    IDeploymentEngine deployments)
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

        if (manifest.Service is null)
        {
            var appQuota = await quota.CanAddAppAsync(request.WorkspaceId, null, null, ct);
            if (!appQuota.Allowed) throw new InvalidOperationException(appQuota.Reason ?? "Application quota exceeded.");
        }

        foreach (var _ in dependencyTypes)
        {
            var serviceQuota = await quota.CanAddServiceAsync(request.WorkspaceId, ct);
            if (!serviceQuota.Allowed) throw new InvalidOperationException(serviceQuota.Reason ?? "Service quota exceeded.");
        }

        var serverId = await db.Servers.Where(s => s.IsLocal).Select(s => (Guid?)s.Id).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("No local server is configured.");

        var (project, environment) = await projects.CreateAsync(
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
                EncryptedPassword = protector.Protect(password)
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
            await db.SaveChangesAsync(ct);
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
            PrebuiltImage = manifest.Image,
            GitRef = string.IsNullOrWhiteSpace(request.GitRef) ? "main" : request.GitRef.Trim(),
            ContainerPort = manifest.Port ?? 80,
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

        var rootDomain = await db.Settings
            .Where(s => s.Key == Harbora.Domain.Settings.SettingKeys.PlatformRootDomain)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        if (!string.IsNullOrWhiteSpace(rootDomain) && !rootDomain.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            app.Domains.Add(new DomainName
            {
                Host = $"{appSlug}.{rootDomain}",
                SslEnabled = true,
                ForceHttps = true,
                IsPrimary = true
            });

        db.Apps.Add(app);
        await db.SaveChangesAsync(ct);

        // The durable queue is FIFO. Dependencies are therefore provisioned before the application
        // that consumes them is built and health-checked.
        foreach (var service in createdServices) await managedServices.QueueProvisionAsync(service.Id, ct);

        Guid? deploymentId = null;
        if (request.DeployNow)
            deploymentId = await deployments.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Manual, request.UserId, app.GitRef), ct);

        return new TemplateDeployResult(project.Id, app.Id, null, deploymentId, createdServices.Count);
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

    private async Task<string> UniqueAppSlugAsync(Guid workspaceId, string name, CancellationToken ct)
    {
        var basis = ProjectService.Slugify(name);
        if (basis.Length == 0) basis = "app";
        var candidate = basis;
        for (var n = 2; await db.Apps.AnyAsync(a => a.WorkspaceId == workspaceId && a.Slug == candidate, ct); n++)
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
