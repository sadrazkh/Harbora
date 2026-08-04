using FluentAssertions;
using Harbora.Domain.Ai;
using Harbora.Infrastructure.Ai;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Choosing which provider token carries a request.
///
/// The failure this prevents is sending traffic to a credential that cannot serve it — disabled,
/// rate-limited, out of budget, or failing repeatedly. Every one of those reads to the customer as
/// a platform outage while the other credentials sit idle.
/// </summary>
public class AiCredentialRouterTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    private static AiProvider Provider(bool enabled = true, decimal? budget = null) =>
        new() { Id = Guid.CreateVersion7(), Name = "OpenRouter", IsEnabled = enabled, MonthlyBudget = budget };

    private static AiProviderCredential Credential(
        string label, int priority = 0, int weight = 1, bool enabled = true,
        int failures = 0, DateTimeOffset? lastFailure = null,
        DateTimeOffset? rateLimitedUntil = null, decimal spend = 0m) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Label = label,
            Priority = priority,
            Weight = weight,
            IsEnabled = enabled,
            ConsecutiveFailures = failures,
            LastFailureAt = lastFailure,
            RateLimitedUntil = rateLimitedUntil,
            MonthToDateSpend = spend
        };

    private static Dictionary<Guid, int> NoLoad() => new();

    [Fact]
    public void A_healthy_credential_is_chosen()
    {
        var credential = Credential("primary");

        var (chosen, failure) = AiCredentialRouter.Choose(Provider(), [credential], NoLoad(), Now);

        chosen.Should().Be(credential);
        failure.Should().BeNull();
    }

    [Fact]
    public void A_disabled_credential_never_carries_traffic()
    {
        var off = Credential("off", enabled: false);
        var on = Credential("on");

        AiCredentialRouter.Choose(Provider(), [off, on], NoLoad(), Now).Chosen.Should().Be(on);
    }

    [Fact]
    public void A_rate_limited_credential_is_left_alone_until_its_penalty_passes()
    {
        // Retrying into a rate limit is how a short penalty becomes a long one.
        var parked = Credential("parked", rateLimitedUntil: Now.AddMinutes(5));
        var free = Credential("free");

        AiCredentialRouter.Choose(Provider(), [parked, free], NoLoad(), Now).Chosen.Should().Be(free);
    }

    [Fact]
    public void A_credential_comes_back_once_its_penalty_has_passed()
    {
        var recovered = Credential("recovered", rateLimitedUntil: Now.AddMinutes(-1));

        AiCredentialRouter.IsAvailable(recovered, Now, null).Should().BeTrue();
    }

    [Fact]
    public void A_repeatedly_failing_credential_is_taken_out_of_rotation()
    {
        var broken = Credential("broken",
            failures: AiCredentialRouter.FailuresBeforeOpen, lastFailure: Now.AddSeconds(-5));
        var good = Credential("good");

        AiCredentialRouter.Choose(Provider(), [broken, good], NoLoad(), Now).Chosen.Should().Be(good);
    }

    [Fact]
    public void A_broken_credential_gets_one_chance_after_the_cooldown()
    {
        // Otherwise a credential that has recovered stays excluded for ever and the capacity is
        // simply lost.
        var recovering = Credential("recovering",
            failures: AiCredentialRouter.FailuresBeforeOpen,
            lastFailure: Now - AiCredentialRouter.CircuitCooldown - TimeSpan.FromSeconds(1));

        AiCredentialRouter.IsAvailable(recovering, Now, null).Should().BeTrue();
    }

    [Fact]
    public void A_credential_over_the_provider_budget_stops_spending()
    {
        // Spending past a ceiling an operator set is worse than refusing the request.
        var spent = Credential("spent", spend: 100m);

        AiCredentialRouter.IsAvailable(spent, Now, providerBudget: 100m).Should().BeFalse();
        AiCredentialRouter.IsAvailable(spent, Now, providerBudget: 200m).Should().BeTrue();
    }

    [Fact]
    public void A_budget_of_zero_means_no_ceiling_rather_than_no_spending()
    {
        var credential = Credential("any", spend: 50m);

        AiCredentialRouter.IsAvailable(credential, Now, providerBudget: 0m).Should().BeTrue();
    }

    [Fact]
    public void A_disabled_provider_routes_nowhere()
    {
        var (chosen, failure) = AiCredentialRouter.Choose(
            Provider(enabled: false), [Credential("fine")], NoLoad(), Now);

        chosen.Should().BeNull();
        failure!.Reason.Should().Contain("disabled");
    }

    [Fact]
    public void With_nothing_available_the_reason_is_given_rather_than_a_silent_null()
    {
        var (chosen, failure) = AiCredentialRouter.Choose(
            Provider(), [Credential("off", enabled: false)], NoLoad(), Now);

        chosen.Should().BeNull();
        failure!.Reason.Should().Contain("available");
    }

    [Fact]
    public void Priority_decides_before_anything_else()
    {
        // An operator saying which token they would rather use must not be overruled by load.
        var preferred = Credential("preferred", priority: 0);
        var fallback = Credential("fallback", priority: 5);
        var loads = new Dictionary<Guid, int> { [preferred.Id] = 50, [fallback.Id] = 0 };

        AiCredentialRouter.Choose(Provider(), [preferred, fallback], loads, Now).Chosen.Should().Be(preferred);
    }

    [Fact]
    public void Among_equals_the_emptiest_one_takes_the_next_request()
    {
        // Two tokens of equal priority should share a burst, not queue behind one.
        var busy = Credential("busy");
        var idle = Credential("idle");
        var loads = new Dictionary<Guid, int> { [busy.Id] = 10, [idle.Id] = 0 };

        AiCredentialRouter.Choose(Provider(), [busy, idle], loads, Now).Chosen.Should().Be(idle);
    }

    [Fact]
    public void Weight_lets_a_bigger_token_carry_proportionally_more()
    {
        // Both are carrying work, but the heavier one has three times the headroom, so at equal
        // raw load it is the less loaded of the two.
        var small = Credential("small", weight: 1);
        var large = Credential("large", weight: 3);
        var loads = new Dictionary<Guid, int> { [small.Id] = 3, [large.Id] = 3 };

        AiCredentialRouter.Choose(Provider(), [small, large], loads, Now).Chosen.Should().Be(large);
    }

    [Fact]
    public void A_zero_weight_credential_is_a_last_resort_not_a_division_by_zero()
    {
        var lastResort = Credential("last", weight: 0);
        var normal = Credential("normal", weight: 1);
        var loads = new Dictionary<Guid, int> { [normal.Id] = 99 };

        AiCredentialRouter.Choose(Provider(), [lastResort, normal], loads, Now).Chosen.Should().Be(normal);
    }

    [Fact]
    public void A_zero_weight_credential_is_still_used_when_it_is_all_there_is()
    {
        var lastResort = Credential("last", weight: 0);

        AiCredentialRouter.Choose(Provider(), [lastResort], NoLoad(), Now).Chosen.Should().Be(lastResort);
    }

    [Fact]
    public void A_credential_already_tried_is_not_tried_again_in_the_same_request()
    {
        // Retrying the same failing token is a retry that cannot succeed.
        var first = Credential("first");
        var second = Credential("second");

        var (chosen, _) = AiCredentialRouter.Choose(
            Provider(), [first, second], NoLoad(), Now, exclude: new HashSet<Guid> { first.Id });

        chosen.Should().Be(second);
    }

    [Fact]
    public void Routing_is_stable_for_the_same_inputs()
    {
        // A router that reshuffles makes an incident impossible to reason about after the fact.
        var a = Credential("a");
        var b = Credential("b");

        var first = AiCredentialRouter.Choose(Provider(), [a, b], NoLoad(), Now).Chosen;
        var again = AiCredentialRouter.Choose(Provider(), [a, b], NoLoad(), Now).Chosen;

        first.Should().Be(again);
    }

    [Fact]
    public void A_success_clears_the_failure_streak()
    {
        var credential = Credential("recovering", failures: 3);

        AiCredentialRouter.NoteSuccess(credential, Now);

        credential.ConsecutiveFailures.Should().Be(0);
        credential.LastSuccessAt.Should().Be(Now);
    }

    [Fact]
    public void A_rate_limit_failure_parks_the_credential_for_the_stated_time()
    {
        var credential = Credential("hot");

        AiCredentialRouter.NoteFailure(credential, Now, "429", TimeSpan.FromSeconds(30));

        credential.RateLimitedUntil.Should().Be(Now.AddSeconds(30));
        AiCredentialRouter.IsAvailable(credential, Now, null).Should().BeFalse();
    }
}

