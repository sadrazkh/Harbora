using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// Creates and removes the environment a branch gets to itself.
///
/// A preview is a real service in a real environment — its own private network, its own address, its
/// own deployment history — created from the parent's configuration minus its secrets, and taken
/// away again when the branch goes. The two halves have to be equally reliable: an environment that
/// is created automatically and removed by hand is a slow leak of somebody's quota.
/// </summary>
public sealed class PreviewEnvironmentService(
    HarboraDbContext db,
    IDeploymentEngine deployEngine,
    IAppOperationsService operations,
    IQuotaService quota,
    ISystemClock clock,
    ILogger<PreviewEnvironmentService> logger)
{
    /// <summary>
    /// Makes sure a preview of this branch exists and is up to date, and queues a deployment.
    /// Returns the deployment id, or null when nothing was done — and says why in the log.
    /// </summary>
    public async Task<Guid?> EnsureAsync(App parent, string branch, string? sha, CancellationToken ct)
    {
        if (parent.EnvironmentId is not { } parentEnvironmentId) return null;

        var projectId = await db.Environments.Where(e => e.Id == parentEnvironmentId)
            .Select(e => e.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;

        var existing = await db.Apps
            .FirstOrDefaultAsync(a => a.PreviewOfAppId == parent.Id && a.PreviewBranch == branch, ct);

        if (existing is not null)
        {
            // Already there: this is the second push to the branch, so only the clock moves.
            existing.PreviewLastPushedAt = clock.UtcNow;
            await db.SaveChangesAsync(ct);

            return await deployEngine.QueueDeploymentAsync(
                new DeploymentRequest(existing.Id, DeploymentTrigger.Webhook, Guid.Empty, branch, sha), ct);
        }

        // A preview costs the same as any other service, so it is subject to the same plan. Checked
        // before anything is created: a half-made preview is worse than none.
        var allowed = await quota.CanAddAppAsync(parent.WorkspaceId, parent.InstanceSizeKey, null, ct);
        if (!allowed.Allowed)
        {
            logger.LogInformation("No preview for {Branch}: {Reason}", branch, allowed.Reason);
            return null;
        }

        var environment = await EnsureEnvironmentAsync(parent, projectId, branch, ct);
        var preview = await CreatePreviewAsync(parent, environment.Id, branch, ct);

        return await deployEngine.QueueDeploymentAsync(
            new DeploymentRequest(preview.Id, DeploymentTrigger.Webhook, Guid.Empty, branch, sha), ct);
    }

    /// <summary>
    /// Removes the preview of a branch: its containers, its volumes, its environment. Called when
    /// the branch is deleted, and by the sweeper when one goes quiet.
    /// </summary>
    public async Task RemoveAsync(Guid previewAppId, CancellationToken ct)
    {
        var preview = await db.Apps.FirstOrDefaultAsync(a => a.Id == previewAppId, ct);
        if (preview is null || preview.PreviewOfAppId is null) return;

        var environmentId = preview.EnvironmentId;

        // Volumes go too. A preview's data is by definition throwaway, and leaving it behind is the
        // leak this method exists to prevent.
        await operations.DeleteAsync(preview.Id, removeVolumes: true, ct);

        // The environment is removed only once nothing is left in it — a preview environment holds
        // one service, but a person may have added another and it is not ours to delete.
        if (environmentId is { } id
            && !await db.Apps.AnyAsync(a => a.EnvironmentId == id, ct)
            && !await db.ManagedServices.AnyAsync(s => s.EnvironmentId == id, ct))
        {
            var environment = await db.Environments.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (environment is not null && !environment.IsDefault)
            {
                db.Environments.Remove(environment);
                await db.SaveChangesAsync(ct);
            }
        }

        logger.LogInformation("Removed preview {Branch}.", preview.PreviewBranch);
    }

    /// <summary>The preview of this branch, if there is one.</summary>
    public Task<App?> FindAsync(Guid parentAppId, string branch, CancellationToken ct) =>
        db.Apps.FirstOrDefaultAsync(a => a.PreviewOfAppId == parentAppId && a.PreviewBranch == branch, ct);

    /// <summary>Previews that have gone quiet for longer than the policy allows.</summary>
    public async Task<IReadOnlyList<App>> ExpiredAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var previews = await db.Apps.IgnoreQueryFilters()
            .Where(a => a.PreviewOfAppId != null)
            .ToListAsync(ct);

        return previews
            .Where(p => PreviewPolicy.HasExpired(p.PreviewLastPushedAt ?? p.CreatedAt, now))
            .ToList();
    }

    private async Task<Domain.Projects.Environment> EnsureEnvironmentAsync(
        App parent, Guid projectId, string branch, CancellationToken ct)
    {
        var slug = PreviewNaming.Slug(branch);

        var existing = await db.Environments
            .FirstOrDefaultAsync(e => e.ProjectId == projectId && e.Slug == slug, ct);
        if (existing is not null) return existing;

        var environment = new Domain.Projects.Environment
        {
            WorkspaceId = parent.WorkspaceId,
            ProjectId = projectId,
            Name = PreviewNaming.EnvironmentName(branch),
            Slug = slug
        };

        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
        return environment;
    }

    private async Task<App> CreatePreviewAsync(App parent, Guid environmentId, string branch, CancellationToken ct)
    {
        var config = PreviewPolicy.ConfigFor(parent.EnvironmentVariables);

        var preview = new App
        {
            WorkspaceId = parent.WorkspaceId,
            EnvironmentId = environmentId,
            ServerId = parent.ServerId,
            Name = $"{parent.Name} · {branch}",
            Slug = PreviewNaming.Slug($"{parent.Slug}-{branch}"),
            Kind = parent.Kind,
            SourceType = parent.SourceType,
            PrebuiltImage = parent.PrebuiltImage,
            DockerfilePath = parent.DockerfilePath,
            ContainerPort = parent.ContainerPort,
            HealthCheckPath = parent.HealthCheckPath,
            InstanceSizeKey = parent.InstanceSizeKey,
            MemoryLimitBytes = parent.MemoryLimitBytes,
            CpuLimit = parent.CpuLimit,
            GitRepositoryId = parent.GitRepositoryId,
            GitRef = branch,

            PreviewOfAppId = parent.Id,
            PreviewBranch = branch,
            PreviewLastPushedAt = clock.UtcNow
        };

        // Everything except the secrets — see PreviewPolicy for why.
        foreach (var (key, value) in config.Copied)
            preview.EnvironmentVariables.Add(new EnvironmentVariable { Key = key, Value = value, IsSecret = false });

        var rootDomain = await db.Settings
            .Where(s => s.Key == Domain.Settings.SettingKeys.PlatformRootDomain)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        // Its own address, never the parent's: a preview that answered on production's hostname
        // would be the worst possible outcome of this feature.
        if (Deployments.ServicePlan.HasPublicTraffic(parent.Kind)
            && PreviewNaming.Host(parent.Slug, branch, rootDomain) is { } host
            && !await db.Domains.AnyAsync(d => d.Host == host, ct))
        {
            preview.Domains.Add(new DomainName { Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true });
        }

        db.Apps.Add(preview);
        await db.SaveChangesAsync(ct);

        if (PreviewPolicy.Advice(config) is { } advice)
            logger.LogInformation("Preview {Slug}: {Advice}", preview.Slug, advice);

        return preview;
    }
}
