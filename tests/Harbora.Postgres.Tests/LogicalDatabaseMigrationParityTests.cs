using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Services;
using Xunit;
using static Harbora.Postgres.Tests.UpgradeFromPreviousRelease;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The <c>LogicalDatabases</c> migration's backfill (D1, 2026-08-25 shared-databases plan), against
/// a real ManagedService and a real AppManagedService seeded exactly as they existed before the
/// migration — this is the fact the plan names as mattering more than any other in this sub-project:
/// <b>an app attached before the migration receives byte-identical environment after it.</b>
///
/// <para>
/// "Byte-identical" is checked two ways. The data-layer facts below prove the new
/// <c>ManagedServiceDatabase</c> row carries the exact same <c>Name</c>/<c>Username</c>/
/// <c>EncryptedPassword</c> the instance already had — a copy, not a new credential — and that the
/// pre-existing attachment is re-pointed at it. <see cref="An_attached_apps_resolved_connection_string_is_unchanged_by_the_migration"/>
/// closes the loop by running the real <see cref="AttachedServiceConnectionResolver"/> the config
/// merge actually calls, and comparing its answer to what the old, pre-D1 scheme would have produced
/// from the very same seeded row — which is exactly the sentence that used to be built straight from
/// <c>ManagedService.Username</c>/<c>EncryptedPassword</c>/<c>DatabaseName</c>, with nothing else
/// involved (see <c>AttachedServiceConnectionResolverTests</c> for the fast-suite half of this same
/// resolver, which proves the identical fallback for a service that never gets a logical database at
/// all — Redis, RabbitMQ, NATS, or one this migration has not reached).
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class LogicalDatabaseMigrationParityTests(PostgresLane lane)
{
    /// <summary>Round-trips as-is — the seeded "EncryptedPassword" is not real ciphertext, so a real
    /// protector would reject it. What matters here is that whatever is stored is copied verbatim.</summary>
    private sealed class IdentityProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => throw new NotSupportedException();
    }

    [PostgresFact]
    public async Task The_instances_existing_admin_database_becomes_its_one_default_logical_database()
    {
        var upgraded = await lane.UpgradedAsync();
        var service = await UpgradedReads.ManagedServiceAsync(upgraded.ConnectionString, Seeded.LegacyDatabaseInstance);

        var logical = await UpgradedReads.LogicalDatabaseForAsync(upgraded.ConnectionString, Seeded.LegacyDatabaseInstance);

        logical.IsDefault.Should().BeTrue("this is the instance's own admin database, not one created on request");
        logical.Name.Should().Be(service.DatabaseName, "a copy, not a new database — nothing was created against the engine");
        logical.Username.Should().Be(service.Username);
        logical.EncryptedPassword.Should().Be(service.EncryptedPassword,
            "the exact ciphertext the instance already had, not a freshly generated credential");
    }

    [PostgresFact]
    public async Task An_attachment_that_predates_the_migration_is_re_pointed_at_the_default_database()
    {
        var upgraded = await lane.UpgradedAsync();

        var attachment = await UpgradedReads.AttachmentAsync(upgraded.ConnectionString, Seeded.LegacyAttachment);
        var logical = await UpgradedReads.LogicalDatabaseForAsync(upgraded.ConnectionString, Seeded.LegacyDatabaseInstance);

        attachment.ManagedServiceDatabaseId.Should().Be(logical.Id,
            "every attachment that existed before this migration must end up pointing at its instance's new default database");
        attachment.Database.Should().NotBeNull("the navigation must resolve, not just the bare id");
    }

    [PostgresFact]
    public async Task An_attached_apps_resolved_connection_string_is_unchanged_by_the_migration()
    {
        var upgraded = await lane.UpgradedAsync();
        var service = await UpgradedReads.ManagedServiceAsync(upgraded.ConnectionString, Seeded.LegacyDatabaseInstance);

        // The old scheme, spelled out exactly as ServiceCatalog.All[PostgreSql].Conn built it before
        // D1 — straight from the ManagedService row, nothing else involved. This is the answer an
        // app attached under the pre-migration code would have received.
        var expected = $"postgresql://{service.Username}:{service.EncryptedPassword}@{service.ContainerName}:{service.InternalPort}/{service.DatabaseName}";

        await using var db = PostgresLane.Open(upgraded.ConnectionString);
        var resolver = new AttachedServiceConnectionResolver(db, new IdentityProtector());

        var resolved = await resolver.ResolveAsync(Seeded.LegacyAttachedApp, "LEGACY_DB", default);

        resolved.Should().Be(expected,
            "an app attached before the migration must read exactly the same connection string after it");
    }
}