/// <summary>
/// Reading a provider failure and deciding what to do about it.
///
/// The distinction that matters is between a failure of the credential and a failure of the
/// request. Retrying a bad request across every credential burns all of them and returns the same
/// error more slowly.
/// </summary>
public class AiFailureClassifierTests
{
    [Fact]
    public void A_rate_limit_parks_the_credential_and_tries_another()
    {
        var verdict = AiFailureClassifier.Classify(429);

        verdict.Kind.Should().Be(AiFailureKind.RateLimited);
        verdict.ParkCredential.Should().BeTrue();
        verdict.RetryElsewhere.Should().BeTrue();
        verdict.RetryAfter.Should().NotBeNull("retrying immediately into a rate limit lengthens it");
    }

    [Fact]
    public void A_stated_retry_after_is_honoured()
    {
        AiFailureClassifier.Classify(429, "45").RetryAfter.Should().Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public void An_absurd_retry_after_is_capped()
    {
        // A provider asking us to wait an hour would otherwise park a credential for an hour.
        AiFailureClassifier.Classify(429, "99999").RetryAfter!.Value
            .Should().BeLessThanOrEqualTo(TimeSpan.FromMinutes(5));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(402)]
    public void A_rejected_credential_is_parked_and_another_is_tried(int status)
    {
        var verdict = AiFailureClassifier.Classify(status);

        verdict.Kind.Should().Be(AiFailureKind.CredentialRejected);
        verdict.ParkCredential.Should().BeTrue();
        verdict.RetryElsewhere.Should().BeTrue();
    }

    [Theory]
    [InlineData(400)]
    [InlineData(404)]
    [InlineData(422)]
    public void A_bad_request_is_not_retried_anywhere(int status)
    {
        // Another credential returns the same answer, more slowly, having burned a token's quota.
        var verdict = AiFailureClassifier.Classify(status);

        verdict.Kind.Should().Be(AiFailureKind.BadRequest);
        verdict.RetryElsewhere.Should().BeFalse();
        verdict.ParkCredential.Should().BeFalse();
    }

    [Fact]
    public void A_provider_error_is_worth_trying_elsewhere_without_blaming_the_credential()
    {
        var verdict = AiFailureClassifier.Classify(503);

        verdict.Kind.Should().Be(AiFailureKind.ProviderError);
        verdict.RetryElsewhere.Should().BeTrue();
        verdict.ParkCredential.Should().BeFalse();
    }

    [Fact]
    public void A_network_failure_is_worth_trying_elsewhere()
    {
        var verdict = AiFailureClassifier.Classify(null, exception: new HttpRequestException("no route"));

        verdict.Kind.Should().Be(AiFailureKind.Network);
        verdict.RetryElsewhere.Should().BeTrue();
    }

    [Fact]
    public void A_response_already_sent_to_the_customer_is_never_retried()
    {
        // They have seen part of an answer. A second attempt either duplicates or contradicts it.
        AiFailureClassifier.IsSafeToRetry(responseStarted: true, usageRecorded: false).Should().BeFalse();
    }

    [Fact]
    public void Something_already_charged_for_is_never_retried()
    {
        AiFailureClassifier.IsSafeToRetry(responseStarted: false, usageRecorded: true).Should().BeFalse();
    }

    [Fact]
    public void An_untouched_request_may_be_retried()
    {
        AiFailureClassifier.IsSafeToRetry(false, false).Should().BeTrue();
    }

    [Fact]
    public void Backoff_grows_but_stops_growing()
    {
        // Past a ceiling the customer's own client has given up, so the wait costs a connection and
        // buys nothing.
        AiFailureClassifier.Backoff(1).Should().Be(TimeSpan.Zero);
        AiFailureClassifier.Backoff(2).Should().Be(TimeSpan.FromSeconds(2));
        AiFailureClassifier.Backoff(3).Should().Be(TimeSpan.FromSeconds(4));
        AiFailureClassifier.Backoff(9).Should().Be(TimeSpan.FromSeconds(8));
    }
}

/// <summary>
/// What a request costs.
///
/// Both the provider's cost and the charged amount are kept: storing only one makes margin
/// unknowable, and the first time a provider changes prices there is no way to tell which invoices
/// were computed under which rates.
/// </summary>
public class AiPricingTests
{
    private static AiModel Model(
        decimal? providerIn = 3m, decimal? providerOut = 15m,
        decimal? overrideIn = null, decimal? overrideOut = null, decimal markup = 0m) =>
        new()
        {
            ProviderInputPrice = providerIn,
            ProviderOutputPrice = providerOut,
            InputPriceOverride = overrideIn,
            OutputPriceOverride = overrideOut,
            MarkupPercent = markup
        };

