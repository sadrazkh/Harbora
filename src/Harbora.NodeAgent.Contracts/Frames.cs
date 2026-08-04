using System.Text.Json;

namespace Harbora.NodeAgent.Contracts;

/// <summary>Frame types a node sends to the control plane.</summary>
public static class NodeFrames
{
    public const string Hello = "node.hello";
    public const string Resume = "node.resume";
    public const string Heartbeat = "node.heartbeat";
    public const string Inventory = "node.inventory";
    public const string CommandAck = "command.ack";
    public const string CommandProgress = "command.progress";
    public const string CommandResult = "command.result";
    public const string LogChunk = "log.chunk";
    public const string Event = "node.event";
    public const string Pong = "node.pong";
}

/// <summary>Frame types the control plane sends to a node.</summary>
public static class ControlFrames
{
    public const string HelloAck = "control.hello-ack";
    public const string Command = "control.command";
    public const string Cancel = "control.cancel";
    public const string CredentialRotated = "control.credential-rotated";
    public const string Ack = "control.ack";
    public const string Ping = "control.ping";
}

/// <summary>
/// The outer envelope every frame on the persistent channel is wrapped in, both directions.
///
/// <para>
/// <see cref="Sequence"/> is per-sender and monotonic. It exists so a reconnect is a resumption
/// rather than a restart: each side tells the other the last sequence it durably handled, and
/// only the tail is replayed. Without it, a five-second network blip during a deploy would either
/// lose the result or deliver it twice.
/// </para>
/// </summary>
public sealed record ControlFrame
{
    /// <summary>Protocol version. A frame whose version is not negotiated is dropped, not guessed at.</summary>
    public int V { get; init; } = NodeContract.ProtocolVersion;

    public required string Type { get; init; }

    /// <summary>Unique per frame. Used for at-least-once delivery bookkeeping.</summary>
    public required string Id { get; init; }

    /// <summary>Monotonic per sender within a session; 0 for frames sent before a session exists.</summary>
    public long Sequence { get; init; }

    public DateTimeOffset SentAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Ties every frame of one logical operation together across both sides' logs.</summary>
    public string? CorrelationId { get; init; }

    public JsonElement? Payload { get; init; }

    public static ControlFrame Create<T>(string type, T payload, long sequence = 0, string? correlationId = null) =>
        new()
        {
            Type = type,
            Id = Guid.CreateVersion7().ToString("n"),
            Sequence = sequence,
            CorrelationId = correlationId,
            Payload = JsonSerializer.SerializeToElement(payload, NodeContract.Json),
        };

    /// <summary>Reads the payload as <typeparamref name="T"/>, or null when there is none.</summary>
    public T? PayloadAs<T>() =>
        Payload is { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } p
            ? p.Deserialize<T>(NodeContract.Json)
            : default;
}

/// <summary>First frame of a session: who the node is and what it can speak.</summary>
public sealed record NodeHello
{
    public required string NodeId { get; init; }
    public required string AgentVersion { get; init; }
    public required IReadOnlyList<int> SupportedProtocolVersions { get; init; }

    /// <summary>Present on a reconnect. Lets the control plane restore the previous session's state.</summary>
    public string? ResumeToken { get; init; }

    /// <summary>Highest sequence number the node durably processed from the control plane.</summary>
    public long LastReceivedSequence { get; init; }

    public required NodeInventory Inventory { get; init; }
    public required NodeCapabilities Capabilities { get; init; }
}

/// <summary>The control plane's answer: the agreed version and the session's terms.</summary>
public sealed record ControlHelloAck
{
    /// <summary>The single version both sides will use for this session.</summary>
    public required int ProtocolVersion { get; init; }

    /// <summary>Opaque; the node stores it and presents it on the next reconnect.</summary>
    public required string ResumeToken { get; init; }

    /// <summary>Control-plane clock, so the node can detect (and log) a skew that would break replay checks.</summary>
    public DateTimeOffset ServerTime { get; init; }

    /// <summary>Highest sequence the control plane durably processed from this node.</summary>
    public long LastReceivedSequence { get; init; }

    /// <summary>
    /// Below this, the node must update before it is trusted with commands. Reported rather than
    /// enforced by the panel alone, because a node that knows it is too old can refuse work itself.
    /// </summary>
    public string? MinimumAgentVersion { get; init; }

    public int HeartbeatIntervalSeconds { get; init; } = 30;

    /// <summary>Scopes this node's credential carries. Commands outside them are refused locally.</summary>
    public IReadOnlyList<string>? GrantedScopes { get; init; }

    /// <summary>True when a session could not be resumed and the node must resend its full state.</summary>
    public bool ResumeRejected { get; init; }
}

