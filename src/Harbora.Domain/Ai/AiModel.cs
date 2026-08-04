using Harbora.Domain.Common;

namespace Harbora.Domain.Ai;

/// <summary>
/// A model as Harbora offers it.
///
/// The display name and the alias are Harbora's, and the provider's model id is stored beside them
/// rather than used as the identity. That separation is what lets the same offering move between
/// providers without every customer's integration breaking — and what stops a provider's renaming
/// of a model from renaming it for everyone here.
/// </summary>
public class AiModel : BaseEntity
{
    public Guid AiProviderId { get; set; }
    public AiProvider? AiProvider { get; set; }

    /// <summary>What the provider calls it, e.g. <c>anthropic/claude-sonnet-4</c>.</summary>
    public string ProviderModelId { get; set; } = string.Empty;

    /// <summary>What callers use in the API. Stable across provider changes.</summary>
    public string Alias { get; set; } = string.Empty;

    /// <summary>What a person reads in the panel.</summary>
    public string DisplayName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// True when an administrator has edited this row by hand. A registry sync must not overwrite
    /// those edits — a sync that silently reverts somebody's pricing override is a sync nobody
    /// trusts enough to run.
    /// </summary>
    public bool IsManuallyManaged { get; set; }

    public int? ContextLength { get; set; }
    public int? MaxOutputTokens { get; set; }

    // ---- capabilities ----

    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsTools { get; set; }
    public bool SupportsVision { get; set; }
    public bool SupportsEmbeddings { get; set; }
    public bool SupportsResponses { get; set; }

    // ---- pricing, per million tokens in the platform's accounting currency ----

    /// <summary>What the provider charges. Kept so margin can be seen and audited.</summary>
    public decimal? ProviderInputPrice { get; set; }
    public decimal? ProviderOutputPrice { get; set; }

    /// <summary>What Harbora charges. Null means "provider price plus markup".</summary>
    public decimal? InputPriceOverride { get; set; }
    public decimal? OutputPriceOverride { get; set; }

    /// <summary>Percentage added to the provider price when no override is set.</summary>
    public decimal MarkupPercent { get; set; }

    public DateTimeOffset? LastSyncedAt { get; set; }
}

/// <summary>
/// A plan a tenant can be on.
///
/// Limits are stored per plan rather than per subscription so changing an offering does not mean
/// rewriting every customer's row — and so a customer can be moved between plans without their
/// limits having to be recomputed by hand.
/// </summary>
public class AiPlan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string? NameFa { get; set; }
    public string? Description { get; set; }
    public string? DescriptionFa { get; set; }

    public decimal MonthlyPrice { get; set; }

    /// <summary>Credit included each month, in the accounting currency.</summary>
    public decimal IncludedCredit { get; set; }

    public int RequestsPerMinute { get; set; } = 60;
    public int TokensPerMinute { get; set; } = 100_000;
    public int RequestsPerDay { get; set; } = 10_000;
    public long MonthlyTokenLimit { get; set; } = 10_000_000;

    /// <summary>Null means no ceiling beyond the included credit.</summary>
    public decimal? MonthlySpendLimit { get; set; }

    public int MaxContext { get; set; } = 128_000;
    public int MaxOutputTokens { get; set; } = 8_192;
    public int ConcurrentRequests { get; set; } = 4;

    public bool AllowStreaming { get; set; } = true;
    public bool TrialAvailable { get; set; }

    /// <summary>
    /// True to refuse once the limit is reached; false to let it run over and bill it. A soft limit
    /// on a prepaid plan is a way to give away money, so this defaults to hard.
    /// </summary>
    public bool HardLimit { get; set; } = true;

    public bool IsEnabled { get; set; } = true;

    public ICollection<AiPlanModel> Models { get; set; } = new List<AiPlanModel>();
}

/// <summary>
/// Which models a plan may use, and any per-model tightening.
///
/// A plan without rows here can use nothing. That is deliberate: the safe default for "which of our
/// models may this customer reach" is none of them, not all of them.
/// </summary>
public class AiPlanModel : BaseEntity
{
    public Guid AiPlanId { get; set; }
    public AiPlan? AiPlan { get; set; }

    public Guid AiModelId { get; set; }
    public AiModel? AiModel { get; set; }

    /// <summary>Tighter than the plan's own limit when set. Never looser.</summary>
    public int? MaxOutputTokens { get; set; }
    public int? RequestsPerMinute { get; set; }
}

/// <summary>What a tenant is currently entitled to.</summary>
public class AiSubscription : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public Guid AiPlanId { get; set; }
    public AiPlan? AiPlan { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Reset each billing period; the running total quota is checked against.</summary>
    public decimal PeriodSpend { get; set; }
    public long PeriodTokens { get; set; }
    public DateTimeOffset PeriodStartedAt { get; set; } = DateTimeOffset.UtcNow;
}
