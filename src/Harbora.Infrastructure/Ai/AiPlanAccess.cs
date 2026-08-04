using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>Why a request was refused, with the status a client should see.</summary>
/// <param name="StatusCode">402 for quota, 403 for not entitled, 429 for rate, 400 for shape.</param>
public sealed record AiRefusal(int StatusCode, string Code, string Message);

/// <summary>
/// What a plan lets a request do.
///
/// Every check here answers a question the gateway must not get wrong in the permissive direction.
/// A model a plan does not include, an output length past the plan's ceiling, streaming on a plan
/// that does not allow it — each of them, allowed by mistake, is a customer receiving something they
/// are not paying for, and finding that out later is a billing dispute rather than a bug report.
/// </summary>
public static class AiPlanAccess
{
    /// <summary>
    /// The models this plan may use. A plan with no rows can use nothing — the safe default for
    /// "which of our models may this customer reach" is none of them.
    /// </summary>
    public static IReadOnlyList<AiModel> ModelsFor(AiPlan plan, IEnumerable<AiModel> allModels)
    {
        var allowed = plan.Models.Select(m => m.AiModelId).ToHashSet();

        return allModels
            .Where(m => m.IsEnabled && allowed.Contains(m.Id))
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Finds a model by the alias a caller asked for, within what the plan allows.</summary>
    public static AiModel? Resolve(AiPlan plan, IEnumerable<AiModel> allModels, string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) return null;

        return ModelsFor(plan, allModels)
            .FirstOrDefault(m => string.Equals(m.Alias, alias.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Whether a request may proceed, and why not when it may not.
    ///
    /// The order matters: entitlement before shape. Telling somebody their max_tokens is too high
    /// for a model they cannot use at all leaks which models exist.
    /// </summary>
    public static AiRefusal? Refuse(
        AiPlan plan, AiModel? model, string requestedAlias,
        int? requestedMaxTokens, bool streaming)
    {
        if (model is null)
            return new AiRefusal(403, "model_not_available",
                $"The model '{requestedAlias}' is not available on your plan.");

        if (streaming && !plan.AllowStreaming)
            return new AiRefusal(403, "streaming_not_allowed", "Your plan does not include streaming responses.");

        if (streaming && !model.SupportsStreaming)
            return new AiRefusal(400, "streaming_unsupported", $"'{model.Alias}' does not support streaming.");

        // The plan's ceiling, tightened by any per-model rule. A per-model row may make it smaller,
        // never larger — otherwise a single row quietly raises a plan's limits.
        var ceiling = EffectiveMaxOutput(plan, model);

        if (requestedMaxTokens is { } requested && requested > ceiling)
            return new AiRefusal(400, "max_tokens_too_high",
                $"max_tokens is limited to {ceiling} on your plan.");

        return null;
    }

    /// <summary>
    /// The most output tokens this plan will allow for this model: the smallest of the plan's
    /// ceiling, the per-model tightening, and what the model itself can produce.
    /// </summary>
    public static int EffectiveMaxOutput(AiPlan plan, AiModel model)
    {
        var ceiling = plan.MaxOutputTokens;

        var perModel = plan.Models.FirstOrDefault(m => m.AiModelId == model.Id)?.MaxOutputTokens;
        if (perModel is { } tighter && tighter < ceiling) ceiling = tighter;

        if (model.MaxOutputTokens is { } modelLimit && modelLimit < ceiling) ceiling = modelLimit;

        return ceiling;
    }

    /// <summary>
    /// Whether a subscription still has room, given what it has already spent this period.
    ///
    /// Checked before the request rather than after: refusing afterwards means the money is already
    /// gone. A soft-limit plan is allowed to run over, which is what makes it soft.
    /// </summary>
    public static AiRefusal? RefuseForQuota(AiPlan plan, AiSubscription subscription)
    {
        if (!subscription.IsActive)
            return new AiRefusal(403, "subscription_inactive", "Your AI subscription is not active.");

        if (!plan.HardLimit) return null;

        if (subscription.PeriodTokens >= plan.MonthlyTokenLimit)
            return new AiRefusal(402, "token_quota_exhausted",
                "You have used this month's token allowance.");

        var ceiling = plan.MonthlySpendLimit ?? plan.IncludedCredit;
        if (ceiling > 0 && subscription.PeriodSpend >= ceiling)
            return new AiRefusal(402, "spend_limit_reached", "You have reached this month's spending limit.");

        return null;
    }
}
