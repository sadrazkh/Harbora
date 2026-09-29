using Microsoft.EntityFrameworkCore;
using static Harbora.Postgres.Tests.UpgradeFromPreviousRelease;

namespace Harbora.Postgres.Tests;

/// <summary>
/// An install carried across <c>20260826192041_LogicalDatabases</c> — D1's own upgrade, at its own
/// boundary.
///
/// <para>
/// This seed used to live inside <see cref="UpgradeFromPreviousRelease"/>, which migrates to
/// <see cref="UpgradeFromPreviousRelease.PreviousRelease"/> (<c>20260806145158_AlertThresholds</c>)
/// before seeding. <c>AppManagedServices</c> is created by <c>20260823233909_AppManagedServices</c>,
/// seventeen days later, so the seed could never have run: its INSERT named a table that did not exist
/// yet, the shared upgrade threw, and every test built on it — thirty of them across the interruption
/// settling, the job backfill, billing, migration and this parity check — failed together. Nobody saw
/// it because CI had not loaded since 8 August, and this check had been counted as "proven in the
/// Postgres lane" by a lane that never ran.
/// </para>
///
/// <para>
/// A migration's upgrade test has to seed at the schema immediately before <em>that</em> migration.
/// For D1 that is <see cref="Boundary"/>, at which <c>Apps.EnvironmentId</c> is already required
/// (<c>20260817214810_EnvironmentRequired</c>) — so a project and an environment are seeded first and
/// named by id, rather than left for <see cref="SchemaSeed"/> to fill with an all-zeros uuid that the
/// foreign key would refuse.
/// </para>
/// </summary>
internal static class UpgradeAcrossLogicalDatabases
{
    /// <summary>The last migration before <c>20260826192041_LogicalDatabases</c>.</summary>
    public const string Boundary = "20260825012838_MemoryOvercommitFactor";

    private static readonly Guid LegacyProject = new("80000000-0000-0000-0000-000000000010");
    private static readonly Guid LegacyEnvironment = new("80000000-0000-0000-0000-000000000011");

    public static async Task<UpgradedInstall> RunAsync(PostgresLane lane)
    {
        var connectionString = await lane.NewDatabaseAsync("upgrade_logical_databases");

        await using (var db = PostgresLane.Open(connectionString))
            await PostgresLane.MigrateToAsync(db, Boundary);

        await using (var connection = await PostgresLane.ConnectAsync(connectionString))
            await SeedAsync(new SchemaSeed(connection));

        await using (var db = PostgresLane.Open(connectionString))
            await db.Database.MigrateAsync();

        return new UpgradedInstall(connectionString);
    }

    /// <summary>
    /// A ManagedService and its attachment exactly as they existed before D1 shipped, at a schema where
    /// <c>ManagedServiceDatabases</c> does not exist yet and <c>AppManagedServices.ManagedServiceDatabaseId</c>
    /// has not been added. What the migration must do to these rows is D1's own safety requirement:
    /// materialise the instance's admin database as its first logical database, re-point the attachment
    /// at it, and change nothing an already-attached app reads.
    /// </summary>
    private static async Task SeedAsync(SchemaSeed seed)
    {
        await seed.InsertAsync("Projects",
            ("Id", LegacyProject), ("WorkspaceId", Seeded.WorkspaceOne), ("Name", "legacy"), ("Slug", "legacy"));

        await seed.InsertAsync("Environments",
            ("Id", LegacyEnvironment), ("WorkspaceId", Seeded.WorkspaceOne), ("ProjectId", LegacyProject),
            ("Name", "production"), ("Slug", "production"), ("IsDefault", true));

        await seed.InsertAsync("Apps",
            ("Id", Seeded.LegacyAttachedApp), ("WorkspaceId", Seeded.WorkspaceOne),
            ("EnvironmentId", LegacyEnvironment),
            ("Name", "legacy-app"), ("Slug", "legacy-app"), ("ServerId", Seeded.Server));

        // Type 0 is ManagedServiceType.PostgreSql, Status 1 is ServiceStatus.Running — frozen wire
        // values, spelled as literals so renumbering the enum without a data migration fails here.
        await seed.InsertAsync("ManagedServices",
            ("Id", Seeded.LegacyDatabaseInstance), ("WorkspaceId", Seeded.WorkspaceOne),
            ("EnvironmentId", LegacyEnvironment),
            ("ServerId", Seeded.Server), ("Name", "legacy-db"), ("Type", 0), ("Version", "16-alpine"),
            ("Status", 1), ("ContainerName", "harbora-svc-legacy"), ("InternalPort", 5432),
            ("Username", "harbora"), ("EncryptedPassword", "legacy-encrypted-admin-password"),
            ("DatabaseName", "legacy_db"), ("VolumeName", "harbora-svc-legacy-data"));

        // No ManagedServiceDatabaseId — the column the migration adds does not exist at this schema,
        // and the migration's own backfill is what sets it.
        await seed.InsertAsync("AppManagedServices",
            ("Id", Seeded.LegacyAttachment), ("AppId", Seeded.LegacyAttachedApp),
            ("ManagedServiceId", Seeded.LegacyDatabaseInstance), ("Alias", "LEGACY_DB"),
            ("AttachOrder", 1), ("HasUnpublishedChanges", false));
    }
}
