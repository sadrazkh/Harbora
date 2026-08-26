using FluentAssertions;
using Harbora.Domain.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The collision scheme for a logical database's name (D1, 2026-08-25 shared-databases plan) — the
/// same idiom <see cref="AppManagedServiceAlias"/> already proves for an attachment's alias, one
/// level down: two apps asking for a database called "app" on the same instance must not collide
/// silently.
/// </summary>
public class LogicalDatabaseNameTests
{
    [Theory]
    [InlineData("Orders", "orders")]
    [InlineData("My App", "my_app")]
    [InlineData("my--app", "my_app")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("ALLCAPS", "allcaps")]
    public void Sanitize_lowercases_and_collapses_to_the_shared_safe_alphabet(string raw, string expected) =>
        LogicalDatabaseName.Sanitize(raw).Should().Be(expected);

    [Fact]
    public void Sanitize_of_nothing_usable_falls_back_to_a_generic_name()
    {
        LogicalDatabaseName.Sanitize("").Should().Be("db");
        LogicalDatabaseName.Sanitize("   ").Should().Be("db");
        LogicalDatabaseName.Sanitize("___").Should().Be("db");
    }

    [Fact]
    public void A_name_that_starts_with_a_digit_is_not_a_valid_identifier_on_its_own()
    {
        // "5apps" is not a legal unquoted identifier's natural reading, and Sanitize is meant to
        // produce something safe for every engine, not merely something DatabaseGrantSql.IsSafe
        // happens to accept.
        LogicalDatabaseName.Sanitize("5apps").Should().Be("db_5apps");
    }

    [Fact]
    public void A_name_longer_than_the_shared_bound_is_truncated()
    {
        var raw = new string('a', 100);
        var result = LogicalDatabaseName.Sanitize(raw);
        result.Length.Should().Be(LogicalDatabaseName.MaxLength);
    }

    [Fact]
    public void Resolve_keeps_the_requested_name_when_nothing_else_has_it()
    {
        LogicalDatabaseName.Resolve("orders", []).Should().Be("orders");
    }

    [Fact]
    public void Resolve_falls_back_to_the_service_default_when_nothing_was_requested()
    {
        LogicalDatabaseName.Resolve(null, []).Should().Be("db");
        LogicalDatabaseName.Resolve("   ", []).Should().Be("db");
    }

    [Fact]
    public void Two_databases_asking_for_the_same_name_do_not_collide()
    {
        LogicalDatabaseName.Resolve("app", ["app"]).Should().Be("app_2");
        LogicalDatabaseName.Resolve("app", ["app", "app_2"]).Should().Be("app_3");
    }

    [Fact]
    public void Resolve_never_reuses_a_name_regardless_of_case()
    {
        // Sanitize already lowercases, but a caller passing existing names straight from storage
        // must still be treated case-insensitively, the same as AppManagedServiceAlias.Resolve does
        // for an attachment's alias.
        LogicalDatabaseName.Resolve("Orders", ["orders", "ORDERS_2"]).Should().Be("orders_3");
    }

    [Fact]
    public void The_suffix_never_pushes_the_name_past_the_shared_bound()
    {
        var maxed = new string('a', LogicalDatabaseName.MaxLength);
        var resolved = LogicalDatabaseName.Resolve(maxed, [maxed]);

        resolved.Length.Should().BeLessOrEqualTo(LogicalDatabaseName.MaxLength);
        resolved.Should().EndWith("_2");
    }

    [Fact]
    public void An_existing_name_that_is_null_or_blank_is_ignored_rather_than_treated_as_taken()
    {
        LogicalDatabaseName.Resolve("orders", [null, "", "   "]).Should().Be("orders");
    }
}
