using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Pre-flights a rollback: the target must exist, belong to the app, be a state we can roll back to,
/// still have a retained image, and that image must still be present on the app's server.
/// Everything is checked up front so the UI can either show the user exactly what they are about to
/// restore, or explain why it is not possible — instead of failing part-way through a deploy.
/// </summary>
public sealed class RollbackPlanner(HarboraDbContext db, IServerEngineFactory engineFactory) : IRollbackPlanner
{
    public async Task<RollbackPlan> PrepareAsync(Guid appId, Guid targetDeploymentId, CancellationToken ct)
    {
        var app = await db.Apps.AsNoTracking().FirstOrDefaultAsync(a => a.Id == appId, ct);
        if (app is null) return Blocked(0, "App not found.");

        var target = await db.Deployments.AsNoTracking()
            .FirstOrDefaultAsync(d => d.Id == targetDeploymentId && d.AppId == appId, ct);
        if (target is null) return Blocked(0, "That deployment no longer exists.");

        var current = app.ActiveDeploymentId is { } activeId
            ? await db.Deployments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == activeId, ct)
            : null;

        RollbackPlan With(bool can, string? reason) => new(
            can, reason, target.Number, target.ImageTag,
            target.CommitSha, target.CommitMessage, target.CommitAuthor,
            target.FinishedAt ?? target.CreatedAt,
            current?.Number, current?.CommitSha);

        if (target.Id == app.ActiveDeploymentId)
            return With(false, $"Deployment #{target.Number} is already the live version.");

        if (target.Status is not (DeploymentStatus.Succeeded or DeploymentStatus.RolledBack))
            return With(false, $"Deployment #{target.Number} never succeeded, so there is nothing to restore.");

        if (string.IsNullOrWhiteSpace(target.ImageTag))
            return With(false, $"Deployment #{target.Number} has no retained image to roll back to.");

        // The decisive check: the artifact must still exist on the node that would run it.
        try
        {
            var docker = await engineFactory.ResolveAsync(app.ServerId, ct);
            if (!await docker.ImageExistsAsync(target.ImageTag!, ct))
                return With(false,
                    $"The image for deployment #{target.Number} is no longer on the server (most likely pruned " +
                    "by image retention). Deploy that commit from source instead.");
        }
        catch (Exception ex)
        {
            // An unreachable node is a blocker for rolling back, but say so plainly rather than
            // claiming the image is missing.
            return With(false, $"Could not reach the server to verify the image: {ex.Message}");
        }

        return With(true, null);
    }

    private static RollbackPlan Blocked(int number, string reason) =>
        new(false, reason, number, null, null, null, null, null, null, null);
}
