using FluentAssertions;
using Harbora.Domain.Functions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one place F3's namespace is forced (2026-08-21 functions-and-services plan, "Custom events
/// from customer apps"). Every one of these is the ingest endpoint's own refusal to let a caller
/// impersonate a platform event: whatever comes in, only <c>custom.*</c> comes out.
/// </summary>
public class FunctionCustomEventKeyNormalisationTests
{
    [Theory]
    [InlineData("order.paid", "custom.order.paid")]
    [InlineData("custom.order.paid", "custom.order.paid")]
    [InlineData("CUSTOM.Order.Paid", "custom.order.paid")]
    [InlineData("  order.paid  ", "custom.order.paid")]
    public void A_raw_key_lands_under_the_custom_namespace_exactly_once(string raw, string expected) =>
        FunctionEvents.NormaliseCustomKey(raw).Should().Be(expected);

    [Fact]
    public void A_caller_cannot_spoof_a_platform_event_by_naming_it_one()
    {
        // The whole point of forcing the namespace server-side: this must never come back as the
        // literal "deployment.succeeded" a real deployment publishes.
        var key = FunctionEvents.NormaliseCustomKey(FunctionEvents.DeploymentSucceeded);

        key.Should().Be("custom.deployment.succeeded");
        key.Should().NotBe(FunctionEvents.DeploymentSucceeded);
    }

    [Theory]
    [InlineData("Order Paid!!", "custom.order.paid")]
    [InlineData("order   paid", "custom.order.paid")]
    [InlineData("order/paid", "custom.order.paid")]
    public void Characters_outside_the_allowed_set_collapse_into_a_single_separator(string raw, string expected) =>
        FunctionEvents.NormaliseCustomKey(raw).Should().Be(expected);

    [Fact]
    public void A_dot_hyphen_or_underscore_the_caller_typed_is_kept_as_is()
    {
        // Unlike genuine junk (whitespace, punctuation), these three are the allowed alphabet itself
        // — an identifier's own choice of separator, not something to normalise away.
        FunctionEvents.NormaliseCustomKey("order_paid").Should().Be("custom.order_paid");
        FunctionEvents.NormaliseCustomKey("order-paid").Should().Be("custom.order-paid");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("custom.")]
    [InlineData("...")]
    public void Nothing_usable_survives_some_inputs(string? raw) =>
        FunctionEvents.NormaliseCustomKey(raw).Should().BeNull();

    [Fact]
    public void A_key_far_longer_than_the_storage_column_is_trimmed_to_fit()
    {
        var raw = new string('a', 500);

        var key = FunctionEvents.NormaliseCustomKey(raw);

        key.Should().NotBeNull();
        key!.Length.Should().BeLessThanOrEqualTo(64, "FunctionDefinition.EventKey is a varchar(64) column");
        key.Should().StartWith(FunctionEvents.CustomPrefix);
    }

    [Fact]
    public void A_platform_event_is_subscribable_but_not_custom() =>
        (FunctionEvents.IsKnown(FunctionEvents.DeploymentSucceeded),
         FunctionEvents.IsCustom(FunctionEvents.DeploymentSucceeded),
         FunctionEvents.IsSubscribable(FunctionEvents.DeploymentSucceeded))
            .Should().Be((true, false, true));

    [Fact]
    public void A_normalised_custom_key_is_subscribable_but_not_a_known_platform_event() =>
        (FunctionEvents.IsKnown("custom.order.paid"),
         FunctionEvents.IsCustom("custom.order.paid"),
         FunctionEvents.IsSubscribable("custom.order.paid"))
            .Should().Be((false, true, true));

    [Fact]
    public void A_key_nothing_recognises_at_all_is_neither() =>
        FunctionEvents.IsSubscribable("nothing.raises.this").Should().BeFalse();

    [Fact]
    public void The_bare_prefix_with_nothing_after_it_is_not_custom() =>
        FunctionEvents.IsCustom("custom.").Should().BeFalse();
}
