using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Characterization tests for the security primitives. These pin current, correct behavior so the
/// overhaul's hardening work (ADR-009, doc 10) cannot silently regress secret handling.
/// </summary>
public class SecretProtectorTests
{
    // A valid 32-byte base64 key (as the installer generates). Test-only, not a real secret.
    private const string TestKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY="; // 32 bytes

    [Fact]
    public void Protect_then_Unprotect_roundtrips()
    {
        var p = new AesGcmSecretProtector(TestKey);
        const string plain = "super-secret-connection-string";

        var cipher = p.Protect(plain);

        cipher.Should().NotBe(plain, "the stored value must be encrypted");
        p.Unprotect(cipher).Should().Be(plain);
    }

    [Fact]
    public void Protect_is_non_deterministic_due_to_random_nonce()
    {
        var p = new AesGcmSecretProtector(TestKey);
        p.Protect("value").Should().NotBe(p.Protect("value"),
            "a fresh nonce per call must produce different ciphertext");
    }

    [Fact]
    public void Unprotect_with_wrong_key_fails()
    {
        var a = new AesGcmSecretProtector(TestKey);
        var b = new AesGcmSecretProtector("YW5vdGhlci0zMi1ieXRlLWtleS0wMTIzNDU2Nzg5YWI=");

        var cipher = a.Protect("value");

        // GCM authentication must reject a wrong key rather than returning garbage.
        var act = () => b.Unprotect(cipher);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void Tampered_ciphertext_is_rejected()
    {
        var p = new AesGcmSecretProtector(TestKey);
        var cipher = p.Protect("value").ToCharArray();
        cipher[^1] = cipher[^1] == 'A' ? 'B' : 'A'; // flip the last base64 char
        var act = () => p.Unprotect(new string(cipher));
        act.Should().Throw<Exception>("GCM tag verification must fail on tampering");
    }
}

public class PasswordHasherTests
{
    [Fact]
    public void Hash_verifies_correct_password_and_rejects_wrong()
    {
        var h = new Pbkdf2PasswordHasher();
        var hash = h.Hash("correct horse battery staple");

        hash.Should().Contain(".", "format is iterations.salt.hash");
        h.Verify("correct horse battery staple", hash).Should().BeTrue();
        h.Verify("wrong password", hash).Should().BeFalse();
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_differ()
    {
        var h = new Pbkdf2PasswordHasher();
        h.Hash("same").Should().NotBe(h.Hash("same"), "a per-password random salt is required");
    }
}

public class SecretRedactorTests
{
    [Fact]
    public void Redacts_secret_values_from_text()
    {
        var r = new SecretRedactor();
        var redacted = r.Redact("token=abcd1234 in the logs", new[] { "abcd1234" });
        redacted.Should().NotContain("abcd1234");
        redacted.Should().Contain("***");
    }

    [Fact]
    public void Does_not_redact_trivially_short_values()
    {
        // Values shorter than 4 chars are ignored to avoid mangling normal text.
        var r = new SecretRedactor();
        r.Redact("abc def", new[] { "abc" }).Should().Be("abc def");
    }

    [Fact]
    public void Handles_empty_text_and_empty_secrets()
    {
        var r = new SecretRedactor();
        r.Redact("", new[] { "x" }).Should().Be("");
        r.Redact("nothing to hide", Array.Empty<string>()).Should().Be("nothing to hide");
    }
}
