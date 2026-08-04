using Harbora.Domain.Ai;

namespace Harbora.Infrastructure.Ai;

/// <summary>What one request cost, on both sides of the margin.</summary>
/// <param name="ProviderCost">What the upstream will charge, as best we can tell.</param>
/// <param name="ChargedCost">What the customer is charged.</param>
public sealed record AiCost(decimal ProviderCost, decimal ChargedCost);

/// <summary>
/// What a request costs.
///
/// Both numbers are kept. Storing only the charged amount makes margin unknowable after the fact,
/// and the first time a provider changes its prices there is no way to tell which invoices were
/// computed under which rates.
///
/// Prices are per million tokens, which is how providers quote them — converting at the boundary
/// rather than storing per-token avoids a column of numbers with eight leading zeros that nobody
/// can eyeball for correctness.
/// </summary>
public static class AiPricing
{
    private const decimal PerMillion = 1_000_000m;

    /// <summary>
    /// The cost of a request.
    ///
    /// <paramref name="cachedInputTokens"/> are billed at the provider's cached rate where one
    /// exists; here they are simply not double-counted against the ordinary input price.
    /// </summary>
    public static AiCost Calculate(AiModel model, long inputTokens, long outputTokens, long cachedInputTokens = 0)
    {
        // Negative counts mean something upstream miscounted. Treated as zero rather than credited:
        // a negative cost is money invented out of a parsing error.
        inputTokens = Math.Max(0, inputTokens);
        outputTokens = Math.Max(0, outputTokens);
        cachedInputTokens = Math.Clamp(cachedInputTokens, 0, inputTokens);

        var billableInput = inputTokens - cachedInputTokens;

        var providerIn = (model.ProviderInputPrice ?? 0m) * billableInput / PerMillion;
        var providerOut = (model.ProviderOutputPrice ?? 0m) * outputTokens / PerMillion;
        var providerCost = providerIn + providerOut;

        var chargedIn = Rate(model.InputPriceOverride, model.ProviderInputPrice, model.MarkupPercent)
            * billableInput / PerMillion;
        var chargedOut = Rate(model.OutputPriceOverride, model.ProviderOutputPrice, model.MarkupPercent)
            * outputTokens / PerMillion;

        return new AiCost(Round(providerCost), Round(chargedCost: chargedIn + chargedOut));
    }

    /// <summary>
    /// The rate to charge: an explicit override wins, otherwise the provider's price plus markup.
    ///
    /// An override of zero is honoured as free rather than treated as "not set" — offering a model
    /// at no charge is a real decision, and quietly billing for it would be the worst kind of bug.
    /// </summary>
    public static decimal Rate(decimal? over, decimal? providerPrice, decimal markupPercent)
    {
        if (over is { } explicitRate) return Math.Max(0m, explicitRate);

        var baseRate = providerPrice ?? 0m;
        return Math.Max(0m, baseRate * (1m + markupPercent / 100m));
    }

    /// <summary>
    /// Rounded to six decimal places. Individual requests cost fractions of a cent, so rounding to
    /// currency precision here would make almost every request free.
    /// </summary>
    private static decimal Round(decimal chargedCost) => Math.Round(chargedCost, 6, MidpointRounding.AwayFromZero);
}
