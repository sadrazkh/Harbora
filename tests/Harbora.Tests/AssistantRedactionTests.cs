using FluentAssertions;
using Harbora.Infrastructure.Assistant;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one part of the assistant that can do harm.
///
/// A deployment log is the most secret-dense thing a PaaS holds, and this decides what of it may
/// leave the server. Every test here is written the same way: assert the secret is gone, not merely
/// that a mask appeared somewhere.
/// </summary>
public class AssistantRedactionTests
{
    [Fact]
    public void A_secret_harbora_knows_never_leaves_however_it_was_printed()
    {
        // By value, so it is caught wherever it turns up — inside a URL, mid-sentence, anywhere.
        const string password = "hunter2-very-secret";
        var log = $"connecting with {password} …\nDATABASE_URL=postgres://app:{password}@db:5432/app";

        var result = AssistantRedaction.Redact(log, [password]);

        result.Text.Should().NotContain(password);
        result.Removed.Should().BeGreaterThan(0);
    }

    [Fact]
    public void A_secret_harbora_has_never_seen_is_caught_by_its_shape()
    {
        // The case the known-values list cannot cover: a key belonging to somebody else's service,
        // printed by the application itself.
        //
        // The value is deliberately not shaped like any real provider's key. What is being tested is
        // that the *name* triggers the rule, and a fixture that looks like a live credential trips
        // every secret scanner between here and the repository — teaching people to click past the
        // one warning that matters.
        var log = "STRIPE_API_KEY=not-a-real-key-0000000000\nstarting…";

        var result = AssistantRedaction.Redact(log);

        result.Text.Should().NotContain("not-a-real-key-0000000000");
        result.Text.Should().Contain("STRIPE_API_KEY", "the name is what makes the explanation useful");
    }

    [Fact]
    public void A_password_inside_a_connection_string_goes_and_the_rest_stays()
    {
        // "cannot reach db:5432" is the whole diagnosis; masking the host would throw it away.
        var result = AssistantRedaction.Redact("FATAL: postgres://appuser:s3cr3tp4ss@db:5432/shop refused");

        result.Text.Should().NotContain("s3cr3tp4ss");
        result.Text.Should().Contain("db:5432").And.Contain("appuser");
    }

    [Fact]
    public void A_bearer_token_goes_but_the_request_is_still_readable()
    {
        var result = AssistantRedaction.Redact("GET /v1/me -H 'Authorization: Bearer abcd1234efgh5678ijkl'");

        result.Text.Should().NotContain("abcd1234efgh5678ijkl");
        result.Text.Should().Contain("/v1/me");
    }

    [Fact]
    public void A_jwt_goes()
    {
        const string jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NSJ9.dBjftJeZ4CVPmB92K27uhbUJU1p1r_wW1g";

        AssistantRedaction.Redact($"session={jwt} expired").Text.Should().NotContain(jwt);
    }

    [Fact]
    public void A_private_key_goes_entirely_not_just_its_header()
    {
        // Masking the BEGIN line and leaving the body is the mistake that looks like it worked.
        var log = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAKCAQEAx7Vm\nQmVhcmVy\n-----END RSA PRIVATE KEY-----";

        var result = AssistantRedaction.Redact(log);

        result.Text.Should().NotContain("MIIEowIBAAKCAQEAx7Vm");
        result.Text.Should().NotContain("QmVhcmVy");
    }

    [Fact]
    public void A_quoted_secret_does_not_escape_by_having_spaces_in_it()
    {
        var result = AssistantRedaction.Redact("""DB_PASSWORD="correct horse battery staple" loaded""");

        // Asserted word by word on purpose. A rule that stops at the first space masks only
        // "correct" and leaves "horse battery staple" in the line — and an assertion on the whole
        // phrase passes, because the phrase really is gone. Most of the password is not.
        result.Text.Should().NotContain("correct").And.NotContain("horse")
            .And.NotContain("battery").And.NotContain("staple");
        result.Text.Should().Contain("loaded", "the rest of the line still explains what happened");
    }

    [Fact]
    public void An_image_digest_survives_because_it_is_not_a_secret()
    {
        // The reason this is shape-based rather than "anything long and random": a digest is exactly
        // what somebody reads to understand which image failed.
        const string digest = "sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc";

        AssistantRedaction.Redact($"Status: downloaded {digest}").Text.Should().Contain(digest);
    }

    [Fact]
    public void An_ordinary_build_log_comes_through_untouched()
    {
        // The guard on all of the above: a rule that masks everything is safe and useless.
        const string log = "npm install\nadded 412 packages in 9s\nnpm ERR! missing script: build";

        var result = AssistantRedaction.Redact(log);

        result.Text.Should().Be(log);
        result.Removed.Should().Be(0);
    }

    [Fact]
    public void The_count_is_what_the_person_is_shown_before_they_send()
    {
        var result = AssistantRedaction.Redact("TOKEN=abcdefghijkl\nAPI_SECRET=mnopqrstuvwx");

        result.Removed.Should().Be(2);
    }

    [Fact]
    public void Removing_a_known_secret_counts_towards_that_number()
    {
        // The count is the only evidence the person has that anything happened. Counting shapes but
        // not known values reports "nothing was removed" about a log the password was just taken out
        // of — which reads as "this log was safe all along".
        var result = AssistantRedaction.Redact("started with topsecretvalue twice: topsecretvalue", ["topsecretvalue"]);

        result.Text.Should().NotContain("topsecretvalue");
        result.Removed.Should().Be(2, "both occurrences were removed");
    }

    [Fact]
    public void A_short_known_value_is_not_used_to_shred_the_log()
    {
        // Replacing every "abc" would leave a log nobody can read, and a three-character secret is
        // not a secret.
        var result = AssistantRedaction.Redact("abc happened, then abc again", ["abc"]);

        result.Text.Should().Be("abc happened, then abc again");
    }

    [Fact]
    public void Nothing_in_means_nothing_out()
    {
        AssistantRedaction.Redact(null).Text.Should().BeEmpty();
        AssistantRedaction.Redact("").Removed.Should().Be(0);
    }

    [Fact]
    public void A_secret_that_contains_another_is_still_fully_removed()
    {
        // Replacing the shorter one first would leave the tail of the longer one behind.
        const string shortSecret = "p4ssw0rd";
        const string longSecret = "p4ssw0rd-with-more-after-it";

        var result = AssistantRedaction.Redact($"using {longSecret} now", [shortSecret, longSecret]);

        result.Text.Should().NotContain("with-more-after-it");
    }
}
