using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Looks up the Docker network name for a resource's environment, or null while it has none.
///
/// <para>
/// The same three-line query — read the environment's slug and its project's slug, then hand both
/// to <see cref="EnvironmentNetwork.For"/> — used to be copied wherever a service needed its own
/// network: once for a deploy, once for a provision, once for a connection test. A shared home means
/// a resource's network can never be computed two different ways by two different callers, which is
/// exactly the drift a database backup, its restore and its password rotation depended on before
/// they each found their own copy of the workspace network instead.
/// </para>
/// </summary>
public static class EnvironmentNetworkResolver
{
    public static async Task<string?> ForAsync(HarboraDbContext db, Guid? environmentId, CancellationToken ct)
    {
        if (environmentId is not { } id) return null;

        var placement = await db.Environments
            .Where(e => e.Id == id)
            .Select(e => new { e.Slug, ProjectSlug = e.Project!.Slug })
            .FirstOrDefaultAsync(ct);

        return placement is null ? null : EnvironmentNetwork.For(placement.ProjectSlug, placement.Slug, id);
    }
}
