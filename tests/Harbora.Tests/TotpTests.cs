using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The second factor's arithmetic, checked against RFC 6238's own published vectors — a hand-rolled
/// TOTP that merely agrees with itself would agree with no authenticator app on earth.
/// </summary>
public class TotpTests
{
    /// <summary>The RFC's SHA-1 test secret, "12345678901234567890".</summary>
    private static readonly byte[] RfcKey = System.Text.Encoding.ASCII.GetBytes("12345678901234567890");
    private const string RfcKeyBase32 = "GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ";

    [Theory]
    // RFC 6238 Appendix B, truncated to the 6 digits every authenticator app shows.
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    [InlineData(1111111111, "050471")]
    [InlineData(1234567890, "005924")]
    [InlineData(2000000000, "279037")]
    [InlineData(20000000000, "353130")]
    public void The_rfc_vectors_come_out_exactly(long unixSeconds, string expected)
    {
        Totp.CodeAt(RfcKey, unixSeconds / 30).Should().Be(expected);
    }

    [Theory]
    [InlineData(59, "287082")]
    [InlineData(1111111109, "081804")]
    public void A_correct_code_verifies_at_its_moment(long unixSeconds, string code)
    {
        Totp.Verify(RfcKeyBase32, code, DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Should().BeTrue();
    }

    [Fact]
    public void One_step_of_clock_skew_is_forgiven_and_two_are_not()
    {
        // The code for t=59 belongs to step 1. At step 2 it is one step old — accepted. At step 3
        // — two steps old — it is history, and history verifying is extra validity nobody granted.
        // The two-step boundary is asserted on both sides, because a window widened to 2 passes
        // every other test in this file.
        Totp.Verify(RfcKeyBase32, "287082", DateTimeOffset.FromUnixTimeSeconds(89)).Should().BeTrue();
        Totp.Verify(RfcKeyBase32, "287082", DateTimeOffset.FromUnixTimeSeconds(119)).Should().BeFalse(
            "two steps in the past must not verify");
        // And the future: a code from two steps ahead is a desynchronised secret, not a login.
        Totp.Verify(RfcKeyBase32, Totp.CodeAt(RfcKey, 59 / 30 + 2), DateTimeOffset.FromUnixTimeSeconds(59))
            .Should().BeFalse("two steps in the future must not verify");
    }

    [Fact]
    public void The_secret_is_the_size_the_rfc_asks_for()
    {
        // 160 bits → 32 base32 characters. A shorter secret passes every behavioural test while
        // quietly shrinking the keyspace.
        Totp.GenerateSecret().Should().HaveLength(32);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("28708")]      // five digits
    [InlineData("2870822")]    // seven
    [InlineData("28708a")]     // not digits
    public void Whatever_was_typed_that_is_not_a_code_is_a_plain_no(string? typed)
    {
        Totp.Verify(RfcKeyBase32, typed, DateTimeOffset.FromUnixTimeSeconds(59)).Should().BeFalse();
    }

    [Fact]
    public void A_code_typed_with_spaces_still_counts()
    {
        // Authenticator apps display "287 082"; people copy what they see.
        Totp.Verify(RfcKeyBase32, "287 082", DateTimeOffset.FromUnixTimeSeconds(59)).Should().BeTrue();
    }

    [Fact]
    public void A_garbage_secret_refuses_rather_than_throwing()
    {
        Totp.Verify("not!base32", "123456", DateTimeOffset.UnixEpoch).Should().BeFalse();
    }

    [Fact]
    public void Base32_round_trips_and_matches_the_apps_alphabet()
    {
        var secret = Totp.GenerateSecret();

        secret.Should().MatchRegex("^[A-Z2-7]+$", "that is the only alphabet authenticator apps accept");
        Totp.ToBase32(Totp.FromBase32(secret)).Should().Be(secret);
        Totp.FromBase32(RfcKeyBase32).Should().Equal(RfcKey);
    }

    [Fact]
    public void The_otpauth_uri_survives_an_email_address()
    {
        var uri = Totp.OtpauthUri("Harbora", "user@example.com", "ABC234");

        uri.Should().StartWith("otpauth://totp/Harbora:user%40example.com?secret=ABC234");
        uri.Should().Contain("issuer=Harbora").And.Contain("digits=6").And.Contain("period=30");
    }

    // ---- recovery codes ----

    [Fact]
    public void A_recovery_code_spends_exactly_once()
    {
        var codes = Totp.IssueRecoveryCodes();
        var stored = Totp.StoreRecoveryCodes(codes);

        var (first, remaining) = Totp.ConsumeRecoveryCode(stored, codes[3]);
        first.Should().BeTrue();

        var (second, _) = Totp.ConsumeRecoveryCode(remaining, codes[3]);
        second.Should().BeFalse("the same code read off the same sheet must never work twice");

        var (other, _) = Totp.ConsumeRecoveryCode(remaining, codes[4]);
        other.Should().BeTrue("spending one code must not burn the others");
    }

    [Fact]
    public void Recovery_codes_are_stored_only_as_hashes()
    {
        var codes = Totp.IssueRecoveryCodes();
        var stored = Totp.StoreRecoveryCodes(codes);

        foreach (var code in codes)
            stored.Should().NotContain(code, "the row must not be a list of working codes");
    }

    [Fact]
    public void A_recovery_code_is_forgiven_its_case_and_spacing()
    {
        var codes = Totp.IssueRecoveryCodes();
        var stored = Totp.StoreRecoveryCodes(codes);

        Totp.ConsumeRecoveryCode(stored, " " + codes[0].ToUpperInvariant() + " ").Ok
            .Should().BeTrue("it will be read off paper years from now");
    }

    [Fact]
    public void Ten_codes_all_different()
    {
        Totp.IssueRecoveryCodes().Should().HaveCount(10).And.OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void A_missing_or_broken_store_never_redeems(string? stored)
    {
        Totp.ConsumeRecoveryCode(stored, "abcd-1234").Ok.Should().BeFalse();
    }
}