    [Fact]
    public void Cost_is_per_million_tokens()
    {
        var cost = AiPricing.Calculate(Model(), inputTokens: 1_000_000, outputTokens: 0);

        cost.ProviderCost.Should().Be(3m);
    }

    [Fact]
    public void Markup_is_charged_on_top_of_the_provider_price()
    {
        var cost = AiPricing.Calculate(Model(markup: 20m), 1_000_000, 0);

        cost.ProviderCost.Should().Be(3m);
        cost.ChargedCost.Should().Be(3.6m);
    }

    [Fact]
    public void An_override_replaces_the_provider_price_rather_than_adding_to_it()
    {
        var cost = AiPricing.Calculate(Model(overrideIn: 10m, markup: 500m), 1_000_000, 0);

        cost.ChargedCost.Should().Be(10m);
    }

    [Fact]
    public void An_override_of_zero_really_means_free()
    {
        // Offering a model at no charge is a real decision. Treating zero as "not set" and billing
        // anyway would be the worst kind of bug.
        var cost = AiPricing.Calculate(Model(overrideIn: 0m, overrideOut: 0m), 5_000_000, 5_000_000);

        cost.ChargedCost.Should().Be(0m);
        cost.ProviderCost.Should().BeGreaterThan(0m, "we still owe the provider");
    }

