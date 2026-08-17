using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Looks up the Docker network name for a resource's environment.
///
/// <para>
/// The same three-line query — read the environment's slug and its project's slug, then hand both
/// to <see cref="EnvironmentNetwork.For"/> — used to be copied wherever a service needed its own
/// network: once for a deploy, once for a provision, once for a connection test. A shared home means
/// a resource's network can never be computed two different ways by two different callers, which is
/// exactly the drift a database backup, its restore and its password rotation depended on before
/// they each found their own copy of the workspace network instead.
/// </para>
///
/// <para>
/// Takes a required <see cref="Guid"/> rather than a nullable one (P2, 2026-08-17
/// app-environment-management design): <c>App.EnvironmentId</c> and <c>ManagedService.EnvironmentId</c>
/// are both required foreign keys now, so every caller already has a real id, and the FK's
/// <c>DeleteBehavior.Restrict</c> means the row it points at cannot have been removed out from under
/// it. A lookup that still finds nothing is a corrupted reference, not a legal "not placed yet" state
/// — worth throwing over, not worth a null a caller could mistake for the latter.
/// </para>
/// </summary>
public static class EnvironmentNetworkResolver
{
    public static async Task<string> ForAsync(HarboraDbContext db, Guid environmentId, CancellationToken ct)
    {
        var placement = await db.Environments
            .Where(e => e.Id == environmentId)
            .Select(e => new { e.Slug, ProjectSlug = e.Project!.Slug })
            .FirstOrDefaultAsync(ct);

        if (placement is null)
            throw new InvalidOperationException(
                $"Environment {environmentId} does not exist, but a required EnvironmentId pointed at " +
                "it. This should be impossible: the column is a required foreign key with " +
                "DeleteBehavior.Restrict, so its row cannot have been deleted while this reference " +
                "still existed.");

        return EnvironmentNetwork.For(placement.ProjectSlug, placement.Slug, environmentId);
    }
}
