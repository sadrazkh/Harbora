using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which variables an attach writes.
///
/// A database handed an application a fixed set of names, which works exactly once. Attaching a
/// second PostgreSQL overwrote the first one's values under the same names: nothing failed at
/// attach time, nothing failed at deploy time, and the first query after the next release went to
/// the wrong server. An environment holding several databases is the ordinary case, so the names
/// have to be able to hold more than one.
/// </summary>
public class AttachKeysTests
{
    private static readonly Dictionary<string, string> Wanted = new()
    {
        ["DATABASE_URL"] = "postgresql://u:p@orders:5432/orders",
        ["PGHOST"] = "orders"
    };

    private static Dictionary<string, string?> Nothing => new();

    [Fact]
    public void The_first_database_gets_the_names_everything_already_reads()
    {
        // Every application that exists today reads DATABASE_URL. The first attach must keep
        // writing it, or this change breaks every one of them at once.
        var final = AttachKeys.For(Wanted, Nothing, "orders");

        final.Should().ContainKey("DATABASE_URL");
        final["DATABASE_URL"].Should().Be(Wanted["DATABASE_URL"]);
    }

    [Fact]
    public void Every_database_also_gets_names_of_its_own()
    {
        var final = AttachKeys.For(Wanted, Nothing, "orders");

        final.Should().ContainKey("ORDERS_DATABASE_URL");
        final["ORDERS_DATABASE_URL"].Should().Be(Wanted["DATABASE_URL"]);
    }

    [Fact]
    public void A_second_database_does_not_take_the_first_ones_names()
    {
        // The bug this exists to prevent, in one assertion.
        var existing = new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "postgresql://u:p@customers:5432/customers",
            ["PGHOST"] = "customers"
        };

        var final = AttachKeys.For(Wanted, existing, "orders");

        final.Should().NotContainKey("DATABASE_URL");
        final.Should().ContainKey("ORDERS_DATABASE_URL");
    }

    [Fact]
    public void Re_attaching_the_same_database_keeps_the_names_it_already_had()
    {
        // A password rotation re-attaches. If "already present" alone meant "somebody else's", the
        // database would lose the unprefixed names it has been using since it was created and the
        // application would start reading a different one.
        var existing = new Dictionary<string, string?>(Wanted!);

        var final = AttachKeys.For(Wanted, existing, "orders");

        final.Should().ContainKey("DATABASE_URL");
    }

    [Fact]
    public void A_value_that_cannot_be_read_is_treated_as_somebody_elses()
    {
        // Undecryptable, so we cannot tell whose it is. Taking the name would overwrite a database
        // somebody may still be using; leaving it alone costs them a rename at worst.
        var existing = new Dictionary<string, string?> { ["DATABASE_URL"] = null };

        AttachKeys.For(Wanted, existing, "orders").Should().NotContainKey("DATABASE_URL");
    }

    [Fact]
    public void Partly_claimed_counts_as_claimed()
    {
        // One of the two names belongs to another database. Writing the other one would leave the
        // application with half of each — a hostname from one and a URL from another, which is the
        // worst of the three possible outcomes.
        var existing = new Dictionary<string, string?> { ["PGHOST"] = "customers" };

        var final = AttachKeys.For(Wanted, existing, "orders");

        final.Should().NotContainKey("DATABASE_URL");
        final.Should().NotContainKey("PGHOST");
    }

    [Fact]
    public void Whether_it_had_to_fall_back_is_reportable()
    {
        // Somebody attaching a second database and then reading DATABASE_URL would get the first
        // one, and nothing on the screen would have suggested it.
        AttachKeys.IsPrefixedOnly(Wanted, Nothing).Should().BeFalse();
        AttachKeys.IsPrefixedOnly(Wanted, new Dictionary<string, string?> { ["PGHOST"] = "other" })
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("orders", "ORDERS_")]
    [InlineData("orders-db", "ORDERS_DB_")]
    [InlineData("  Orders DB  ", "ORDERS_DB_")]
    [InlineData("orders--db", "ORDERS_DB_")]
    // Leading and trailing punctuation would otherwise become leading and trailing underscores, so
    // "orders" and "-orders-" produce two different prefixes for what people read as one name.
    [InlineData("-orders-", "ORDERS_")]
    [InlineData("_orders", "ORDERS_")]
    public void The_prefix_comes_from_the_service_name(string name, string expected)
    {
        AttachKeys.PrefixFor(name).Should().Be(expected);
    }

    [Fact]
    public void A_prefix_never_starts_with_a_digit()
    {
        // A shell will not export 2ND_CACHE_DATABASE_URL, so the variable would exist in the
        // container's config and not in the process that reads it.
        AttachKeys.PrefixFor("2nd-cache").Should().Be("_2ND_CACHE_");
    }

    [Theory]
    [InlineData("")]
    [InlineData("---")]
    [InlineData("!!!")]
    public void A_name_with_nothing_usable_still_gets_a_prefix(string name)
    {
        // An empty prefix would make the "own names" identical to the unprefixed set they exist to
        // be distinguishable from, and the second database would overwrite the first again.
        AttachKeys.PrefixFor(name).Should().NotBeEmpty();
        AttachKeys.PrefixFor(name).Should().EndWith("_");
    }

    [Fact]
    public void Two_services_never_share_a_prefix_by_accident()
    {
        AttachKeys.PrefixFor("orders").Should().NotBe(AttachKeys.PrefixFor("customers"));
    }
}
