using Harbora.Domain.Common;

namespace Harbora.Domain.Ai;

/// <summary>Which upstream API shape a provider speaks.</summary>
public enum AiProviderType
{
    /// <summary>OpenRouter's OpenAI-compatible API.</summary>
    OpenRouter = 0,

    /// <summary>Anything else speaking OpenAI's chat-completions shape.</summary>
    OpenAiCompatible = 1
}

/// <summary>
/// An upstream Harbora forwards requests to.
///
/// Plural by design. Building the platform around one provider means the day that provider has an
/// outage, or changes its terms, is the day the feature stops existing — so routing, pricing and
/// model naming are all expressed in Harbora's own terms and adapted per provider.
/// </summary>
public class AiProvider : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public AiProviderType Type { get; set; } = AiProviderType.OpenRouter;

    /// <summary>
    /// Where requests go. Validated against an allowlist before use — a provider URL an
    /// administrator can type is a request Harbora's server will make, which is an SSRF hole if
    /// nothing checks it.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Lower is tried first. Ties are broken by weight, then by load.</summary>
    public int Priority { get; set; }

    /// <summary>Optional headers this provider wants, as JSON. Never used for authorization.</summary>
    public string? ExtraHeadersJson { get; set; }

    /// <summary>A ceiling on spend, in the platform's accounting currency. Null means no ceiling.</summary>
    public decimal? MonthlyBudget { get; set; }

    public ICollection<AiProviderCredential> Credentials { get; set; } = new List<AiProviderCredential>();
}

/// <summary>
/// One API token for a provider.
///
/// Several per provider is the normal case: it is how rate limits are spread and how one exhausted
/// token stops being a total outage. The token itself is encrypted at rest and never returned to any
/// interface after it is saved — an administrator who needs a different one replaces it rather than
/// reading the old one back.
/// </summary>
public class AiProviderCredential : BaseEntity
{
    public Guid AiProviderId { get; set; }
    public AiProvider? AiProvider { get; set; }

    /// <summary>What a person calls it. The only part ever shown.</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Encrypted. Decrypted at the moment of use and never logged.</summary>
    public string EncryptedToken { get; set; } = string.Empty;

    public bool IsEnabled { get; set; } = true;

    /// <summary>Lower is preferred. Used before weight.</summary>
    public int Priority { get; set; }

    /// <summary>Share of traffic among equal priorities. Zero means "only if nothing else is left".</summary>
    public int Weight { get; set; } = 1;

    // ---- health, written by the router ----

    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public string? LastFailureReason { get; set; }

    /// <summary>Set when the provider said "slow down"; nothing is sent through it until it passes.</summary>
    public DateTimeOffset? RateLimitedUntil { get; set; }

    /// <summary>Consecutive failures. Resets on success; opens the circuit at a threshold.</summary>
    public int ConsecutiveFailures { get; set; }

    /// <summary>Spend attributed to this token this month, for budget enforcement.</summary>
    public decimal MonthToDateSpend { get; set; }
}
