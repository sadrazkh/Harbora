namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// Structured failure codes. The control plane branches on these; the human message is for the
/// operator only and is never parsed. Codes are additive — a node that returns a code the panel
/// does not know must be treated as <see cref="Unknown"/> rather than as a protocol violation.
/// </summary>
public enum NodeErrorCode
{
    Unknown = 0,

    // --- envelope / protocol ---
    UnsupportedProtocolVersion,
    MalformedEnvelope,
    UnknownCommand,
    /// <summary>The command is real but this agent build does not implement it.</summary>
    CommandNotSupported,
    /// <summary>Envelope timestamp outside the freshness window, or its nonce was already seen.</summary>
    ReplayRejected,
    /// <summary>The command's declared scope is not one this node was enrolled to accept.</summary>
    Unauthorized,
    ValidationFailed,
    Timeout,
    Cancelled,

    // --- node state ---
    NodeDraining,
    NodeNotReady,
    AgentTooOld,
    UnsupportedArchitecture,
    InsufficientResources,

    // --- runtime ---
    RuntimeUnavailable,
    ImagePullFailed,
    /// <summary>An image was referenced by a mutable tag where a digest is required.</summary>
    ImageNotPinned,
    ContainerStartFailed,
    HealthCheckFailed,
    RolledBack,
    RollbackFailed,
    VolumeOperationFailed,
    NetworkOperationFailed,
    /// <summary>A spec asked for a host mount, capability or namespace the policy forbids.</summary>
    PolicyDenied,

    // --- database access ---
    GrantNotFound,
    GrantExpired,
    GrantRevoked,
    UnsupportedDatabaseEngine,
    CredentialRotationFailed,
    IpNotAllowed,
    ConnectionLimitReached,

    // --- tunnel ---
    TunnelUnavailable,
    TunnelRejected,

    // --- update ---
    UpdateDownloadFailed,
    UpdateVerificationFailed,
    UpdateApplyFailed,
    UpdateRolledBack,

    // --- enrollment ---
    EnrollmentTokenInvalid,
    EnrollmentTokenExpired,
    EnrollmentTokenAlreadyUsed,
    CredentialRevoked,

    /// <summary>The agent hit an unexpected fault. Details are redacted before they leave the node.</summary>
    Internal,
}

/// <summary>A machine-readable failure, optionally carrying whether a retry could ever help.</summary>
public sealed record NodeError
{
    public required NodeErrorCode Code { get; init; }

    /// <summary>Operator-facing message. Already passed through secret redaction.</summary>
    public required string Message { get; init; }

    /// <summary>True when the same request could succeed later (transient network, pull throttling…).</summary>
    public bool Retryable { get; init; }

    /// <summary>Optional non-secret structured detail, e.g. the field that failed validation.</summary>
    public IReadOnlyDictionary<string, string>? Details { get; init; }

    public static NodeError From(NodeErrorCode code, string message, bool retryable = false) =>
        new() { Code = code, Message = message, Retryable = retryable };
}
