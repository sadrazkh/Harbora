using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// The test double for C1's seam (<see cref="IAttachedServiceConnectionStringResolver"/>) — proves
/// C2's <c>AttachedServiceConnectionString</c> value kind against a fake rather than C1's real
/// <c>AttachedServiceConnectionResolver</c> (which needs a real <see cref="Harbora.Domain.Services.AppManagedService"/>
/// row and a real service catalog entry to resolve anything). Seed a connection string for an
/// app+alias pair to simulate an attached service resolving; leave it unseeded to simulate one that
/// no longer is — the same null-means-nothing contract C1's own resolver keeps.
/// </summary>
public sealed class FakeAttachedServiceConnectionStringResolver : IAttachedServiceConnectionStringResolver
{
    private readonly Dictionary<(Guid AppId, string Alias), string> _byAppAndAlias = new();

    public FakeAttachedServiceConnectionStringResolver Seed(Guid appId, string alias, string connectionString)
    {
        _byAppAndAlias[(appId, alias.ToUpperInvariant())] = connectionString;
        return this;
    }

    public Task<AttachedServiceConnectionStringResult> ResolveAsync(Guid appId, string alias, CancellationToken ct) =>
        Task.FromResult(_byAppAndAlias.TryGetValue((appId, alias.ToUpperInvariant()), out var cs)
            ? AttachedServiceConnectionStringResult.Ok(cs)
            : AttachedServiceConnectionStringResult.NotFound(
                $"no attachment named '{alias}' resolves a connection string for this app (simulated detach)."));
}
