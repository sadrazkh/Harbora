using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// The stub C2 (2026-08-22 config-delivery plan) ships until C1's "attach a database to an app and
/// give it a real connection string" work lands and registers its own
/// <see cref="IAttachedServiceConnectionStringResolver"/> in its place — see that interface's own doc
/// for the seam contract. Every rule of value kind <c>AttachedServiceConnectionString</c> fails with
/// this reason until then: an ordinary, actionable
/// <see cref="Harbora.Domain.Configuration.ConfigOverrideFailureReason.ServiceReferenceUnavailable"/>,
/// never a thrown exception and never a value that happens to be empty.
/// </summary>
public sealed class NullAttachedServiceConnectionStringResolver : IAttachedServiceConnectionStringResolver
{
    public Task<AttachedServiceConnectionStringResult> ResolveAsync(
        Guid workspaceId, Guid appId, Guid attachedServiceReferenceId, CancellationToken ct) =>
        Task.FromResult(AttachedServiceConnectionStringResult.NotFound(
            "connecting a config override to an attached service's connection string is not wired up " +
            "on this install yet."));
}
