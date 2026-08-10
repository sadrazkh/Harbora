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
///
/// Every read here bypasses the tenant filter, and has to. This runs inside a webhook request, which
/// carries no session, so the filter resolves to "no workspace" and every query comes back empty —
/// the whole feature would report success and do nothing. The tenant is not being widened: it is
/// taken from the parent app, which the webhook's signature has already proven the caller owns.
/// </summary>
public sealed class PreviewEnvironmentService(
    HarboraDbContext db,
    IDeploymentEngine deployEngine,
    IAppOperationsService operations,
    IQuotaService quota,
    ISystemClock clock,
    Billing.ResourceCreationBilling creationBilling,
    ILogger<PreviewEnvironmentService> logger)
{
    /// <summary>
    /// Makes sure a preview of this branch exists and is up to date, and queues a deployment.
    /// Returns the deployment id, or null when nothing was done — and says why in the log.
    /// </summary>
    public async Task<Guid?> EnsureAsync(App parent, string branch, string? sha, CancellationToken ct)
    {
        if (parent.EnvironmentId is not { } parentEnvironmentId) return null;

        var projectId = await db.Environments.IgnoreQueryFilters().Where(e => e.Id == parentEnvironmentId)
            .Select(e => e.ProjectId).FirstOrDefaultAsync(ct);
        if (projectId == Guid.Empty) return null;

        var existing = await db.Apps.IgnoreQueryFilters()
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
        var preview = await db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.Id == previewAppId, ct);
        if (preview is null || preview.PreviewOfAppId is null) return;

        // Volumes go too. A preview's data is by definition throwaway, and leaving it behind is the
        // leak this method exists to prevent. Deleting the app also drops the environment it was
        // created in once that is empty — the same rule whether the branch went away or somebody
        // removed the preview from the panel, so it lives there rather than being repeated here.
        await operations.DeleteAsync(preview.Id, removeVolumes: true, ct);

        // Checked, not assumed. Deletion runs through another service that returns nothing and, on a
        // path like this one, used to find no app and return quietly — leaving the container running
        // while this line announced it was gone. A sweeper that reports removals it did not perform
        // is worse than one that fails loudly, because the leak it exists to stop goes unnoticed.
        if (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == previewAppId, ct))
            throw new InvalidOperationException(
                $"Preview '{preview.PreviewBranch}' could not be removed: the app row is still there.");

        logger.LogInformation("Removed preview {Branch}.", preview.PreviewBranch);
    }

    /// <summary>The preview of this branch, if there is one.</summary>
    public Task<App?> FindAsync(Guid parentAppId, string branch, CancellationToken ct) =>
        db.Apps.IgnoreQueryFilters().FirstOrDefaultAsync(a => a.PreviewOfAppId == parentAppId && a.PreviewBranch == branch, ct);

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

        var existing = await db.Environments.IgnoreQueryFilters()
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
        return environment;
    }

    private async Task<App> CreatePreviewAsync(App parent, Guid environmentId, string branch, CancellationToken ct)
    {
        // Read here rather than trusting the caller's Include. The webhook loads the parent to decide
        // whether it wants previews at all, so it has no reason to bring the variables along — and an
        // unloaded collection is not empty, it is unknown. Believing it produced a preview with no
        // configuration whatsoever, which starts, looks healthy, and behaves like nothing else.
        var parentVariables = await db.Set<EnvironmentVariable>().IgnoreQueryFilters()
            .Where(v => v.AppId == parent.Id).ToListAsync(ct);

        var config = PreviewPolicy.ConfigFor(parentVariables);

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
            ComposeFilePath = parent.ComposeFilePath,
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

        var rootDomain = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == Domain.Settings.SettingKeys.PlatformRootDomain)
            .Select(s => s.Value).FirstOrDefaultAsync(ct);

        // Its own address, never the parent's: a preview that answered on production's hostname
        // would be the worst possible outcome of this feature.
        if (Deployments.ServicePlan.HasPublicTraffic(parent.Kind)
            && PreviewNaming.Host(parent.Slug, branch, rootDomain) is { } host
            && !await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Host == host, ct))
        {
            preview.Domains.Add(new DomainName { Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true });
        }

        db.Apps.Add(preview);
        await creationBilling.SaveAsync(parent.WorkspaceId,
            [new Billing.CreatedBillableResource(
                Domain.Billing.BilledResourceType.App,
                preview.Id, preview.Name, preview.InstanceSizeKey)], ct);

        if (PreviewPolicy.Advice(config) is { } advice)
            logger.LogInformation("Preview {Slug}: {Advice}", preview.Slug, advice);

        return preview;
    }
}
