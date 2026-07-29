using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Backup archives are encrypted under a key derived from the master key. That derivation must be
/// DETERMINISTIC — it is used to encrypt today and decrypt months later.
///
/// It wasn't: the engine derived it by hashing <c>Protect("…")</c>, which uses a fresh nonce per
/// call, so every archive was sealed with a key that could never be reproduced. The unit tests
/// missed it because the test double returned its input unchanged; only a real backup on a real
/// server surfaced it, as "the computed authentication tag did not match".
/// </summary>
public class KeyDerivationTests
{
    private const string MasterKey = "8Zx1cV1kQeQm1yQ1r0mHhWpN2sYtJqUu3vXbKcLdEfA=";

    [Fact]
    public void The_derived_key_is_the_same_every_time()
    {
        var protector = new AesGcmSecretProtector(MasterKey);

        protector.DeriveKey("backup-archive")
            .Should().Equal(protector.DeriveKey("backup-archive"));
    }

    [Fact]
    public void The_derived_key_survives_a_restart()
    {
        // Two instances = the app before and after a redeploy. An archive written by one must be
        // readable by the other.
        new AesGcmSecretProtector(MasterKey).DeriveKey("backup-archive")
            .Should().Equal(new AesGcmSecretProtector(MasterKey).DeriveKey("backup-archive"));
    }

    [Fact]
    public void Protect_is_deliberately_NOT_deterministic()
    {
        // States why DeriveKey has to exist at all: this is the property that broke the backups.
        var protector = new AesGcmSecretProtector(MasterKey);

        protector.Protect("same input").Should().NotBe(protector.Protect("same input"));
    }

    [Fact]
    public void Different_purposes_get_different_keys()
    {
        var protector = new AesGcmSecretProtector(MasterKey);

        protector.DeriveKey("backup-archive").Should().NotEqual(protector.DeriveKey("something-else"));
    }

    [Fact]
    public void A_different_master_key_yields_a_different_archive_key()
    {
        new AesGcmSecretProtector(MasterKey).DeriveKey("backup-archive")
            .Should().NotEqual(new AesGcmSecretProtector("QmFzZTY0S2V5Rm9yVGVzdGluZ1B1cnBvc2VzMTIzNDU=").DeriveKey("backup-archive"));
    }

    [Fact]
    public void The_derived_key_is_32_bytes()
    {
        new AesGcmSecretProtector(MasterKey).DeriveKey("backup-archive").Should().HaveCount(32,
            "AES-256-GCM needs exactly 32 bytes");
    }
}
