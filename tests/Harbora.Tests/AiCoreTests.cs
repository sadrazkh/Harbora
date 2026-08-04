using FluentAssertions;
using Harbora.Domain.Ai;
using Harbora.Infrastructure.Ai;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Harbora's own API keys for the AI gateway.
///
/// The customer holds one of these and never a provider token — revoking here really revokes,
/// whereas a provider token pasted into somebody's environment file keeps working and keeps billing
/// whoever owns it.
/// </summary>
public class AiApiKeyTests
{
    [Fact]
    public void A_new_key_verifies_against_its_own_hash()
    {
        var key = AiApiKeys.Create();

        AiApiKeys.Verify(key.Secret, key.Hash).Should().BeTrue();
    }

    [Fact]
    public void The_stored_hash_does_not_contain_the_secret()
    {
        // If this fails, the database is a list of working keys.
        var key = AiApiKeys.Create();

        key.Hash.Should().NotContain(key.Secret);
    }

    [Fact]
    public void A_key_is_recognisable_on_sight()
    {
        // Secret scanners key off exactly this. A key found in a paste or a log should be
        // identifiable as Harbora's and reportable.
        AiApiKeys.Create().Secret.Should().StartWith("har_");
    }

    [Fact]
    public void The_stored_prefix_is_short_enough_to_be_useless_alone()
    {
        var key = AiApiKeys.Create();

        key.Prefix.Should().StartWith("har_");
        key.Prefix.Length.Should().BeLessThan(key.Secret.Length / 2);
        AiApiKeys.Verify(key.Prefix, key.Hash).Should().BeFalse();
    }

    [Fact]
    public void Two_keys_are_never_the_same()
    {
        AiApiKeys.Create().Secret.Should().NotBe(AiApiKeys.Create().Secret);
    }

    [Fact]
    public void A_wrong_key_does_not_verify()
    {
        var key = AiApiKeys.Create();

        AiApiKeys.Verify(key.Secret + "x", key.Hash).Should().BeFalse();
        AiApiKeys.Verify("har_totallywrong", key.Hash).Should().BeFalse();
    }

    [Fact]
    public void A_malformed_stored_hash_is_refused_rather_than_crashing()
    {
        foreach (var bad in new[] { "", "nonsense", "1.2", "x.c2FsdA==.aGFzaA==", "100000.!!.!!" })
            AiApiKeys.Verify("har_anything", bad).Should().BeFalse($"stored was {bad}");
    }

    [Fact]
    public void The_lookup_prefix_narrows_to_one_row()
    {
        // Without it, every authenticated request would hash the candidate against every key in the
        // table — slow, and a way to make the server do unbounded work on demand.
        var key = AiApiKeys.Create();

        AiApiKeys.PrefixOf(key.Secret).Should().Be(key.Prefix);
    }

    [Fact]
    public void Something_that_is_not_one_of_our_keys_has_no_prefix()
    {
        AiApiKeys.PrefixOf(null).Should().BeNull();
        AiApiKeys.PrefixOf("").Should().BeNull();
        AiApiKeys.PrefixOf("sk-someone-elses-key").Should().BeNull();
        AiApiKeys.PrefixOf("har_").Should().BeNull("too short to identify anything");
    }

    [Theory]
    [InlineData("Bearer har_abc123", "har_abc123")]
    [InlineData("bearer har_abc123", "har_abc123")]
    [InlineData("Bearer   har_abc123  ", "har_abc123")]
    public void A_bearer_token_is_read_from_the_header(string header, string expected)
    {
        AiApiKeys.FromAuthorizationHeader(header).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("har_abc123")]
    [InlineData("Basic har_abc123")]
    [InlineData("Bearer ")]
    public void Anything_else_in_the_header_is_not_a_token(string? header)
    {
        // Parsed strictly on purpose: this header decides who the request belongs to.
        AiApiKeys.FromAuthorizationHeader(header).Should().BeNull();
    }
}

