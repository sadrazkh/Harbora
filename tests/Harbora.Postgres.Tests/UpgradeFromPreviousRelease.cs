using Harbora.Modules.Backup.Contracts;
using Harbora.Domain.Jobs;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Postgres.Tests;

/// <summary>A database that was carried across the upgrade these branches ship.</summary>
public sealed record UpgradedInstall(string ConnectionString);

/// <summary>
/// The upgrade, performed once: a database at the previous release, carrying the rows a real
/// install could be carrying, migrated the rest of the way.
///
/// <para>
/// Five migrations sit on these branches and two of them contain hand-written SQL, which until this
/// lane existed had never been executed anywhere. Both <b>change rows</b> rather than being additive
/// in the way a column is, and one has to, because the <c>CREATE UNIQUE INDEX</c> that follows would
/// otherwise fail and leave the panel unable to boot. What follows is the set of rows that reaches
/// every branch of every one of those statements — the ones that must be changed, and, just as
/// importantly, the ones that must be left alone.
/// </para>
///
/// <para>
/// It was eleven until billing's seven were squashed into one, and the third hand-written statement
/// went with them: one of those seven existed only to undo a zero that the one before it had been
/// forced to write into every plan and every tier. A migration generated once from the final model
/// adds those columns nullable and writes nothing, so there is nothing left to undo. The plan and
/// the size are still seeded below — "arrives unpriced" is the claim, and it is worth asking of a
/// real upgrade whatever happens to make it true.
/// </para>
///
/// <para>
/// The other three are additive — two new tables, new columns, one new index — and are exercised by
/// being applied at all: <c>An_install_at_the_previous_release_can_be_carried_across</c> migrates
/// this database the whole way, so any of them that cannot run over a populated install fails there.
/// Nothing needs seeding for them, which is why they are absent below rather than forgotten.
/// </para>
///
/// <para>
/// Seeded all at once and upgraded once, rather than a database per case: the migration run is the
/// expensive part, the groups do not interact (different targets, different workspaces), and one
/// upgrade over a mixed table is a closer likeness of the thing being tested than a dozen upgrades
/// over one row each.
/// </para>
/// </summary>
internal static class UpgradeFromPreviousRelease
{
    /// <summary>
    /// The last migration on <c>master</c>, and therefore the schema an operator upgrading to these
    /// branches is coming <i>from</i>.
    /// </summary>
    public const string PreviousRelease = "20260806145158_AlertThresholds";

    /// <summary>
    /// The migrations that follow <see cref="PreviousRelease"/>, in order.
    /// <see cref="MigrationTests"/> pins that they are still these.
    ///
    /// <para>
    /// A prefix, not the whole upgrade: the run itself applies every migration to head, and this
    /// list only fixes where the boundary is. It is deliberately the four that were here before
    /// billing, because those are the ones the seed below is written against — extending it every
    /// time a branch appends a migration would turn a tripwire on the boundary into a second copy of
    /// the migrations folder, and the "can it be carried across" fact already covers the rest by
    /// running them.
    /// </para>
    /// </summary>
    public static readonly string[] Applied =
    [
        "20260807090816_JobNextAttemptAt",
        "20260807132914_JobExclusiveWith",
        "20260807151317_BackupInterruptionRecovery",
        "20260808061352_MetricRollupChartIndex"
    ];

    /// <summary>
    /// Everything the seed writes is dated from here, and every seeded row's <c>UpdatedAt</c> is set
    /// to match its <c>CreatedAt</c>. That is what makes "was this row written to?" answerable
    /// without trusting two clocks against each other: a settled row's <c>UpdatedAt</c> is the
    /// migration's <c>NOW()</c> and is weeks ahead of its own <c>CreatedAt</c>; an untouched row's
    /// is still exactly equal to it.
    /// </summary>
    public static readonly DateTimeOffset BeforeTheUpgrade = DateTimeOffset.UtcNow.AddDays(-30);

    public static async Task<UpgradedInstall> RunAsync(PostgresLane lane)
    {
        var connectionString = await lane.NewDatabaseAsync("upgrade");

        await using (var db = PostgresLane.Open(connectionString))
            await PostgresLane.MigrateToAsync(db, PreviousRelease);

        await using (var connection = await PostgresLane.ConnectAsync(connectionString))
            await SeedAsync(new SchemaSeed(connection));

        // The whole point. If any hand-written statement is wrong — or merely in the wrong order —
        // this throws, and every fact in the upgrade tests reports the failure rather than a
        // misleading assertion further down.
        await using (var db = PostgresLane.Open(connectionString))
            await db.Database.MigrateAsync();

        return new UpgradedInstall(connectionString);
    }

