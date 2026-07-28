using Harbora.Domain.Common;
using Harbora.Domain.Apps;

namespace Harbora.Domain.Deployments;

/// <summary>
/// A single build+release attempt for an <see cref="App"/>. Immutable history: rollback
/// creates a new Deployment that re-releases a prior image rather than mutating a row.
/// </summary>
public class Deployment : BaseEntity
{
    public Guid AppId { get; set; }

    /// <summary>
    /// Denormalised from the owning app so tenant isolation can be a direct comparison.
    /// Filtering through the <c>App</c> navigation instead would make EF emit an inner join —
    /// which silently hides any deployment whose app row is missing, including from the crash
    /// reconciler that exists precisely to find stranded deployments.
    /// </summary>
    public Guid WorkspaceId { get; set; }
    public App? App { get; set; }

    /// <summary>Monotonic per-app build number shown in the UI (#1, #2 …).</summary>
    public int Number { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Queued;
    public DeploymentTrigger Trigger { get; set; }

    // Git provenance (nullable for image/static deploys).
    public string? CommitSha { get; set; }
    public string? CommitMessage { get; set; }
    public string? CommitAuthor { get; set; }
    public string? GitRef { get; set; }

    /// <summary>Resulting image tag, e.g. "harbora/myapp:build-42".</summary>
    public string? ImageTag { get; set; }

    /// <summary>When this is a rollback, the deployment whose image we re-released.</summary>
    public Guid? RolledBackFromId { get; set; }

    public Guid TriggeredByUserId { get; set; }

    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? ErrorMessage { get; set; }

    public ICollection<DeploymentLog> Logs { get; set; } = new List<DeploymentLog>();
}
