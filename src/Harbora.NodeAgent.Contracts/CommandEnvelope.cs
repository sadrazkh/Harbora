using System.Text.Json;

namespace Harbora.NodeAgent.Contracts;

/// <summary>
/// One instruction from the control plane. Everything needed to admit, deduplicate, authorise,
/// bound and audit the instruction lives on the envelope; the verb-specific data is confined to
/// <see cref="Payload"/>, which no code touches until the envelope itself has been accepted.
/// </summary>
public sealed record CommandEnvelope
{
    /// <summary>Unique per issued command. Identifies the ack/progress/result frames that follow.</summary>
    public required string CommandId { get; init; }

    /// <summary>Must be a member of <see cref="NodeCommandCatalog"/>; anything else is refused.</summary>
    public required string Command { get; init; }

    /// <summary>
    /// Caller-chosen key for "this is the same request as before". Re-sending a deploy after a
    /// timeout must not deploy twice, and the control plane cannot know whether its first attempt
    /// landed — so the node, which does know, is where that decision belongs.
    /// </summary>
    public required string IdempotencyKey { get; init; }

    /// <summary>Single-use value. Together with <see cref="IssuedAt"/> this makes a replay detectable.</summary>
    public required string Nonce { get; init; }

    /// <summary>
    /// When the control plane issued this. Envelopes outside
    /// <see cref="NodeContract.CommandFreshnessWindow"/> are rejected regardless of nonce.
    /// </summary>
    public required DateTimeOffset IssuedAt { get; init; }

    /// <summary>Threaded through every log line and audit record on both sides.</summary>
    public required string CorrelationId { get; init; }

    /// <summary>
    /// The scope the control plane asserts this command needs. It must both match the catalog's
    /// requirement for the verb and be one the node was enrolled to accept — a mismatch is a
    /// refusal, never a downgrade.
    /// </summary>
    public required string RequiredScope { get; init; }

    /// <summary>Hard bound on execution. Clamped to the catalog default when absent or absurd.</summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>Who asked, on whose behalf, from where. Recorded verbatim in the node audit log.</summary>
    public AuditMetadata? Audit { get; init; }

    /// <summary>Verb-specific arguments. Shape is defined per command in contracts/node-agent/v1/.</summary>
    public JsonElement Payload { get; init; }

    public T? PayloadAs<T>() =>
        Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? default
            : Payload.Deserialize<T>(NodeContract.Json);
}

/// <summary>Request to abandon an in-flight command. Best-effort: some steps are not interruptible.</summary>
public sealed record CommandCancel
{
    public required string CommandId { get; init; }
    public string? Reason { get; init; }
}

/// <summary>
/// The provenance of an action, carried from the control plane onto the node's own audit trail so
/// "who deleted this volume" is answerable from the server itself, not only from the panel.
/// </summary>
public sealed record AuditMetadata
{
    /// <summary>Stable id of the acting principal in the control plane.</summary>
    public string? ActorId { get; init; }

    /// <summary>Display form (email/username). Never a credential.</summary>
    public string? ActorName { get; init; }

    /// <summary>Workspace/tenant the action belongs to. Drives per-tenant isolation checks.</summary>
    public string? TenantId { get; init; }

    /// <summary>Originating client address as the control plane saw it.</summary>
    public string? SourceIp { get; init; }

    /// <summary>Free-form reason, e.g. an incident ticket.</summary>
    public string? Reason { get; init; }
}
