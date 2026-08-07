namespace Harbora.Application.Abstractions;

/// <summary>
/// Resolves the right <see cref="IDockerEngine"/> for a server: the in-process engine for the
/// local node, or an HTTP-backed remote engine that talks to that node's agent. This is the
/// single seam that makes the whole platform multi-server without changing call sites' logic.
/// </summary>
public interface IServerEngineFactory
{
    /// <summary>
    /// The in-process engine for the local node.
    ///
    /// <para>
    /// This is the <em>same instance</em> <see cref="ResolveAsync"/> hands back for the local
    /// server — reference equality, not merely an engine that behaves identically. That is a
    /// contract, not an accident of the current implementation: callers whose work only makes sense
    /// on this machine decide by comparing what they resolved against this property. The
    /// external-access gateway publishes its port here and forwards over a private network that only
    /// exists here; a backup helper has to share this panel's staging volume. An implementation that
    /// handed out a fresh engine for the local server would tell every single-server install that its
    /// own databases live somewhere else, and would do it silently.
    /// </para>
    /// </summary>
    IDockerEngine Local { get; }

    /// <summary>Engine for the given server (local or remote agent).</summary>
    Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct);
}
