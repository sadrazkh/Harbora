using FluentAssertions;
using Harbora.Infrastructure.Security;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F5 (2026-08-21 functions-and-services plan): proves the actual production hazard the WIP's
/// original <c>DeploymentPipeline.BuildEnv</c> had — decrypting a bucket's secret key once while
/// building its <c>BucketEnvEntry</c>, then decrypting the already-plaintext result a second time in
/// the merge's own <c>IsSecret ? SafeUnprotect(e.Value) : e.Value</c> line — against the real
/// <see cref="AesGcmSecretProtector"/> rather than a test fake.
///
/// <para>
/// This matters because <c>StorageBucketPipelineTests</c> runs over <c>PipelineHarness</c>'s fake
/// protector (<c>Fakes/PipelineFakes.PassthroughProtector</c>), whose <c>Unprotect</c> is lenient: it
/// only strips a "|nonce:" marker if one is present, and returns anything else unchanged — so a
/// double-<c>Unprotect</c> call is accidentally harmless there and would NOT have caught this bug.
/// The real protector authenticates every decrypt (AES-GCM) and throws on anything that is not its own
/// ciphertext, which is what actually turned the double-decrypt into a swallowed exception and a
/// silently empty <c>S3_SECRET_KEY</c> in production (<c>SafeUnprotect</c>'s catch-all).
/// </para>
/// </summary>
public class StorageBucketSecretDecryptionTests
{
    private static AesGcmSecretProtector RealProtector() => new("test-master-key-for-storage-bucket-secrets");

    [Fact]
    public void Decrypting_ciphertext_once_returns_the_original_secret()
    {
        var protector = RealProtector();
        var ciphertext = protector.Protect("correct-horse-battery-staple");

        protector.Unprotect(ciphertext).Should().Be("correct-horse-battery-staple");
    }

    [Fact]
    public void Decrypting_an_already_decrypted_value_a_second_time_throws_rather_than_returning_it_unchanged()
    {
        // This is exactly the bug: BuildEnv decrypted once building the bucket's env entries, then
        // the merge's own IsSecret branch decrypted the result again. SafeUnprotect's try/catch turned
        // this throw into "", so S3_SECRET_KEY reached every container empty — no build error, no
        // stack trace, just a credential nobody sent.
        var protector = RealProtector();
        var ciphertext = protector.Protect("correct-horse-battery-staple");
        var plaintext = protector.Unprotect(ciphertext);

        var act = () => protector.Unprotect(plaintext);

        act.Should().Throw<Exception>(
            "plaintext is not valid base64/AES-GCM ciphertext, so a second Unprotect call must fail " +
            "rather than silently hand back something else — which is why BucketEnvKeys.EntriesFor takes " +
            "ciphertext, not plaintext, and DeploymentPipeline.BuildEnv decrypts a bucket's secret exactly once");
    }
}
