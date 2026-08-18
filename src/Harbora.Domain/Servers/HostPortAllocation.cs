using Harbora.Domain.Common;

namespace Harbora.Domain.Servers;

/// <summary>
/// A host port reserved on a remote node for one deployment of one app.
///
/// Remote nodes have no shared overlay network, so the proxy reaches an app at
/// <c>node-host:published-port</c>. The port used to be derived from a hash of the app slug and
/// deployment number, which reserved nothing: two apps could land on the same number, and because the
/// route stores host *and* port, a port later handed to a different app silently pointed one app's
/// traffic at another's container.
///
/// Recording the reservation makes "is this port free?" a fact rather than a hope, and the unique
/// index on (server, port) makes it one the database enforces even when two deploys race.
/// </summary>
public class HostPortAllocation : BaseEntity
{
    public Guid ServerId { get; set; }
    public Server? Server { get; set; }

    public int Port { get; set; }

    /// <summary>The app the port belongs to; freed when its deployments no longer need it.</summary>
    public Guid AppId { get; set; }

    /// <summary>
    /// Which deployment holds it. Old and new deployments coexist during a cutover, so a single
    /// reservation per app would fail exactly when the overlap matters.
    /// </summary>
    public int DeploymentNumber { get; set; }

    /// <summary>
    /// Which replica of the deployment this port belongs to (1-based). Zero for everything reserved
    /// before replicas existed and for a deployment running exactly one replica — the ordinary case —
    /// so every pre-existing row and every single-replica app keeps reading as it always has. Combined
    /// with <see cref="DeploymentNumber"/>, this is what lets three replicas of one deployment each
    /// hold their own port at once instead of racing to overwrite one reservation.
    /// </summary>
    public int ReplicaIndex { get; set; }

    /// <summary>
    /// The port the panel binds locally when this node's own ports can only be reached through its
    /// ingress tunnel. Null on a node the proxy can dial directly, which is the ordinary case.
    ///
    /// <para>
    /// Here rather than in a table of its own so the lifecycle is the one that already works: the
    /// pair is reserved together at deploy time, survives a restart together, and is released
    /// together — after the cutover, or at once when a deploy fails. A separate table would be a
    /// second thing to remember to free, and the first release path to forget it would leave the
    /// panel holding a listener for a container that no longer exists.
    /// </para>
    /// </summary>
    public int? IngressPort { get; set; }
}
