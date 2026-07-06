namespace Harbora.Application.Abstractions;

/// <summary>
/// Resolves the right <see cref="IDockerEngine"/> for a server: the in-process engine for the
/// local node, or an HTTP-backed remote engine that talks to that node's agent. This is the
/// single seam that makes the whole platform multi-server without changing call sites' logic.
/// </summary>
public interface IServerEngineFactory
{
    /// <summary>The in-process engine for the local node.</summary>
    IDockerEngine Local { get; }

    /// <summary>Engine for the given server (local or remote agent).</summary>
    Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct);
}