    [Fact]
    public void Cached_input_is_not_billed_as_fresh_input()
    {
        var full = AiPricing.Calculate(Model(), 1_000_000, 0);
        var cached = AiPricing.Calculate(Model(), 1_000_000, 0, cachedInputTokens: 1_000_000);

        cached.ProviderCost.Should().BeLessThan(full.ProviderCost);
    }

    [Fact]
    public void More_cached_tokens_than_input_tokens_does_not_produce_a_refund()
    {
        // A miscount upstream must not turn into money invented out of a parsing error.
        var cost = AiPricing.Calculate(Model(), 100, 0, cachedInputTokens: 100_000);

        cost.ProviderCost.Should().BeGreaterThanOrEqualTo(0m);
        cost.ChargedCost.Should().BeGreaterThanOrEqualTo(0m);
    }

    [Fact]
    public void Negative_token_counts_are_treated_as_zero()
    {
        var cost = AiPricing.Calculate(Model(), -500, -500);

        cost.ChargedCost.Should().Be(0m);
    }

    [Fact]
    public void A_model_with_no_price_costs_nothing_rather_than_crashing()
    {
        var cost = AiPricing.Calculate(Model(providerIn: null, providerOut: null), 1_000, 1_000);

        cost.ProviderCost.Should().Be(0m);
        cost.ChargedCost.Should().Be(0m);
    }

    [Fact]
    public void A_small_request_is_not_rounded_away_to_free()
    {
        // Individual requests cost fractions of a cent. A hundred input tokens at $3 per million is
        // $0.0003 — rounded to currency precision that is zero, and a platform that bills zero for
        // most of its traffic has no revenue and no way to notice.
        var cost = AiPricing.Calculate(Model(), inputTokens: 100, outputTokens: 0);

        cost.ChargedCost.Should().Be(0.0003m);
    }

    [Fact]
    public void A_negative_markup_never_produces_a_negative_charge()
    {
        AiPricing.Rate(null, 3m, -500m).Should().BeGreaterThanOrEqualTo(0m);
    }
}