    private static async Task SeedAsync(SchemaSeed seed)
    {
        await SeedDeploymentQueueAsync(seed);
        await SeedBackupSnapshotsAsync(seed);
        await SeedRestoreJobsAsync(seed);
        await SeedTenancyPricingAsync(seed);
        await SeedLogicalDatabaseMigrationAsync(seed);
    }

    // ---------------------------------------------------------------------------------------
    // LogicalDatabases (D1, 2026-08-25 shared-databases plan) — a ManagedService and its
    // attachment exactly as they existed before this shipped, at a schema where
    // ManagedServiceDatabases does not exist yet and AppManagedServices.ManagedServiceDatabaseId
    // has not been added. What the migration must do to these rows is the whole point of D1's
    // own safety requirement: materialise the instance's admin database as its first logical
    // database, re-point the attachment at it, and change nothing an already-attached app reads.
    // ---------------------------------------------------------------------------------------

    private static async Task SeedLogicalDatabaseMigrationAsync(SchemaSeed seed)
    {
        await seed.InsertAsync("Apps",
            ("Id", Seeded.LegacyAttachedApp), ("WorkspaceId", Seeded.WorkspaceOne),
            ("Name", "legacy-app"), ("Slug", "legacy-app"), ("ServerId", Seeded.Server));

        // Type 0 is ManagedServiceType.PostgreSql, Status 1 is ServiceStatus.Running — frozen wire
        // values, spelled as literals for the same reason the deployment-queue seed above does.
        await seed.InsertAsync("ManagedServices",
            ("Id", Seeded.LegacyDatabaseInstance), ("WorkspaceId", Seeded.WorkspaceOne),
            ("ServerId", Seeded.Server), ("Name", "legacy-db"), ("Type", 0), ("Version", "16-alpine"),
            ("Status", 1), ("ContainerName", "harbora-svc-legacy"), ("InternalPort", 5432),
            ("Username", "harbora"), ("EncryptedPassword", "legacy-encrypted-admin-password"),
            ("DatabaseName", "legacy_db"), ("VolumeName", "harbora-svc-legacy-data"));

        // No ManagedServiceDatabaseId here — the column this migration adds does not exist at this
        // schema, and the whole point is that the migration's own backfill is what sets it.
        await seed.InsertAsync("AppManagedServices",
            ("Id", Seeded.LegacyAttachment), ("AppId", Seeded.LegacyAttachedApp),
            ("ManagedServiceId", Seeded.LegacyDatabaseInstance), ("Alias", "LEGACY_DB"),
            ("AttachOrder", 1), ("HasUnpublishedChanges", false));
    }

    // ---------------------------------------------------------------------------------------
    // PayAsYouGoBilling — ADD COLUMN "…Minor" bigint NULL, over rows that predate the price
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// A plan and a size that were on the install before billing existed, so neither can carry a
    /// price. What makes them worth seeding is what the upgrade must leave them holding: nothing.
    ///
    /// <para>
    /// <c>PayAsYouGoBilling</c> adds the rate columns nullable and with no default, so both of these
    /// rows come across null and no statement has to put them right. That is the whole reason the
    /// squash was worth doing: the two migrations it replaced added the columns
    /// <c>NOT NULL DEFAULT 0</c> — the only way to add a required column to a table that already has
    /// rows — and then spent a hand-written <c>UPDATE</c> turning those zeros back into nulls.
    /// </para>
    ///
    /// <para>
    /// Still seeded, because the claim is about the rows rather than about the statement: an
    /// upgraded install must arrive with every price <i>unset</i> rather than at zero, which now
    /// reads as <i>deliberately free</i>, and nothing downstream has any way to know it was never
    /// asked. A migration that declared one of these columns required would put the zeros back, and
    /// this is the pair of rows that would be holding them.
    /// </para>
    /// </summary>
    private static async Task SeedTenancyPricingAsync(SchemaSeed seed)
    {
        await seed.InsertAsync("Plans",
            ("Id", Seeded.PlanCarriedAcross), ("Name", "carried"), ("NameFa", "carried"),
            ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        await seed.InsertAsync("InstanceSizes",
            ("Id", Seeded.SizeCarriedAcross), ("Key", "carried"), ("Name", "carried"), ("NameFa", "carried"),
            ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));
    }