/// <summary>Periodic liveness plus the volatile parts of the node's state.</summary>
public sealed record NodeHeartbeat
{
    public required string NodeId { get; init; }
    public required string AgentVersion { get; init; }
    public required NodeHealthState Health { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public double Load1 { get; init; }
    public double Load5 { get; init; }
    public double Load15 { get; init; }
    public long FreeMemoryBytes { get; init; }
    public long FreeDiskBytes { get; init; }
    public int RunningWorkloads { get; init; }
    public int ActiveDatabaseGrants { get; init; }
    public int ActiveTunnels { get; init; }
    public bool Draining { get; init; }

    /// <summary>When the node credential stops being usable — the panel warns long before it bites.</summary>
    public DateTimeOffset? CertificateExpiresAt { get; init; }
}

public enum NodeHealthState
{
    Unknown = 0,
    Healthy,
    /// <summary>Serving, but under pressure (disk/memory/CPU) or with a degraded subsystem.</summary>
    Degraded,
    Draining,
    Unhealthy,
}

/// <summary>Acknowledgement that a command was received and admitted (or recognised as a duplicate).</summary>
public sealed record CommandAck
{
    public required string CommandId { get; init; }
    public DateTimeOffset AcceptedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// True when the idempotency key had already been executed. The control plane must treat this
    /// as success-in-progress, not as a fresh run — the result frame will carry the original outcome.
    /// </summary>
    public bool Deduplicated { get; init; }

    /// <summary>Set when the command was refused outright; no result frame follows a rejected ack.</summary>
    public NodeError? Rejected { get; init; }
}

/// <summary>Coarse progress for long-running commands. Purely informational; never load-bearing.</summary>
public sealed record CommandProgress
{
    public required string CommandId { get; init; }
    public required string Phase { get; init; }
    public int? Percent { get; init; }
    public string? Message { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public enum CommandStatus
{
    Succeeded = 0,
    Failed,
    Cancelled,
    TimedOut,
    Rejected,
}

/// <summary>Terminal outcome of a command. Exactly one is emitted per accepted command.</summary>
public sealed record CommandResult
{
    public required string CommandId { get; init; }
    public required CommandStatus Status { get; init; }
    public NodeError? Error { get; init; }
    public JsonElement? Result { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }

    /// <summary>True when this outcome was replayed from the idempotency store rather than re-executed.</summary>
    public bool IdempotentReplay { get; init; }

    public static CommandResult Ok<T>(string commandId, T result, DateTimeOffset startedAt) => new()
    {
        CommandId = commandId,
        Status = CommandStatus.Succeeded,
        Result = JsonSerializer.SerializeToElement(result, NodeContract.Json),
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
    };

    public static CommandResult Fail(string commandId, NodeError error, DateTimeOffset startedAt) => new()
    {
        CommandId = commandId,
        Status = CommandStatus.Failed,
        Error = error,
        StartedAt = startedAt,
        CompletedAt = DateTimeOffset.UtcNow,
    };
}

/// <summary>A chunk of container output being streamed for an in-flight <c>StreamLogs</c> command.</summary>
public sealed record LogChunk
{
    public required string CommandId { get; init; }
    public required string WorkloadId { get; init; }
    public required string Text { get; init; }
    public bool Final { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>An unsolicited state change worth telling the control plane about immediately.</summary>
public sealed record NodeEvent
{
    public required string Kind { get; init; }
    public required string Message { get; init; }
    public string? WorkloadId { get; init; }
    public IReadOnlyDictionary<string, string>? Data { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>Well-known <see cref="NodeEvent.Kind"/> values.</summary>
public static class NodeEventKinds
{
    public const string ContainerStateChanged = "container.state-changed";
    public const string DeploymentCompleted = "deployment.completed";
    public const string DeploymentFailed = "deployment.failed";
    public const string DeploymentRolledBack = "deployment.rolled-back";
    public const string DiskPressure = "pressure.disk";
    public const string MemoryPressure = "pressure.memory";
    public const string CpuPressure = "pressure.cpu";
    public const string CertificateExpiring = "certificate.expiring";
    public const string CertificateRotated = "certificate.rotated";
    public const string TunnelStateChanged = "tunnel.state-changed";
    public const string DatabaseGrantExpired = "database-grant.expired";
    public const string DatabaseGrantRevoked = "database-grant.revoked";
    public const string AgentUpdateStarted = "agent-update.started";
    public const string AgentUpdateCompleted = "agent-update.completed";
    public const string AgentUpdateRolledBack = "agent-update.rolled-back";
    public const string DrainStarted = "node.drain-started";
    public const string DrainCompleted = "node.drain-completed";
}
