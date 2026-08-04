using Harbora.Domain.Common;

namespace Harbora.Domain.Ai;

/// <summary>
/// One request through the gateway, as far as billing and debugging need to know.
///
/// There is deliberately no prompt and no response here. Storing them would make this table the most
/// sensitive thing Harbora holds — every customer's data, in one place, readable by anyone with
/// database access, retained for as long as billing records are. What is kept is what a bill and an
/// incident review actually need: which model, how many tokens, what it cost, how long it took, and
/// whether it worked.
///
/// Content logging, if it is ever added, has to be a separate opt-in feature with its own retention
/// and its own consent — not a column quietly added here.
/// </summary>
public class AiUsageRecord : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>Which key was used. Kept so a compromised key's traffic can be identified.</summary>
    public Guid? AiUserApiKeyId { get; set; }

    public Guid AiPlanId { get; set; }

    /// <summary>What the caller asked for, in Harbora's naming.</summary>
    public string RequestedModel { get; set; } = string.Empty;

    /// <summary>What actually served it. Differs from the request after a fallback.</summary>
    public string? ProviderModelId { get; set; }

    public Guid? AiProviderId { get; set; }

    /// <summary>
    /// Which credential carried it — the id and label only. Never the token: an audit table that
    /// holds provider secrets is a second copy of the thing hardest to rotate.
    /// </summary>
    public Guid? AiProviderCredentialId { get; set; }

    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CachedInputTokens { get; set; }

    /// <summary>What the provider will charge us.</summary>
    public decimal ProviderCost { get; set; }

    /// <summary>What the customer is charged. Both are kept so margin stays knowable.</summary>
    public decimal ChargedCost { get; set; }

    public int DurationMs { get; set; }

    /// <summary>HTTP status returned to the customer.</summary>
    public int StatusCode { get; set; }

    public bool Streaming { get; set; }

    /// <summary>
    /// True when the customer disconnected mid-stream. Tokens already produced are still charged —
    /// the provider billed us for them — and this is how that shows up honestly on an invoice
    /// somebody queries.
    /// </summary>
    public bool ClientDisconnected { get; set; }

    /// <summary>Ties this row to the request the customer saw, for support conversations.</summary>
    public string? CorrelationId { get; set; }

    /// <summary>A short reason when it failed. Never a prompt, never a response body.</summary>
    public string? FailureReason { get; set; }
}
