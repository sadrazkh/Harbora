using Harbora.Domain.Common;

namespace Harbora.Domain.Nodes;

public enum NodeCommandStatus
{
    /// <summary>Persisted, not yet handed to a connection.</summary>
    Queued = 0,

    /// <summary>Sent; the node has not acknowledged it yet.</summary>
    Sent = 1,

    /// <summary>The node admitted it and is working.</summary>
    Acknowledged = 2,

    Succeeded = 3,
    Failed = 4,
    Cancelled = 5,
    TimedOut = 6,

    /// <summary>The node refused it at admission. No result follows.</summary>
    Rejected = 7,
}

/// <summary>
/// One instruction the control plane sent to a node, and what came back.
///
/// <para>
/// Written before the frame goes out, so a command that was issued is on record even if the panel
/// crashes between sending and hearing back. Without that, the panel's idea of what it asked for
/// would be reconstructed from what it happened to receive — which is exactly wrong when the thing
/// that failed is the receiving.
/// </para>
/// </summary>
public class NodeCommandRecord : BaseEntity
{
    public Guid NodeRowId { get; set; }

    /// <summary>The agent-facing node id, denormalised so a command survives the node row's deletion.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Unique per issued command; the ack and result frames carry it back.</summary>
    public string CommandId { get; set; } = string.Empty;

    public string Command { get; set; } = string.Empty;

    /// <summary>
    /// Stable per logical operation. A retry reuses it, which is what lets the node recognise the
    /// second delivery and replay its answer instead of doing the work twice.
    /// </summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>Fresh on every send, including a retry. A reused nonce is rejected as a replay.</summary>
    public string Nonce { get; set; } = string.Empty;

    public string RequiredScope { get; set; } = string.Empty;

    /// <summary>The verb's arguments, as sent.</summary>
    public string PayloadJson { get; set; } = "{}";

    public NodeCommandStatus Status { get; set; } = NodeCommandStatus.Queued;

    public string? ResultJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary>True when the node answered from its ledger rather than executing again.</summary>
    public bool IdempotentReplay { get; set; }

    public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
    public DateTimeOffset? AcknowledgedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public int TimeoutSeconds { get; set; }

    public Guid? IssuedByUserId { get; set; }
    public string? IssuedByName { get; set; }
    public Guid? WorkspaceId { get; set; }
    public string? SourceIp { get; set; }
    public string? Reason { get; set; }

    public bool IsTerminal => Status
        is NodeCommandStatus.Succeeded or NodeCommandStatus.Failed
        or NodeCommandStatus.Cancelled or NodeCommandStatus.TimedOut or NodeCommandStatus.Rejected;
}

/// <summary>
/// Something a node reported that nobody asked about — a rolled-back deploy, disk pressure, a grant
/// expiring. Kept so the panel can show a node's story without polling it.
/// </summary>
public class NodeEventRecord : BaseEntity
{
    public Guid NodeRowId { get; set; }
    public string NodeId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? WorkloadId { get; set; }
    public string? DataJson { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
