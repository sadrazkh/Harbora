using FluentAssertions;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The logins handed out for external database access.
///
/// The property that matters most is what is *not* here: after creation the plaintext password
/// exists nowhere in Harbora. A leaked copy of this platform's own database must not also hand over
/// live logins into every customer database.
/// </summary>
public class DatabaseCredentialManagerTests
{
    [Fact]
    public void A_created_credential_can_be_verified_against_its_hash()
    {
        var credential = DatabaseCredentialManager.Create();

        DatabaseCredentialManager.Verify(credential.Password, credential.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public void The_stored_hash_does_not_contain_the_password()
    {
        // The whole point. If this fails, the database is a password list.
        var credential = DatabaseCredentialManager.Create();

        credential.PasswordHash.Should().NotContain(credential.Password);
    }

    [Fact]
    public void A_wrong_password_does_not_verify()
    {
        var credential = DatabaseCredentialManager.Create();

        DatabaseCredentialManager.Verify(credential.Password + "x", credential.PasswordHash).Should().BeFalse();
        DatabaseCredentialManager.Verify("", credential.PasswordHash).Should().BeFalse();
    }

    [Fact]
    public void Two_credentials_never_come_out_the_same()
    {
        var a = DatabaseCredentialManager.Create();
        var b = DatabaseCredentialManager.Create();

        a.Password.Should().NotBe(b.Password);
        a.Username.Should().NotBe(b.Username, "each grant needs its own login so one can be revoked alone");
    }

    [Fact]
    public void The_same_password_hashes_differently_every_time()
    {
        // A per-credential salt. Without it, two customers who happen to share a password are
        // visibly identical in the database.
        DatabaseCredentialManager.Hash("same-password")
            .Should().NotBe(DatabaseCredentialManager.Hash("same-password"));
    }

    [Fact]
    public void A_malformed_stored_hash_is_refused_rather_than_crashing()
    {
        // Rows predating a format change, or a truncated column. Refusing is right; throwing turns
        // a bad row into a 500 on the connection path.
        foreach (var bad in new[] { "", "garbage", "1.2", "notanumber.c2FsdA==.aGFzaA==", "100000.!!!.###" })
            DatabaseCredentialManager.Verify("anything", bad).Should().BeFalse($"stored value was {bad}");
    }

    [Fact]
    public void A_password_is_long_enough_that_guessing_is_not_a_strategy()
    {
        DatabaseCredentialManager.Create().Password.Length.Should().BeGreaterThanOrEqualTo(24);
    }

    [Fact]
    public void A_password_survives_being_pasted_into_a_connection_string()
    {
        // Characters that terminate a URL or get eaten by a shell turn into a support ticket that
        // looks like "the password is wrong".
        var password = DatabaseCredentialManager.Create().Password;

        password.Should().MatchRegex("^[A-Za-z0-9]+$");
    }

    [Fact]
    public void A_username_is_a_legal_database_identifier()
    {
        var username = DatabaseCredentialManager.Create("Shop Production!").Username;

        username.Should().MatchRegex("^[a-z0-9_]+$");
        username.Should().NotStartWith("_");
    }

    [Fact]
    public void An_empty_prefix_still_produces_a_usable_name()
    {
        DatabaseCredentialManager.Create("!!!").Username.Should().StartWith("harbora_");
    }

    [Theory]
    [InlineData("postgres", "postgresql://")]
    [InlineData("mysql", "mysql://")]
    [InlineData("mariadb", "mysql://")]
    [InlineData("mongodb", "mongodb://")]
    [InlineData("redis", "redis://")]
    public void The_connection_string_matches_the_engine(string engine, string scheme)
    {
        DatabaseCredentialManager.ConnectionString(engine, "gw.example.com", 5432, "u", "p", "db")
            .Should().StartWith(scheme);
    }

    [Fact]
    public void A_password_with_awkward_characters_is_escaped_into_the_url()
    {
        // Generated passwords are alphanumeric, but a rotated or imported one may not be, and an
        // unescaped @ silently moves the host.
        var connection = DatabaseCredentialManager.ConnectionString(
            "postgres", "gw.example.com", 5432, "user", "p@ss:word/1", "shop");

        connection.Should().Contain("p%40ss%3Aword%2F1");
        connection.Should().EndWith("@gw.example.com:5432/shop");
    }

    [Fact]
    public void The_connection_string_points_at_the_gateway_it_was_given()
    {
        // Never the node. The host passed in is the gateway, and nothing here substitutes another.
        DatabaseCredentialManager.ConnectionString("postgres", "gw.harbora.dev", 15432, "u", "p", "db")
            .Should().Contain("gw.harbora.dev:15432");
    }
}
