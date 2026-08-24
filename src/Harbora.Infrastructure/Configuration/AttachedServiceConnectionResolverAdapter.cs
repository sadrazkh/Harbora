using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// Wraps C1's <see cref="Services.AttachedServiceConnectionResolver"/> behind the seam C2 owns
/// (<see cref="IAttachedServiceConnectionStringResolver"/>). C1's own method never throws and
/// returns <c>null</c> for an alias that resolves to nothing; this adapter is the one place that
/// <c>null</c> becomes the actionable, named
/// <see cref="Harbora.Domain.Configuration.ConfigOverrideFailureReason.ServiceReferenceUnavailable"/>
/// reason C2's own failure taxonomy requires, rather than an empty string quietly written into
/// somebody's config file.
/// </summary>
public sealed class AttachedServiceConnectionResolverAdapter(Services.AttachedServiceConnectionResolver inner)
    : IAttachedServiceConnectionStringResolver
{
    public async Task<AttachedServiceConnectionStringResult> ResolveAsync(Guid appId, string alias, CancellationToken ct)
    {
        var connectionString = await inner.ResolveAsync(appId, alias, ct);
        return connectionString is null
            ? AttachedServiceConnectionStringResult.NotFound(
                $"no attachment named '{alias}' resolves a connection string for this app — it may have been detached.")
            : AttachedServiceConnectionStringResult.Ok(connectionString);
    }
}