    // ---------------------------------------------------------------------------------------
    // JobExclusiveWith — UPDATE "Jobs" SET "ExclusiveWith" = d."AppId" FROM "Deployments" d …
    // ---------------------------------------------------------------------------------------

    private static async Task SeedDeploymentQueueAsync(SchemaSeed seed)
    {
        await seed.InsertAsync("Apps",
            ("Id", Seeded.AppOne), ("WorkspaceId", Seeded.WorkspaceOne),
            ("Name", "one"), ("Slug", "one"), ("ServerId", Seeded.Server));

        await seed.InsertAsync("Apps",
            ("Id", Seeded.AppTwo), ("WorkspaceId", Seeded.WorkspaceOne),
            ("Name", "two"), ("Slug", "two"), ("ServerId", Seeded.Server));

        // Two deployments of one app — the case ExclusiveWith exists for. Under the serial worker
        // they merely queued behind each other; beside the parallel worker they would build twice.
        await seed.InsertAsync("Deployments",
            ("Id", Seeded.DeploymentOfAppOne), ("AppId", Seeded.AppOne),
            ("WorkspaceId", Seeded.WorkspaceOne), ("Number", 1), ("Status", 0));

        await seed.InsertAsync("Deployments",
            ("Id", Seeded.SecondDeploymentOfAppOne), ("AppId", Seeded.AppOne),
            ("WorkspaceId", Seeded.WorkspaceOne), ("Number", 2), ("Status", 1));

        await seed.InsertAsync("Deployments",
            ("Id", Seeded.DeploymentOfAppTwo), ("AppId", Seeded.AppTwo),
            ("WorkspaceId", Seeded.WorkspaceOne), ("Number", 1), ("Status", 0));

        // Kind 0 is JobKind.Deployment; Status 0 and 1 are Pending and Running. Literals here for
        // the same reason the migration uses them: these are frozen wire values, and the test has to
        // fail if somebody renumbers the enum without a data migration.
        await seed.InsertAsync("Jobs",
            ("Id", Seeded.PendingDeploymentJob), ("Kind", 0), ("Status", 0),
            ("TargetId", Seeded.DeploymentOfAppOne), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        await seed.InsertAsync("Jobs",
            ("Id", Seeded.RunningDeploymentJob), ("Kind", 0), ("Status", 1),
            ("TargetId", Seeded.SecondDeploymentOfAppOne), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        await seed.InsertAsync("Jobs",
            ("Id", Seeded.OtherAppDeploymentJob), ("Kind", 0), ("Status", 0),
            ("TargetId", Seeded.DeploymentOfAppTwo), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        // The deployment row is gone — deleted with its app, most likely. The backfill must leave
        // this null rather than reach for Guid.Empty, which would make it exclude against every
        // other keyless deployment on the platform.
        await seed.InsertAsync("Jobs",
            ("Id", Seeded.OrphanedDeploymentJob), ("Kind", 0), ("Status", 0),
            ("TargetId", Seeded.DeploymentThatWasDeleted), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        // Finished work. Nothing will ever claim these again, so stamping them would be noise.
        await seed.InsertAsync("Jobs",
            ("Id", Seeded.SucceededDeploymentJob), ("Kind", 0), ("Status", 2),
            ("TargetId", Seeded.DeploymentOfAppOne), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        await seed.InsertAsync("Jobs",
            ("Id", Seeded.FailedDeploymentJob), ("Kind", 0), ("Status", 3),
            ("TargetId", Seeded.DeploymentOfAppOne), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));

        // A backup job whose target id happens to be a deployment's. Nothing stops that — they are
        // ids from different tables — and the Kind term is the only thing that keeps the backfill
        // from stamping an app id onto a backup.
        await seed.InsertAsync("Jobs",
            ("Id", Seeded.BackupJobPointingAtADeploymentId), ("Kind", 1), ("Status", 0),
            ("TargetId", Seeded.DeploymentOfAppOne), ("CreatedAt", BeforeTheUpgrade), ("UpdatedAt", BeforeTheUpgrade));
    }

    // ---------------------------------------------------------------------------------------
    // BackupInterruptionRecovery — settling duplicates before IX_BackupSnapshots_ActiveTarget
    // ---------------------------------------------------------------------------------------

    private static async Task SeedBackupSnapshotsAsync(SchemaSeed seed)
    {
        await seed.InsertAsync("BackupRepositories",
            ("Id", Seeded.Repository), ("WorkspaceId", Seeded.WorkspaceOne), ("Name", "primary"));

        // Three active runs of one target: what the old read-then-insert guard could let through.
        // The newest survives; the older two are settled Failed with a reason on them.
        await ActiveSnapshotAsync(seed, Seeded.OldestOfThree, "vol-duplicated",
            BackupSnapshotStatus.Pending, BeforeTheUpgrade.AddHours(-3));
        await ActiveSnapshotAsync(seed, Seeded.MiddleOfThree, "vol-duplicated",
            BackupSnapshotStatus.Preparing, BeforeTheUpgrade.AddHours(-2));
        await ActiveSnapshotAsync(seed, Seeded.NewestOfThree, "vol-duplicated",
            BackupSnapshotStatus.Running, BeforeTheUpgrade.AddHours(-1));

        // Written in the same second, which is not rare — a manual run and the scheduler's. The id
        // is the tie-break, so the larger one survives.
        await ActiveSnapshotAsync(seed, Seeded.TiedSnapshotLoser, "vol-tied",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);
        await ActiveSnapshotAsync(seed, Seeded.TiedSnapshotWinner, "vol-tied",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);

        // One active row, which is the ordinary state and also exactly what a second run of the
        // migration would find. Nothing may happen to it.
        await ActiveSnapshotAsync(seed, Seeded.LoneSnapshot, "vol-alone",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);

        // An active run with finished history on both sides of it. Finished rows are outside the
        // index's filter, so none of these three is a duplicate of another and none may be touched.
        //
        // The newer finished row is the one that makes that claim falsifiable. The EXISTS in the
        // settling statement asks "is there a newer run of this target?" and restricts the search to
        // ACTIVE rows; with only the older finished sibling present, "o.CreatedAt > s.CreatedAt"
        // already excludes it, so the status term inside the EXISTS never decides anything and could
        // be deleted with nothing going red. This row is finished AND an hour newer than the live
        // one, so the CreatedAt term lets it through and the status term is the only thing standing
        // between a running backup and being settled Failed by the upgrade meant to carry it across.
        //
        // Its id is the greater of the pair, following TiedSnapshotLoser/TiedSnapshotWinner — but the
        // tie-break cannot engage here: it only applies where CreatedAt is equal, and these are an
        // hour apart.
        await ActiveSnapshotAsync(seed, Seeded.SnapshotWithHistory, "vol-with-history",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);
        await SnapshotAsync(seed, Seeded.OlderCompletedSnapshotOfTheSameTarget, Seeded.WorkspaceOne,
            BackupTargetType.DockerVolume, "vol-with-history",
            BackupSnapshotStatus.Completed, BeforeTheUpgrade.AddHours(-4));
        await SnapshotAsync(seed, Seeded.NewerCompletedSnapshotOfTheSameTarget, Seeded.WorkspaceOne,
            BackupTargetType.DockerVolume, "vol-with-history",
            BackupSnapshotStatus.Completed, BeforeTheUpgrade.AddHours(1));

        // The same target reference, in another tenant. The index is workspace-scoped, so these are
        // not duplicates of each other and settling either would destroy a live backup.
        await SnapshotAsync(seed, Seeded.OtherWorkspaceSnapshot, Seeded.WorkspaceTwo,
            BackupTargetType.DockerVolume, "vol-duplicated",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);

        // The same reference and the same workspace, but a different kind of target — a directory
        // called "vol-duplicated" is not the volume called "vol-duplicated".
        await SnapshotAsync(seed, Seeded.OtherTargetTypeSnapshot, Seeded.WorkspaceOne,
            BackupTargetType.Directory, "vol-duplicated",
            BackupSnapshotStatus.Running, BeforeTheUpgrade);
    }

    private static Task ActiveSnapshotAsync(
        SchemaSeed seed, Guid id, string targetRef, BackupSnapshotStatus status, DateTimeOffset createdAt) =>
        SnapshotAsync(seed, id, Seeded.WorkspaceOne, BackupTargetType.DockerVolume, targetRef, status, createdAt);

    private static Task SnapshotAsync(
        SchemaSeed seed, Guid id, Guid workspaceId, BackupTargetType targetType,
        string targetRef, BackupSnapshotStatus status, DateTimeOffset createdAt) =>
        seed.InsertAsync("BackupSnapshots",
            ("Id", id), ("WorkspaceId", workspaceId), ("RepositoryId", Seeded.Repository),
            ("TargetType", (int)targetType), ("TargetRef", targetRef),
            ("Status", (int)status), ("CreatedAt", createdAt), ("UpdatedAt", createdAt));

    // ---------------------------------------------------------------------------------------
    // BackupInterruptionRecovery — settling restores, by duplicate destination and by length
    // ---------------------------------------------------------------------------------------

    /// <summary><c>RestoreJob.MaxDestinationLength</c>, spelled out the way the migration spells it.</summary>
    private const int LongestDestinationTheServiceAccepts = 512;

    /// <summary>
    /// Three bytes each in UTF-8. 1024 of them is 3072 bytes, past the ~2704 a btree index row can
    /// hold — so a row like this is precisely what would make <c>CREATE UNIQUE INDEX</c> throw, and
    /// therefore what proves the settling statement runs first.
    /// </summary>
    private const char ThreeByteCharacter = '一';

    private static async Task SeedRestoreJobsAsync(SchemaSeed seed)
    {
        await SnapshotAsync(seed, Seeded.RestorableSnapshot, Seeded.WorkspaceOne,
            BackupTargetType.DockerVolume, "vol-restorable",
            BackupSnapshotStatus.Completed, BeforeTheUpgrade.AddHours(-6));

        // Two restores racing for one directory, which is the state the index exists to prevent.
        await RestoreAsync(seed, Seeded.OlderRestoreOfOneDestination, Seeded.WorkspaceOne,
            "/srv/harbora/restore/duplicated", RestoreJobStatus.Pending, BeforeTheUpgrade.AddHours(-2));
        await RestoreAsync(seed, Seeded.NewerRestoreOfOneDestination, Seeded.WorkspaceOne,
            "/srv/harbora/restore/duplicated", RestoreJobStatus.Running, BeforeTheUpgrade.AddHours(-1));

        // Two tenants, one path on the machine. IX_RestoreJobs_ActiveDestination is deliberately not
        // workspace-scoped, so this IS a duplicate and one of them has to be settled.
        await RestoreAsync(seed, Seeded.RestoreIntoASharedPath, Seeded.WorkspaceOne,
            "/srv/harbora/restore/shared", RestoreJobStatus.Running, BeforeTheUpgrade.AddHours(-2));
        await RestoreAsync(seed, Seeded.OtherTenantsRestoreIntoTheSamePath, Seeded.WorkspaceTwo,
            "/srv/harbora/restore/shared", RestoreJobStatus.Running, BeforeTheUpgrade.AddHours(-1));

        await RestoreAsync(seed, Seeded.TiedRestoreLoser, Seeded.WorkspaceOne,
            "/srv/harbora/restore/tied", RestoreJobStatus.Running, BeforeTheUpgrade);
        await RestoreAsync(seed, Seeded.TiedRestoreWinner, Seeded.WorkspaceOne,
            "/srv/harbora/restore/tied", RestoreJobStatus.Running, BeforeTheUpgrade);

        await RestoreAsync(seed, Seeded.LoneRestore, Seeded.WorkspaceOne,
            "/srv/harbora/restore/alone", RestoreJobStatus.Running, BeforeTheUpgrade);

        // An active restore with a finished one on either side of it, into the same place. Finished
        // rows are the audit trail of a destructive act and are outside the filter; none may be
        // touched.
        //
        // The newer one is here for the same reason as the snapshot above: it is what makes the
        // status term inside the EXISTS decide something. A finished restore that is OLDER than the
        // live one is already excluded by "o.CreatedAt > r.CreatedAt", so it can never demonstrate
        // that the search is restricted to active rows. This one is newer, and without that
        // restriction it would settle a running restore Failed.
        await RestoreAsync(seed, Seeded.RestoreWithHistory, Seeded.WorkspaceOne,
            "/srv/harbora/restore/historic", RestoreJobStatus.Running, BeforeTheUpgrade);
        await RestoreAsync(seed, Seeded.OlderCompletedRestoreOfTheSamePath, Seeded.WorkspaceOne,
            "/srv/harbora/restore/historic", RestoreJobStatus.Completed, BeforeTheUpgrade.AddHours(-5));
        await RestoreAsync(seed, Seeded.NewerCompletedRestoreOfTheSamePath, Seeded.WorkspaceOne,
            "/srv/harbora/restore/historic", RestoreJobStatus.Completed, BeforeTheUpgrade.AddHours(1));

        // Longer than the index can hold. Without the settling UPDATE the migration dies on
        // "index row size … exceeds btree version 4 maximum 2704" and the panel does not boot.
        await RestoreAsync(seed, Seeded.ActiveRestoreWithAnOverLongDestination, Seeded.WorkspaceOne,
            new string(ThreeByteCharacter, 1024), RestoreJobStatus.Pending, BeforeTheUpgrade);

        // The same length, already finished. Outside the index's filter, so it does not break the
        // build — and it is a record of what was written where, which nothing here has any business
        // rewriting.
        await RestoreAsync(seed, Seeded.CompletedRestoreWithAnOverLongDestination, Seeded.WorkspaceOne,
            new string('二', 1024), RestoreJobStatus.Completed, BeforeTheUpgrade);

        // Exactly the bound. The statement says "> 512", so this one stays.
        await RestoreAsync(seed, Seeded.RestoreAtExactlyTheBound, Seeded.WorkspaceOne,
            new string('三', LongestDestinationTheServiceAccepts), RestoreJobStatus.Running, BeforeTheUpgrade);

        // One character past it.
        await RestoreAsync(seed, Seeded.RestoreOneCharacterPastTheBound, Seeded.WorkspaceOne,
            new string('四', LongestDestinationTheServiceAccepts + 1), RestoreJobStatus.Running, BeforeTheUpgrade);
    }

    private static Task RestoreAsync(
        SchemaSeed seed, Guid id, Guid workspaceId, string destination,
        RestoreJobStatus status, DateTimeOffset createdAt) =>
        seed.InsertAsync("RestoreJobs",
            ("Id", id), ("WorkspaceId", workspaceId), ("SnapshotId", Seeded.RestorableSnapshot),
            ("Destination", destination), ("Status", (int)status),
            ("CreatedAt", createdAt), ("UpdatedAt", createdAt));

    /// <summary>
    /// The seeded rows, by name. Written out rather than generated because two of them are a
    /// tie-break — <c>…0001</c> loses to <c>…0002</c> — and a reader has to be able to see that.
    /// </summary>
    internal static class Seeded
    {
        public static readonly Guid WorkspaceOne = new("11111111-0000-0000-0000-000000000001");
        public static readonly Guid WorkspaceTwo = new("11111111-0000-0000-0000-000000000002");
        public static readonly Guid Server = new("55555555-0000-0000-0000-000000000001");

        public static readonly Guid AppOne = new("aaaaaaaa-0000-0000-0000-000000000001");
        public static readonly Guid AppTwo = new("aaaaaaaa-0000-0000-0000-000000000002");

        public static readonly Guid DeploymentOfAppOne = new("dddddddd-0000-0000-0000-000000000001");
        public static readonly Guid SecondDeploymentOfAppOne = new("dddddddd-0000-0000-0000-000000000002");
        public static readonly Guid DeploymentOfAppTwo = new("dddddddd-0000-0000-0000-000000000003");
        public static readonly Guid DeploymentThatWasDeleted = new("dddddddd-0000-0000-0000-0000000000ff");

        public static readonly Guid PendingDeploymentJob = new("10b00000-0000-0000-0000-000000000001");
        public static readonly Guid RunningDeploymentJob = new("10b00000-0000-0000-0000-000000000002");
        public static readonly Guid OtherAppDeploymentJob = new("10b00000-0000-0000-0000-000000000003");
        public static readonly Guid OrphanedDeploymentJob = new("10b00000-0000-0000-0000-000000000004");
        public static readonly Guid SucceededDeploymentJob = new("10b00000-0000-0000-0000-000000000005");
        public static readonly Guid FailedDeploymentJob = new("10b00000-0000-0000-0000-000000000006");
        public static readonly Guid BackupJobPointingAtADeploymentId = new("10b00000-0000-0000-0000-000000000007");

        public static readonly Guid Repository = new("bbbbbbbb-0000-0000-0000-000000000001");

        public static readonly Guid OldestOfThree = new("50000000-0000-0000-0000-000000000001");
        public static readonly Guid MiddleOfThree = new("50000000-0000-0000-0000-000000000002");
        public static readonly Guid NewestOfThree = new("50000000-0000-0000-0000-000000000003");
        public static readonly Guid TiedSnapshotLoser = new("50000000-0000-0000-0000-000000000011");
        public static readonly Guid TiedSnapshotWinner = new("50000000-0000-0000-0000-000000000012");
        public static readonly Guid LoneSnapshot = new("50000000-0000-0000-0000-000000000021");
        public static readonly Guid SnapshotWithHistory = new("50000000-0000-0000-0000-000000000031");
        public static readonly Guid OlderCompletedSnapshotOfTheSameTarget = new("50000000-0000-0000-0000-000000000032");
        public static readonly Guid NewerCompletedSnapshotOfTheSameTarget = new("50000000-0000-0000-0000-000000000033");
        public static readonly Guid OtherWorkspaceSnapshot = new("50000000-0000-0000-0000-000000000041");
        public static readonly Guid OtherTargetTypeSnapshot = new("50000000-0000-0000-0000-000000000051");
        public static readonly Guid RestorableSnapshot = new("50000000-0000-0000-0000-000000000061");

        public static readonly Guid OlderRestoreOfOneDestination = new("60000000-0000-0000-0000-000000000001");
        public static readonly Guid NewerRestoreOfOneDestination = new("60000000-0000-0000-0000-000000000002");
        public static readonly Guid RestoreIntoASharedPath = new("60000000-0000-0000-0000-000000000011");
        public static readonly Guid OtherTenantsRestoreIntoTheSamePath = new("60000000-0000-0000-0000-000000000012");
        public static readonly Guid TiedRestoreLoser = new("60000000-0000-0000-0000-000000000021");
        public static readonly Guid TiedRestoreWinner = new("60000000-0000-0000-0000-000000000022");
        public static readonly Guid LoneRestore = new("60000000-0000-0000-0000-000000000031");
        public static readonly Guid RestoreWithHistory = new("60000000-0000-0000-0000-000000000041");
        public static readonly Guid OlderCompletedRestoreOfTheSamePath = new("60000000-0000-0000-0000-000000000042");
        public static readonly Guid NewerCompletedRestoreOfTheSamePath = new("60000000-0000-0000-0000-000000000043");
        public static readonly Guid ActiveRestoreWithAnOverLongDestination = new("60000000-0000-0000-0000-000000000051");
        public static readonly Guid CompletedRestoreWithAnOverLongDestination = new("60000000-0000-0000-0000-000000000052");
        public static readonly Guid RestoreAtExactlyTheBound = new("60000000-0000-0000-0000-000000000053");
        public static readonly Guid RestoreOneCharacterPastTheBound = new("60000000-0000-0000-0000-000000000054");

        public static readonly Guid PlanCarriedAcross = new("70000000-0000-0000-0000-000000000001");
        public static readonly Guid SizeCarriedAcross = new("70000000-0000-0000-0000-000000000002");

        public static readonly Guid LegacyAttachedApp = new("80000000-0000-0000-0000-000000000001");
        public static readonly Guid LegacyDatabaseInstance = new("80000000-0000-0000-0000-000000000002");
        public static readonly Guid LegacyAttachment = new("80000000-0000-0000-0000-000000000003");
    }
}

/// <summary>Reading the upgraded rows back. Unscoped, because the sweepers that own them are.</summary>
internal static class UpgradedReads
{
    public static async Task<Job> JobAsync(string connectionString, Guid id)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    public static async Task<ManagedService> ManagedServiceAsync(string connectionString, Guid id)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public static async Task<ManagedServiceDatabase> LogicalDatabaseForAsync(string connectionString, Guid managedServiceId)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.ManagedServiceDatabases.AsNoTracking().SingleAsync(d => d.ManagedServiceId == managedServiceId);
    }

    public static async Task<AppManagedService> AttachmentAsync(string connectionString, Guid id)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.AppManagedServices.AsNoTracking()
            .Include(a => a.Database).SingleAsync(a => a.Id == id);
    }

    public static async Task<Modules.Backup.Domain.BackupSnapshot> SnapshotAsync(string connectionString, Guid id)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.BackupSnapshots.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public static async Task<Modules.Backup.Domain.RestoreJob> RestoreAsync(string connectionString, Guid id)
    {
        await using var db = PostgresLane.Open(connectionString);
        return await db.RestoreJobs.AsNoTracking().SingleAsync(r => r.Id == id);
    }
}
