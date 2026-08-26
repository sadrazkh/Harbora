using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The seam a file override (C2, 2026-08-22 config-delivery plan) binds to when its value is "an
/// attached service's connection string" rather than a literal or a secret: "give me the connection
/// string for this app's attachment named X."
///
/// <para>
/// Deliberately thin. It does not compose anything itself — it looks up which
/// <see cref="Harbora.Domain.Services.AppManagedService"/> row on this app carries the requested
/// <see cref="Harbora.Domain.Services.AppManagedService.Alias"/>, then asks
/// <see cref="ServiceCatalog.All"/>'s own <c>Conn</c> function for the one connection string that
/// engine's details screen already shows — the same builder <c>ManagedServiceEngine.GetConnectionInfoAsync</c>
/// calls, reused rather than re-composed a third way. A C2 override that names an alias no longer
/// attached, or an app with no such attachment, gets <c>null</c> back — C2's own contract is to turn
/// that into the named, actionable refusal the config-delivery plan requires ("value reference
/// pointing at a service no longer attached"), not to guess.
/// </para>
/// </summary>
public sealed class AttachedServiceConnectionResolver(HarboraDbContext db, ISecretProtector protector)
{
    /// <summary>
    /// The ready-to-use connection string for <paramref name="appId"/>'s attachment named
    /// <paramref name="alias"/> (case-insensitive — aliases are stored upper-cased by
    /// <see cref="Harbora.Domain.Services.AppManagedServiceAlias"/>), or <c>null</c> when no such
    /// attachment exists. Never throws for a missing alias or app — a missing value is exactly what a
    /// caller building a "the reference points at nothing" error needs to detect.
    /// </summary>
    public async Task<string?> ResolveAsync(Guid appId, string alias, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        var link = await db.AppManagedServices.AsNoTracking()
            .Include(x => x.ManagedService)
            .Include(x => x.Database)
            .FirstOrDefaultAsync(x => x.AppId == appId
                && x.ManagedService != null
                && x.Alias.ToUpper() == alias.ToUpper(), ct);

        if (link?.ManagedService is not { } svc) return null;

        var definition = ServiceCatalog.All[svc.Type];
        var creds = AttachedDatabaseCreds.Resolve(svc, link.Database, protector);
        return definition.Conn(creds).Full;
    }
}
