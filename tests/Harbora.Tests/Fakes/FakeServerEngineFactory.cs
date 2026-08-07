using Harbora.Application.Abstractions;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Stands in for <see cref="IServerEngineFactory"/> so a test can say which machine holds what.
///
/// <para>
/// Asserting against this rather than against a single engine is the whole point. With one engine in
/// the test, "the backup ran on the machine that holds the data" and "the backup ran on the panel's
/// own daemon" produce identical call logs — which is exactly how a volume backup of a remote
/// service came to archive a same-named local volume and report success.
/// </para>
///
/// <para>
/// A server that was never registered resolves to the local engine, because that is what the real
/// factory does for the local server and for a target whose server row has gone away.
/// </para>
/// </summary>
public sealed class FakeServerEngineFactory(IDockerEngine local) : IServerEngineFactory
{
    private readonly Dictionary<Guid, IDockerEngine> _byServer = [];
    private readonly Dictionary<Guid, string> _unreachable = [];

    public IDockerEngine Local { get; } = local;

    /// <summary>Every server id resolution was asked for, in order.</summary>
    public List<Guid> Resolved { get; } = [];

    /// <summary>Puts an engine behind a server id.</summary>
    public FakeServerEngineFactory On(Guid serverId, IDockerEngine engine)
    {
        _byServer[serverId] = engine;
        return this;
    }

    /// <summary>
    /// A server the real factory refuses to resolve at all — no agent endpoint, no enrolled node, or
    /// a revoked one. It throws rather than handing back the local engine, and callers have to cope.
    /// </summary>
    public FakeServerEngineFactory Unreachable(Guid serverId, string reason)
    {
        _unreachable[serverId] = reason;
        return this;
    }

    public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct)
    {
        Resolved.Add(serverId);

        if (_unreachable.TryGetValue(serverId, out var reason))
            throw new InvalidOperationException(reason);

        return Task.FromResult(_byServer.TryGetValue(serverId, out var engine) ? engine : Local);
    }
}
