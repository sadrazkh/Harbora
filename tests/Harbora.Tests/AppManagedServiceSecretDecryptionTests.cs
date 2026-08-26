using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C1 (2026-08-22 config-delivery plan): the database-attach restatement of
/// <see cref="StorageBucketSecretDecryptionTests"/> — proving the actual production hazard that class
/// documents (a value decrypted once, then handed to a merge step that decrypts <c>IsSecret</c>
/// entries again) cannot happen for <see cref="ManagedServiceAttachEnv.EntriesFor"/>, against the real
/// <see cref="AesGcmSecretProtector"/> rather than a lenient test fake.
///
/// <para>
/// The hazard is sharper here than for a bucket: <see cref="ManagedServiceAttachEnv.EntriesFor"/>
/// itself decrypts <c>ManagedService.EncryptedPassword</c> to compose <c>DATABASE_URL</c> and friends
/// — so unlike <c>BucketEnvKeys.EntriesFor</c> (which never touches <see cref="ISecretProtector"/> at
/// all), this method MUST re-encrypt every composed value that embeds the password before returning
/// it, or the merge's own <c>SafeUnprotect(e.Value)</c> step downstream would be decrypting plaintext.
/// </para>
/// </summary>
public class AppManagedServiceSecretDecryptionTests
{
    private static AesGcmSecretProtector RealProtector() => new("test-master-key-for-database-attach-secrets");

    private static ManagedService GivenService(AesGcmSecretProtector protector, string password) => new()
    {
        Id = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(),
        ServerId = Guid.NewGuid(), Name = "orders", Type = ManagedServiceType.PostgreSql,
        Version = "16-alpine", ContainerName = "harbora-svc-orders", InternalPort = 5432,
        Username = "harbora", EncryptedPassword = protector.Protect(password),
        DatabaseName = "orders", VolumeName = "harbora-svc-orders-data", Status = ServiceStatus.Running
    };

    /// <summary>
    /// D1 (2026-08-25 shared-databases plan): <c>EntriesFor</c> now reads the attachment, not the
    /// bare service — this is the attachment, with no logical database (<c>Database</c> stays null),
    /// which is what falls back to the instance's own admin login exactly as every attachment did
    /// before D1 shipped.
    /// </summary>
    private static AppManagedService GivenAttachment(ManagedService svc, string alias) => new()
    {
        AppId = Guid.NewGuid(), ManagedServiceId = svc.Id, ManagedService = svc, Alias = alias
    };

    [Fact]
    public void Every_entry_that_embeds_the_password_comes_back_as_real_ciphertext_decryptable_exactly_once()
    {
        var protector = RealProtector();
        var svc = GivenService(protector, "correct-horse-battery-staple");

        var entries = ManagedServiceAttachEnv.EntriesFor(GivenAttachment(svc, "ORDERS"), protector);

        var secretEntries = entries.Where(e => e.IsSecret).ToList();
        secretEntries.Should().NotBeEmpty("DATABASE_URL and friends embed the password and must be flagged secret");

        foreach (var entry in secretEntries)
        {
            // The exact seam DeploymentPipeline.BuildEnv calls next: SafeUnprotect(e.Value). A second,
            // accidental decrypt anywhere inside EntriesFor would leave Value as plaintext here, and
            // this single Unprotect call would throw — the real protector authenticates every decrypt
            // and rejects anything that is not its own ciphertext, unlike PipelineHarness's lenient
            // PassthroughProtector fake.
            var act = () => protector.Unprotect(entry.Value);
            act.Should().NotThrow($"{entry.Key} must still be ciphertext when EntriesFor returns it");
        }
    }

    [Fact]
    public void Decrypting_a_secret_entry_yields_the_composed_value_with_the_real_password_in_it()
    {
        var protector = RealProtector();
        var svc = GivenService(protector, "correct-horse-battery-staple");

        var entries = ManagedServiceAttachEnv.EntriesFor(GivenAttachment(svc, "ORDERS"), protector);
        var dsn = entries.Single(e => e.Key == "DATABASE_DSN");

        protector.Unprotect(dsn.Value).Should().Be(
            "Host=harbora-svc-orders;Port=5432;Database=orders;Username=harbora;Password=correct-horse-battery-staple");
    }

    [Fact]
    public void A_value_that_does_not_embed_the_password_is_not_marked_secret_and_stays_plaintext()
    {
        var protector = RealProtector();
        var svc = GivenService(protector, "correct-horse-battery-staple");

        var entries = ManagedServiceAttachEnv.EntriesFor(GivenAttachment(svc, "ORDERS"), protector);
        var host = entries.Single(e => e.Key == "PGHOST");

        host.IsSecret.Should().BeFalse();
        host.Value.Should().Be("harbora-svc-orders", "a value with no password in it needs no protection and must not be encrypted");
    }
}
