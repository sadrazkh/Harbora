namespace Harbora.Application.Abstractions;

/// <summary>
/// Resolves a config-override value of kind <c>AttachedServiceConnectionString</c> (C2, 2026-08-22
/// config-delivery plan) — the seam C2 depends on without waiting for C1's "attach a database to an
/// app and give it a real connection string" work to land.
///
/// <para>
/// <b>C1 has landed and fills this in for real</b>: <c>AttachedServiceConnectionResolverAdapter</c>
/// (registered in <c>DependencyInjection.cs</c>) wraps C1's own
/// <c>Harbora.Infrastructure.Services.AttachedServiceConnectionResolver</c>, which looks up the
/// <see cref="Harbora.Domain.Services.AppManagedService"/> row on an app carrying the requested
/// alias and asks <c>ServiceCatalog</c> for its ready-to-use connection string — the same builder
/// the database's own details screen uses. Every existing <c>AttachedServiceConnectionString</c>-kind
/// rule resolves through this with no change to C2's own code.
/// </para>
/// </summary>
public interface IAttachedServiceConnectionStringResolver
{
    /// <summary>
    /// The connection string <paramref name="appId"/>'s attachment named <paramref name="alias"/>
    /// (case-insensitive — aliases are stored upper-cased, per
    /// <see cref="Harbora.Domain.Services.AppManagedServiceAlias"/>) would hand this app right now,
    /// or a named reason it cannot: no such attachment exists, or it was detached. Never throws for
    /// an ordinary "not attached any more" — a config override rule failing this way must be exactly
    /// as debuggable as every other cause C2 names, and an exception bubbling out of a resolver is
    /// the "override failed" wall of silence the owner explicitly ruled out.
    /// </summary>
    Task<AttachedServiceConnectionStringResult> ResolveAsync(Guid appId, string alias, CancellationToken ct);
}

/// <summary>
/// One resolution attempt's outcome. <paramref name="ConnectionString"/> is set only when
/// <paramref name="Found"/> is true; never logged, never redacted-and-shown — a caller that needs to
/// report a failure uses <paramref name="FailureReason"/> instead, which by construction cannot be a
/// secret.
/// </summary>
public readonly record struct AttachedServiceConnectionStringResult(
    bool Found, string? ConnectionString, string? FailureReason)
{
    public static AttachedServiceConnectionStringResult Ok(string connectionString) =>
        new(true, connectionString, null);

    public static AttachedServiceConnectionStringResult NotFound(string reason) =>
        new(false, null, reason);
}
