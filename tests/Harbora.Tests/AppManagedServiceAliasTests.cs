using FluentAssertions;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C1 (2026-08-22 config-delivery plan): "an app attached to two databases must get unambiguous
/// variable names ... make collisions impossible rather than last-write-wins." This is the
/// resolution logic that makes that literally true — a customer's requested alias, or the service's
/// own name, sanitised, and suffixed until it does not collide with a sibling already on the same
/// app. <see cref="AppManagedServicePipelineTests"/> proves the resulting names actually reach a
/// container side by side.
/// </summary>
public class AppManagedServiceAliasTests
{
    [Fact]
    public void With_nothing_requested_the_alias_defaults_to_the_services_own_name()
    {
        AppManagedServiceAlias.Resolve(null, "orders", []).Should().Be("ORDERS");
    }

    [Fact]
    public void A_requested_alias_is_sanitised_but_otherwise_kept()
    {
        AppManagedServiceAlias.Resolve("primary-db", "orders", []).Should().Be("PRIMARY_DB");
    }

    [Fact]
    public void A_colliding_default_is_suffixed_rather_than_silently_reused()
    {
        AppManagedServiceAlias.Resolve(null, "orders", ["ORDERS"]).Should().Be("ORDERS_2");
    }

    [Fact]
    public void A_third_collision_keeps_counting_up()
    {
        AppManagedServiceAlias.Resolve(null, "orders", ["ORDERS", "ORDERS_2"]).Should().Be("ORDERS_3");
    }

    [Fact]
    public void Comparison_against_existing_aliases_is_case_insensitive()
    {
        AppManagedServiceAlias.Resolve(null, "orders", ["orders"]).Should().Be("ORDERS_2",
            "an alias already taken in a different case is still the same collision");
    }

    [Fact]
    public void A_name_starting_with_a_digit_gets_a_leading_underscore_so_it_stays_a_legal_env_var_prefix()
    {
        AppManagedServiceAlias.Sanitize("2nd-cache").Should().Be("_2ND_CACHE");
    }

    [Fact]
    public void A_name_with_nothing_usable_falls_back_to_a_generic_alias()
    {
        AppManagedServiceAlias.Sanitize("***").Should().Be("SERVICE");
    }

    [Fact]
    public void Blank_existing_aliases_are_ignored_rather_than_treated_as_a_collision()
    {
        AppManagedServiceAlias.Resolve(null, "orders", [null, "", "   "]).Should().Be("ORDERS");
    }
}
