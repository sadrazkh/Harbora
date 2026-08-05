using FluentAssertions;
using Harbora.Infrastructure.Storage;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether a name is one an S3 bucket can have.
///
/// The rules are not ours and cannot be relaxed: a name the storage server rejects fails at
/// provisioning time — after the row has been written and the person has been told they have a
/// bucket. Every case here is one that is accepted by a naive check and refused by a real server,
/// or the reverse.
/// </summary>
public class BucketNameTests
{
    [Theory]
    [InlineData("uploads")]
    [InlineData("my-app-uploads")]
    [InlineData("a1b")]
    [InlineData("2024-backups")]
    public void An_ordinary_name_is_accepted(string name)
    {
        BucketName.Check(name).Should().Be(BucketNameRefusal.None);
    }

    [Fact]
    public void Uppercase_is_refused_rather_than_lowercased()
    {
        // A name silently changed is a name that does not match what somebody put in their
        // configuration file, and they find out when the client 404s.
        BucketName.Check("MyUploads").Should().Be(BucketNameRefusal.BadCharacters);
    }

    [Fact]
    public void A_period_is_refused_even_though_S3_allows_it()
    {
        // Legal in S3 and unusable over TLS with virtual-host addressing: the wildcard certificate
        // does not cover the extra label, so the client fails on the certificate rather than on
        // anything that points at the name.
        BucketName.Check("my.uploads").Should().Be(BucketNameRefusal.BadCharacters);
    }

    [Theory]
    [InlineData("-uploads")]
    [InlineData("uploads-")]
    public void A_name_cannot_start_or_end_with_a_hyphen(string name)
    {
        BucketName.Check(name).Should().Be(BucketNameRefusal.BadEnds);
    }

    [Fact]
    public void Something_shaped_like_an_address_is_refused()
    {
        // Path-style and virtual-host addressing cannot both resolve it.
        BucketName.Check("192-168-1-1").Should().Be(BucketNameRefusal.LooksLikeAnAddress);
    }

    [Fact]
    public void A_name_that_merely_contains_numbers_is_not_an_address()
    {
        // The guard has to be narrow, or it refuses ordinary names like this one.
        BucketName.Check("2024-1-2-backups").Should().Be(BucketNameRefusal.None);
    }

    [Theory]
    [InlineData("uploads-s3alias")]
    [InlineData("uploads--ol-s3")]
    public void A_reserved_suffix_is_refused(string name)
    {
        // Accepted by some servers and rejected by others, which is worse than a refusal here.
        BucketName.Check(name).Should().Be(BucketNameRefusal.ReservedSuffix);
    }

    [Theory]
    [InlineData("ab", BucketNameRefusal.TooShort)]
    [InlineData("", BucketNameRefusal.Missing)]
    [InlineData("   ", BucketNameRefusal.Missing)]
    [InlineData(null, BucketNameRefusal.Missing)]
    public void A_name_that_is_not_one_is_named_as_such(string? name, BucketNameRefusal expected)
    {
        // Each refusal is distinct so the page can say which rule was broken. "Invalid name" sends
        // somebody to the documentation; "must be at least 3 characters" does not.
        BucketName.Check(name).Should().Be(expected);
    }

    [Fact]
    public void The_length_limits_are_the_servers_limits()
    {
        BucketName.Check(new string('a', BucketName.MinLength)).Should().Be(BucketNameRefusal.None);
        BucketName.Check(new string('a', BucketName.MaxLength)).Should().Be(BucketNameRefusal.None);
        BucketName.Check(new string('a', BucketName.MaxLength + 1)).Should().Be(BucketNameRefusal.TooLong);
    }

    [Fact]
    public void Whitespace_around_a_name_is_not_quietly_removed()
    {
        // "uploads " and "uploads" would be two different buckets to a person and one to a trimmer.
        BucketName.IsValid(" uploads").Should().BeFalse();
    }

    // --- the suggestion offered on the form ---

    [Theory]
    [InlineData("My App Uploads", "my-app-uploads")]
    [InlineData("orders_db", "orders-db")]
    [InlineData("--weird--name--", "weird-name")]
    public void A_suggestion_is_derived_from_what_was_typed(string typed, string expected)
    {
        BucketName.Suggest(typed).Should().Be(expected);
    }

    [Fact]
    public void A_suggestion_is_always_something_that_would_be_accepted()
    {
        // It fills the box, so offering an invalid one just moves the failure one click later.
        BucketName.Suggest("A").Should().BeNull();
        BucketName.Suggest(new string('x', 200)).Should().NotBeNull();
        BucketName.IsValid(BucketName.Suggest(new string('x', 200))).Should().BeTrue();
    }

    [Fact]
    public void Nothing_typed_suggests_nothing()
    {
        BucketName.Suggest(null).Should().BeNull();
        BucketName.Suggest("   ").Should().BeNull();
    }
}