/// <summary>
/// What a plan lets a request do.
///
/// Each of these, allowed by mistake, is a customer receiving something they are not paying for —
/// which surfaces later as a billing dispute rather than a bug report.
/// </summary>
public class AiPlanAccessTests
{
    private static AiModel Model(
        string alias, bool streaming = true, int? maxOutput = null, bool enabled = true) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Alias = alias,
            DisplayName = alias,
            ProviderModelId = "vendor/" + alias,
            SupportsStreaming = streaming,
            MaxOutputTokens = maxOutput,
            IsEnabled = enabled
        };

    private static AiPlan Plan(
        IEnumerable<AiModel>? allowed = null, bool streaming = true,
        int maxOutput = 4096, bool hardLimit = true,
        long tokenLimit = 1_000_000, decimal credit = 10m)
    {
        var plan = new AiPlan
        {
            Id = Guid.CreateVersion7(),
            AllowStreaming = streaming,
            MaxOutputTokens = maxOutput,
            HardLimit = hardLimit,
            MonthlyTokenLimit = tokenLimit,
            IncludedCredit = credit
        };

        foreach (var model in allowed ?? [])
            plan.Models.Add(new AiPlanModel { AiPlanId = plan.Id, AiModelId = model.Id });

        return plan;
    }

    [Fact]
    public void A_plan_with_no_models_can_use_nothing()
    {
        // The safe default for "which of our models may this customer reach" is none of them.
        var model = Model("fast");

        AiPlanAccess.ModelsFor(Plan(), [model]).Should().BeEmpty();
    }

    [Fact]
    public void A_customer_sees_only_the_models_their_plan_includes()
    {
        var included = Model("fast");
        var other = Model("expensive");

        var visible = AiPlanAccess.ModelsFor(Plan([included]), [included, other]);

        visible.Should().ContainSingle().Which.Alias.Should().Be("fast");
    }

    [Fact]
    public void A_disabled_model_disappears_even_from_a_plan_that_includes_it()
    {
        var model = Model("fast", enabled: false);

        AiPlanAccess.ModelsFor(Plan([model]), [model]).Should().BeEmpty();
    }

    [Fact]
    public void Asking_for_a_model_outside_the_plan_is_refused()
    {
        var included = Model("fast");
        var other = Model("expensive");
        var plan = Plan([included]);

        var resolved = AiPlanAccess.Resolve(plan, [included, other], "expensive");
        resolved.Should().BeNull();

        AiPlanAccess.Refuse(plan, resolved, "expensive", null, false)!
            .StatusCode.Should().Be(403);
    }

    [Fact]
    public void The_alias_lookup_is_case_insensitive()
    {
        var model = Model("fast");

        AiPlanAccess.Resolve(Plan([model]), [model], "FAST").Should().NotBeNull();
    }

    [Fact]
    public void Streaming_is_refused_when_the_plan_excludes_it()
    {
        var model = Model("fast");
        var plan = Plan([model], streaming: false);

        AiPlanAccess.Refuse(plan, model, "fast", null, streaming: true)!
            .Code.Should().Be("streaming_not_allowed");
    }

    [Fact]
    public void Streaming_is_refused_when_the_model_cannot_do_it()
    {
        var model = Model("batch", streaming: false);

        AiPlanAccess.Refuse(Plan([model]), model, "batch", null, streaming: true)!
            .Code.Should().Be("streaming_unsupported");
    }

    [Fact]
    public void Entitlement_is_checked_before_the_shape_of_the_request()
    {
        // Telling somebody their max_tokens is too high for a model they cannot use at all leaks
        // which models exist.
        var other = Model("expensive");

        AiPlanAccess.Refuse(Plan(), null, "expensive", 999_999, false)!
            .Code.Should().Be("model_not_available");
    }

    [Fact]
    public void A_request_past_the_plans_output_ceiling_is_refused()
    {
        var model = Model("fast");

        AiPlanAccess.Refuse(Plan([model], maxOutput: 4096), model, "fast", 8192, false)!
            .Code.Should().Be("max_tokens_too_high");
    }

    [Fact]
    public void A_per_model_rule_can_tighten_the_ceiling_but_never_raise_it()
    {
        // A single row must not quietly grant more than the plan says.
        var model = Model("fast");
        var plan = Plan([model], maxOutput: 4096);
        plan.Models.Single().MaxOutputTokens = 1024;

        AiPlanAccess.EffectiveMaxOutput(plan, model).Should().Be(1024);

        plan.Models.Single().MaxOutputTokens = 999_999;
        AiPlanAccess.EffectiveMaxOutput(plan, model).Should().Be(4096);
    }

    [Fact]
    public void The_models_own_limit_also_caps_the_ceiling()
    {
        var model = Model("small", maxOutput: 512);

        AiPlanAccess.EffectiveMaxOutput(Plan([model], maxOutput: 4096), model).Should().Be(512);
    }

    [Fact]
    public void A_normal_request_is_not_refused()
    {
        // The guard on everything above: a gateway that refuses everything is secure and useless.
        var model = Model("fast");

        AiPlanAccess.Refuse(Plan([model]), model, "fast", 1000, streaming: true).Should().BeNull();
    }

    // ---- quota ----

    [Fact]
    public void An_inactive_subscription_cannot_send_anything()
    {
        var plan = Plan();
        var subscription = new AiSubscription { AiPlanId = plan.Id, IsActive = false };

        AiPlanAccess.RefuseForQuota(plan, subscription)!.Code.Should().Be("subscription_inactive");
    }

    [Fact]
    public void A_hard_limit_plan_stops_when_the_tokens_run_out()
    {
        // Checked before the request: refusing afterwards means the money is already spent.
        var plan = Plan(tokenLimit: 1000);
        var subscription = new AiSubscription { AiPlanId = plan.Id, PeriodTokens = 1000 };

        AiPlanAccess.RefuseForQuota(plan, subscription)!.StatusCode.Should().Be(402);
    }

    [Fact]
    public void A_hard_limit_plan_stops_at_the_spending_ceiling()
    {
        var plan = Plan(credit: 5m);
        var subscription = new AiSubscription { AiPlanId = plan.Id, PeriodSpend = 5m };

        AiPlanAccess.RefuseForQuota(plan, subscription)!.Code.Should().Be("spend_limit_reached");
    }

    [Fact]
    public void A_soft_limit_plan_is_allowed_to_run_over()
    {
        // That is what makes it soft. A soft limit that blocks is a hard limit with a confusing name.
        var plan = Plan(hardLimit: false, tokenLimit: 1000);
        var subscription = new AiSubscription { AiPlanId = plan.Id, PeriodTokens = 50_000 };

        AiPlanAccess.RefuseForQuota(plan, subscription).Should().BeNull();
    }

    [Fact]
    public void A_subscription_with_room_left_is_allowed()
    {
        var plan = Plan(tokenLimit: 1_000_000, credit: 10m);
        var subscription = new AiSubscription { AiPlanId = plan.Id, PeriodTokens = 10, PeriodSpend = 0.01m };

        AiPlanAccess.RefuseForQuota(plan, subscription).Should().BeNull();
    }
}
