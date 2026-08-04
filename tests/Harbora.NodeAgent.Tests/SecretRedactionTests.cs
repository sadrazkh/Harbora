using FluentAssertions;
using Harbora.NodeAgent.Security;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 8 of the brief: secrets must not appear in a log, a command line, an exception or an
/// unencrypted temporary file. These cover the first and third of those; the runtime tests cover
/// the second, and the identity tests cover the fourth.
/// </summary>
public class SecretRedactionTests
{
    [Fact]
    public void Registered_value_is_removed_wherever_it_appears()
    {
        var redactor = new SecretRedactor();
        redactor.Register("s3cret-value-1234");

        var result = redactor.Redact("connecting with s3cret-value-1234 to the database");

        result.Should().NotContain("s3cret-value-1234");
        result.Should().Contain(SecretRedactor.Mask);
    }

    [Fact]
    public void Longer_secret_is_masked_before_a_shorter_one_it_contains()
    {
        // Masking the short one first would leave the tail of the long one readable — which is
        // exactly the half of a token an attacker needs least help with.
        var redactor = new SecretRedactor();
        redactor.Register("abcdef");
        redactor.Register("abcdefghijkl");

        redactor.Redact("value=abcdefghijkl").Should().NotContain("ghijkl");
    }

    [Fact]
    public void Very_short_values_are_not_registered()
    {
        // Redacting "1" would mask every number in every log line and hide nothing worth hiding.
        var redactor = new SecretRedactor();
        redactor.Register("abc");

        redactor.Redact("abc is a common substring").Should().Contain("abc");
    }

    [Theory]
    [InlineData("postgres://user:hunter2@db:5432/app", "hunter2")]
    [InlineData("PASSWORD=supersecret123", "supersecret123")]
    [InlineData("\"apiKey\": \"ak_live_9182736455\"", "ak_live_9182736455")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("token: ghp_aBcDeFgHiJkLmNoPqRsTuV", "ghp_aBcDeFgHiJkLmNoPqRsTuV")]
    public void Secret_shapes_are_redacted_without_being_registered(string text, string secret)
    {
        // The workload's own output is the case that matters: the agent was never told these
        // values, so a registry-only redactor would pass them straight through.
        new SecretRedactor().Redact(text).Should().NotContain(secret);
    }

    [Fact]
    public void Private_key_blocks_are_collapsed()
    {
        const string pem = """
        -----BEGIN PRIVATE KEY-----
        MIGHAgEAMBMGByqGSM49AgEGCCqGSM49AwEHBG0wawIBAQQg
        -----END PRIVATE KEY-----
        """;

        var result = new SecretRedactor().Redact($"failed to load key:\n{pem}\ndone");

        result.Should().NotContain("MIGHAgEAMBMGByqGSM49");
        result.Should().Contain(SecretRedactor.Mask);
    }

    [Fact]
    public void Secretish_keys_are_masked_by_name_regardless_of_value()
    {
        var redactor = new SecretRedactor();

        var result = redactor.RedactValues(new Dictionary<string, string>
        {
            ["DB_PASSWORD"] = "looks-harmless",
            ["LOG_LEVEL"] = "debug",
            ["CONNECTIONSTRING"] = "Host=db;Username=a",
        });

        result["DB_PASSWORD"].Should().Be(SecretRedactor.Mask);
        result["CONNECTIONSTRING"].Should().Be(SecretRedactor.Mask);
        result["LOG_LEVEL"].Should().Be("debug", "a non-secret key must survive intact or the logs become useless");
    }

    [Fact]
    public void Forgetting_a_rotated_value_stops_masking_it()
    {
        var redactor = new SecretRedactor();
        redactor.Register("old-password-1");
        redactor.Forget("old-password-1");

        redactor.Redact("old-password-1").Should().Be("old-password-1");
    }

    [Fact]
    public void Registry_is_bounded_so_a_long_lived_agent_does_not_grow_forever()
    {
        var redactor = new SecretRedactor();

        for (var i = 0; i < 5_000; i++) redactor.Register($"secret-value-{i:00000}");

        redactor.RegisteredCount.Should().BeLessThanOrEqualTo(2_000);
    }

    [Fact]
    public void Null_and_empty_input_are_handled()
    {
        var redactor = new SecretRedactor();

        redactor.Redact(null).Should().BeEmpty();
        redactor.Redact(string.Empty).Should().BeEmpty();
    }
}
