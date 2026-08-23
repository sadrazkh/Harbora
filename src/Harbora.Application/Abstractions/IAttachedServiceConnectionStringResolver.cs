namespace Harbora.Application.Abstractions;

/// <summary>
/// Resolves a config-override value of kind <c>AttachedServiceConnectionString</c> (C2, 2026-08-22
/// config-delivery plan) — the seam C2 depends on without waiting for C1's "attach a database to an
/// app and give it a real connection string" work to land.
///
/// <para>
/// <b>This is the interface C1 fills in.</b> C2 ships a stub (<c>NullAttachedServiceConnectionStringResolver</c>,
/// registered in <c>DependencyInjection.cs</c>) that always reports "not wired up yet" as an
/// ordinary, actionable <see cref="Harbora.Domain.Configuration.ConfigOverrideFailureReason.ServiceReferenceUnavailable"/>
/// failure — never a thrown exception, and never a silent placeholder. Once C1's attach-a-database
/// work exposes an app's attached services and their composed connection strings, its own
/// implementation replaces the stub in DI and every existing
/// <c>AttachedServiceConnectionString</c>-kind rule starts resolving for real with no change to C2's
/// own code.
/// </para>
/// </summary>
public interface IAttachedServiceConnectionStringResolver
{
    /// <summary>
    /// The connection string an attached service would hand this app right now, or a named reason it
    /// cannot: the attachment no longer exists, the service was detached, or its credentials are not
    /// ready. Never throws for an ordinary "not attached any more" — a config override rule failing
    /// this way must be exactly as debuggable as every other cause C2 names, and an exception bubbling
    /// out of a resolver is the "override failed" wall of silence the owner explicitly ruled out.
    /// </summary>
    Task<AttachedServiceConnectionStringResult> ResolveAsync(
        Guid workspaceId, Guid appId, Guid attachedServiceReferenceId, CancellationToken ct);
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
