using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// The test double for C1's seam (<see cref="IAttachedServiceConnectionStringResolver"/>) — proves
/// C2's <c>AttachedServiceConnectionString</c> value kind end to end without C1's own attach-a-database
/// work having landed. Seed a connection string for a reference id to simulate an attached service
/// resolving; leave it unseeded to simulate one that no longer is.
/// </summary>
public sealed class FakeAttachedServiceConnectionStringResolver : IAttachedServiceConnectionStringResolver
{
    private readonly Dictionary<Guid, string> _byReference = new();

    public FakeAttachedServiceConnectionStringResolver Seed(Guid referenceId, string connectionString)
    {
        _byReference[referenceId] = connectionString;
        return this;
    }

    public Task<AttachedServiceConnectionStringResult> ResolveAsync(
        Guid workspaceId, Guid appId, Guid attachedServiceReferenceId, CancellationToken ct) =>
        Task.FromResult(_byReference.TryGetValue(attachedServiceReferenceId, out var cs)
            ? AttachedServiceConnectionStringResult.Ok(cs)
            : AttachedServiceConnectionStringResult.NotFound(
                $"no attached service resolves reference {attachedServiceReferenceId} (simulated detach)."));
}
