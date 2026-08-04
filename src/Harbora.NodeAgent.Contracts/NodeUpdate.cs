namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// Payload of <c>UpdateAgent</c>. The node downloads, verifies, swaps and — if the new binary
/// cannot come back and report its version — puts the old one back. An update that leaves a node
/// unreachable is worse than an update that did not happen, so rollback is not optional.
/// </summary>
public sealed record AgentUpdateRequest
{
    /// <summary>Version being installed, as the new binary will report it.</summary>
    public required string TargetVersion { get; init; }

    /// <summary>HTTPS URL of the release artifact for this node's architecture.</summary>
    public required string DownloadUrl { get; init; }

    /// <summary>
    /// Lowercase hex SHA-256 of the artifact. Required: an unverified binary downloaded and then
    /// executed as root is the single worst thing an update path can do.
    /// </summary>
    public required string Sha256 { get; init; }

    /// <summary>Optional detached signature, verified against the pinned release public key.</summary>
    public string? SignatureBase64 { get; init; }

    /// <summary>Drain running workloads before swapping. Slower, but no request is cut mid-flight.</summary>
    public bool DrainFirst { get; init; } = true;

    /// <summary>Seconds to wait for drain before proceeding anyway.</summary>
    public int DrainTimeoutSeconds { get; init; } = 300;

    /// <summary>
    /// Seconds the new binary has to start and report its version before the update is judged a
    /// failure and reverted.
    /// </summary>
    public int VerifyTimeoutSeconds { get; init; } = 120;
}

public sealed record AgentUpdateResult
{
    public required AgentUpdateOutcome Outcome { get; init; }
    public required string PreviousVersion { get; init; }
    public required string CurrentVersion { get; init; }
    public string? Message { get; init; }
    public NodeError? Error { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public enum AgentUpdateOutcome
{
    /// <summary>Already on the target version; nothing was downloaded.</summary>
    AlreadyCurrent = 0,
    Updated,
    RolledBack,
    Failed,
}

/// <summary>Payload of <c>DrainNode</c>.</summary>
public sealed record DrainNodeRequest
{
    /// <summary>False lifts a drain and puts the node back in service.</summary>
    public bool Drain { get; init; } = true;

    /// <summary>Stop running workloads too, rather than only refusing new ones.</summary>
    public bool StopWorkloads { get; init; }

    public int TimeoutSeconds { get; init; } = 300;
    public string? Reason { get; init; }
}

public sealed record DrainNodeResult
{
    public required bool Draining { get; init; }
    public int WorkloadsStopped { get; init; }
    public int WorkloadsRemaining { get; init; }
    public bool TimedOut { get; init; }
}
