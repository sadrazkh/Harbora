using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Infrastructure.Proxy;

/// <summary>
/// Reads the platform's routing out of the database for <see cref="TraefikProxyEngine"/>.
///
/// <para>
/// A scope of its own because the engine that asks is a singleton and the context is scoped. That
/// also means this read never inherits the caller's ambient tenant — which is the point:
/// <c>IgnoreQueryFilters</c> below is load-bearing, not defensive. A request thread would otherwise
/// read only its own workspace's routes and a sessionless caller would read none, and either answer
/// rendered into the dynamic-config file takes other people's sites off the internet.
/// </para>
///
/// <para>
/// Ordered by id — a v7 Guid, so creation order — because Postgres has no row order to promise and
/// the file is compared, watched and reloaded. Two applies over an unchanged route set must produce
/// the same bytes rather than shuffle Traefik's routers about for nothing.
/// </para>
/// </summary>
public sealed class RouteCatalog(IServiceScopeFactory scopes) : IRouteCatalog
{
    public async Task<IReadOnlyList<Route>> AllEnabledAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        return await db.Routes
            .IgnoreQueryFilters()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Id)
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
