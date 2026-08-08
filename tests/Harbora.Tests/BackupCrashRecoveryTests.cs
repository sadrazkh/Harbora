using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

// Both namespaces declare an IBackupEngine — the platform's target-oriented service and this
// module's storage-engine port (ARCHITECTURE.md § 2). The stub implements the module's.
using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Tests;

/// <summary>
/// The incident these tests exist for (HARBORA-0003).
///
/// <para>
/// The module refuses a second snapshot while one is already active for a target. Nothing settled
/// the <c>BackupSnapshot</c> row when the process died mid-backup, so ONE hard restart ended that
/// target's backups permanently — manual and scheduled, with no screen anywhere to clear it. The
/// scheduler logged a warning each tick and advanced <c>NextRunAt</c>, so protection stopped and
/// nothing said so. For a backup product that is the worst failure available.
/// </para>
/// <para>
/// The headline test is <see cref="A_target_whose_backup_a_restart_interrupted_can_be_backed_up_again"/>.
/// Everything else here exists to keep it true.
/// </para>
/// </summary>
public sealed class BackupCrashRecoveryTests : IDisposable
{
    private readonly string _root;
    private readonly string _staging;
    private readonly ServiceProvider _sp;
    private readonly BackupModuleOptions _options;
    private readonly RecordingJobQueue _jobs = new();
    private readonly RecordingBackupNotifications _notifications = new();

    private readonly string _database;
    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly Guid _repositoryId = Guid.CreateVersion7();
    private readonly string _source;

    public BackupCrashRecoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-backup-recovery", Guid.NewGuid().ToString("N"));
        _staging = Path.Combine(_root, "staging");
        _source = Path.Combine(_root, "source");
        Directory.CreateDirectory(_staging);
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(Path.Combine(_root, "restore"));

        _options = new BackupModuleOptions
        {
            StagingDirectory = _staging,
            RestoreRoot = Path.Combine(_root, "restore"),
            AllowedSourceRoots = [_source]
        };

        // Named once, outside the lambda: the lambda runs per context, so a name built inside it
        // would give every scope a database of its own and nothing would ever be read back.
        _database = "backup-recovery-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_database));
        _sp = services.BuildServiceProvider();

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        db.BackupRepositories.Add(new BackupRepository
        {
            Id = _repositoryId,
            WorkspaceId = _workspace,
            Name = "Local",
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            BasePath = Path.Combine(_root, "repo"),
            Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        });
        db.SaveChanges();
    }

    // --- the incident -------------------------------------------------------------------------

    /// <summary>
    /// The whole point. A backup was running when the panel was killed; every later backup of that
    /// target was refused for as long as the row said Running. After reconciliation it is accepted.
    /// </summary>
    [Fact]
    public async Task A_target_whose_backup_a_restart_interrupted_can_be_backed_up_again()
    {
        SeedSnapshot(BackupSnapshotStatus.Running);

        var beforeRestart = await Snapshots().QueueAsync(
            _workspace, _repositoryId, BackupTargetType.Directory, _source,
            null, BackupTrigger.Schedule, default);

        beforeRestart.Succeeded.Should().BeFalse("this is the state the restart left behind");
        beforeRestart.Error.Should().Contain("already running");

        await Reconciler().StartingAsync(default);

        var afterRestart = await Snapshots().QueueAsync(
            _workspace, _repositoryId, BackupTargetType.Directory, _source,
            null, BackupTrigger.Schedule, default);

        afterRestart.Succeeded.Should().BeTrue(
            "a crash must not end a target's backups; " + afterRestart.Error);
    }

    // --- settling the rows --------------------------------------------------------------------

    [Theory]
    [InlineData(BackupSnapshotStatus.Pending)]
    [InlineData(BackupSnapshotStatus.Preparing)]
    [InlineData(BackupSnapshotStatus.Running)]
    public async Task A_snapshot_left_mid_flight_is_settled_failed_and_says_a_restart_did_it(
        BackupSnapshotStatus stranded)
    {
        var id = SeedSnapshot(stranded).Id;

        var result = await Reconciler().ReconcileAsync(default);

        result.Snapshots.Should().Be(1);

        var settled = Read().BackupSnapshots.Single(s => s.Id == id);
        settled.Status.Should().Be(BackupSnapshotStatus.Failed);
        settled.CompletedAt.Should().NotBeNull();
        settled.FailureReason.Should().NotBeNullOrWhiteSpace()
            .And.Contain("restart",
                "an operator reading the Backup Center must be told what happened, not just find " +
                "a failure with no cause");
    }

    [Theory]
    [InlineData(BackupSnapshotStatus.Completed)]
    [InlineData(BackupSnapshotStatus.CompletedWithWarnings)]
    [InlineData(BackupSnapshotStatus.Failed)]
    [InlineData(BackupSnapshotStatus.Cancelled)]
    public async Task A_snapshot_that_already_finished_is_left_exactly_as_it_was(
        BackupSnapshotStatus terminal)
    {
        var snapshot = SeedSnapshot(terminal);
        snapshot.FailureReason = "the original reason";
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        result.Snapshots.Should().Be(0);

        var after = Read().BackupSnapshots.Single(s => s.Id == snapshot.Id);
        after.Status.Should().Be(terminal);
        after.FailureReason.Should().Be("the original reason",
            "reconciliation must never overwrite the reason a backup actually failed");
    }

    [Fact]
    public async Task The_reconciler_does_nothing_at_all_when_the_module_is_switched_off()
    {
        var id = SeedSnapshot(BackupSnapshotStatus.Running).Id;

        await Reconciler(backupEnabled: false).StartingAsync(default);

        Read().BackupSnapshots.Single(s => s.Id == id).Status
            .Should().Be(BackupSnapshotStatus.Running,
                "a module that is off owns nothing and must touch nothing");
    }

    /// <summary>
    /// Reconciliation is startup work: it must never be the reason a panel fails to boot. The rows
    /// stay as they are and the host comes up, which is the same bargain <c>JobReconciler</c> makes.
    /// </summary>
    [Fact]
    public async Task A_reconciliation_that_throws_does_not_stop_the_host_from_starting()
    {
        SeedSnapshot(BackupSnapshotStatus.Running);

        var reconciler = new BackupModuleReconciler(
            new ThrowingScopeFactory(),
            Options.Create(new BackupFeatureOptions { Backup = true }),
            Options.Create(_options),
            new FixedClock(DateTimeOffset.UtcNow),
            NullLogger<BackupModuleReconciler>.Instance);

        var start = async () => await reconciler.StartingAsync(default);

        await start.Should().NotThrowAsync();
    }

    // --- the staging sweep --------------------------------------------------------------------

    [Fact]
    public async Task The_staged_copy_of_an_interrupted_snapshot_is_removed()
    {
        var staged = Path.Combine(_staging, "volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "customers.db"), "plaintext application data");

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running, BackupTargetType.DockerVolume, "app-data");
        snapshot.StagingPath = staged;
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(staged).Should().BeFalse(
            "a staged copy is application data in the clear; a crash must not leave it on disk");
        result.StagingPathsSwept.Should().Be(1);
    }

    /// <summary>
    /// The sweep deletes. Anything it can be pointed at that is not the module's own staging area
    /// is somebody's live data — a Directory target's source most obviously — so the root is the
    /// boundary and <c>PathGuard</c> is what enforces it.
    /// </summary>
    [Theory]
    [InlineData("outside")]
    [InlineData("..")]
    public async Task A_path_outside_the_staging_root_is_never_touched(string escape)
    {
        var outside = Path.Combine(_root, "live-data");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "orders.db"), "the customer's actual data");

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running);
        var pointer = escape == ".."
            ? Path.Combine(_staging, "..", "live-data")
            : outside;
        snapshot.StagingPath = pointer;
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(outside).Should().BeTrue(
            "the sweep may only remove what is inside the module's own staging directory");
        result.StagingPathsSwept.Should().Be(0);
        result.Snapshots.Should().Be(1, "the row is still settled; only the deletion is refused");

        Read().BackupSnapshots.Single(s => s.Id == snapshot.Id).StagingPath
            .Should().Be(pointer,
                "refusing to delete it does not make it go away — and a copy outside the staging " +
                "root is the one nobody will find by listing the staging root");
    }

    [Fact]
    public async Task The_staging_root_itself_is_never_deleted()
    {
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running);
        snapshot.StagingPath = _staging;
        Save(snapshot);

        await Reconciler().ReconcileAsync(default);

        Directory.Exists(_staging).Should().BeTrue(
            "sweeping the root would take every other target's staged copy with it");
    }

    // --- restores -----------------------------------------------------------------------------

    [Theory]
    [InlineData(RestoreJobStatus.Pending)]
    [InlineData(RestoreJobStatus.Running)]
    public async Task A_restore_left_mid_flight_is_settled_and_its_destination_released(
        RestoreJobStatus stranded)
    {
        var destination = Path.Combine(_options.RestoreRoot, "orders");
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Completed);
        SeedRestore(stranded, destination, snapshot.Id);

        var blocked = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Fail), default);

        blocked.Succeeded.Should().BeFalse();
        blocked.Error.Should().Contain("already running");

        var result = await Reconciler().ReconcileAsync(default);
        result.Restores.Should().Be(1);

        var settled = Read().RestoreJobs.Single();
        settled.Status.Should().Be(RestoreJobStatus.Failed);
        settled.CompletedAt.Should().NotBeNull();
        settled.FailureReason.Should().NotBeNullOrWhiteSpace().And.Contain("restart");

        var released = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Fail), default);

        released.Succeeded.Should().BeTrue(
            "the destination is free once nothing is restoring into it; " + released.Error);
    }

    [Fact]
    public async Task A_restore_that_already_finished_is_left_alone()
    {
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Completed);
        var job = SeedRestore(RestoreJobStatus.Completed,
            Path.Combine(_options.RestoreRoot, "done"), snapshot.Id);

        var result = await Reconciler().ReconcileAsync(default);

        result.Restores.Should().Be(0);
        Read().RestoreJobs.Single(r => r.Id == job.Id).Status
            .Should().Be(RestoreJobStatus.Completed);
    }

    /// <summary>
    /// A database restore lands its dump in staging first, named by the restore job's own id. That
    /// dump is the whole database in the clear, and the <c>finally</c> that removes it is exactly
    /// what a kill -9 skips.
    /// </summary>
    [Fact]
    public async Task The_dump_a_database_restore_left_in_staging_is_removed()
    {
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Completed);
        var job = SeedRestore(RestoreJobStatus.Running,
            Guid.CreateVersion7().ToString(), snapshot.Id, RestoreType.Database);

        var dump = Path.Combine(_staging, BackupStagingLayout.DatabaseRestoreDirectory(job.Id));
        Directory.CreateDirectory(dump);
        File.WriteAllText(Path.Combine(dump, "dump.sql"), "every row, in the clear");

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(dump).Should().BeFalse();
        result.StagingPathsSwept.Should().Be(1);
    }

    /// <summary>
    /// A file restore writes straight into its destination, which is restored data under the
    /// restore root — not the module's staging area, and never the sweep's to delete.
    /// </summary>
    [Fact]
    public async Task A_file_restores_destination_is_not_swept()
    {
        var destination = Path.Combine(_options.RestoreRoot, "recovered");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "invoice.pdf"), "half a restore is still data");

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Completed);
        SeedRestore(RestoreJobStatus.Running, destination, snapshot.Id);

        await Reconciler().ReconcileAsync(default);

        Directory.Exists(destination).Should().BeTrue(
            "what a restore already wrote belongs to the operator to inspect, not to the reconciler " +
            "to delete");
    }

    // --- the database-level guarantee ---------------------------------------------------------

    /// <summary>
    /// The pre-check in <c>QueueAsync</c> is a read-then-insert: two requests can both pass it. The
    /// partial unique index is what actually holds, and it is declared on the model so the migration
    /// carries it.
    /// </summary>
    [Fact]
    public void At_most_one_active_snapshot_per_target_is_a_rule_the_database_holds()
    {
        using var db = PostgresModel();

        var index = db.Model.FindEntityType(typeof(BackupSnapshot))!.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique).Subject;

        index.Properties.Select(p => p.Name).Should()
            .Equal(nameof(BackupSnapshot.WorkspaceId), nameof(BackupSnapshot.TargetType),
                nameof(BackupSnapshot.TargetRef));

        var filter = index.GetFilter();
        filter.Should().NotBeNullOrWhiteSpace(
            "unfiltered, the index would allow a target exactly one backup ever");

        // The WHOLE filter, not a bag of substrings. "contains 0 and 1 and 2" is also true of
        // IN (10, 12) and of IN (0, 1, 2, 4) — the second of which would turn this index from "one
        // active backup" into "one backup, ever", which is the very failure being fixed.
        // 0 Pending, 1 Preparing, 2 Running: exactly the set QueueAsync treats as active.
        Normalise(filter).Should().Be("\"Status\" IN (0, 1, 2)");
    }

    [Fact]
    public void At_most_one_active_restore_per_destination_is_a_rule_the_database_holds()
    {
        using var db = PostgresModel();

        var index = db.Model.FindEntityType(typeof(RestoreJob))!.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique).Subject;

        index.Properties.Select(p => p.Name).Should().Equal(nameof(RestoreJob.Destination));

        // Whole filter again: 0 Pending, 1 Running, and nothing else.
        Normalise(index.GetFilter()).Should().Be("\"Status\" IN (0, 1)");
    }

    /// <summary>
    /// The indexed value has to fit in a btree index row (~2704 bytes), which 1024 multi-byte
    /// characters would not.
    ///
    /// <para>
    /// The column is deliberately NOT narrowed to make that true. An <c>ALTER COLUMN</c> to a
    /// shorter varchar is not an additive migration: an install that already recorded a longer
    /// destination — a length this column has always permitted — would meet it during boot, and the
    /// rows in question are the audit trail of a destructive operation. So the bound lives in
    /// <c>RestoreService</c>, ahead of the insert, where it costs no migration and cannot invalidate
    /// anything already stored.
    /// </para>
    /// </summary>
    [Fact]
    public void The_destination_this_panel_accepts_is_short_enough_for_the_index_to_hold_it()
    {
        (RestoreJob.MaxDestinationLength * 4).Should().BeLessThan(2704,
            "the worst case is four bytes per character, and a btree index row cannot exceed ~2704");

        using var db = PostgresModel();

        db.Model.FindEntityType(typeof(RestoreJob))!
            .FindProperty(nameof(RestoreJob.Destination))!.GetMaxLength()
            .Should().Be(RestoreJob.StoredDestinationLength,
                "the column keeps the width it shipped with; narrowing it would be a migration that " +
                "can refuse to boot, which is a worse failure than the one being prevented");
    }

    [Fact]
    public async Task A_destination_too_long_to_store_is_refused_in_words_not_as_a_false_conflict()
    {
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Completed);

        // Comfortably inside RestoreRoot, and comfortably past what the column can hold.
        var tooLong = Path.Combine(_options.RestoreRoot, new string('a', 600));

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, tooLong, RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("characters long")
            .And.NotContain("already running",
                "nothing is running there; reporting a conflict would send an operator looking for " +
                "a restore that does not exist");
    }

    /// <summary>
    /// EF's in-memory provider does not enforce a unique index, so the race itself cannot be run
    /// here. What CAN be pinned is the half that is ours: when the store refuses the insert, the
    /// caller must get the same sentence the pre-check gives — not a 500 with a constraint name in it.
    /// </summary>
    [Fact]
    public async Task Losing_the_insert_race_reads_as_the_same_refusal_the_pre_check_gives()
    {
        using var db = new RejectingContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("backup-recovery-race-" + Guid.NewGuid()).Options);

        db.BackupRepositories.Add(new BackupRepository
        {
            Id = _repositoryId, WorkspaceId = _workspace, Name = "Local",
            Type = BackupRepositoryType.Local, Engine = BackupEngineKind.Native,
            BasePath = Path.Combine(_root, "repo"), Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        db.RejectTheNextInsertWith = UniqueViolation("IX_BackupSnapshots_ActiveTarget");

        var result = await Snapshots(db).QueueAsync(
            _workspace, _repositoryId, BackupTargetType.Directory, _source,
            null, BackupTrigger.Manual, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("already running",
            "the loser of the race is in exactly the situation the pre-check describes");
    }

    /// <summary>
    /// The other direction, and the reason the catch is qualified at all.
    ///
    /// <para>
    /// "A backup of this target is already running" is a statement about the world, and it has to be
    /// true. An unqualified <c>catch (DbUpdateException)</c> says it about every refusal this insert
    /// can meet — a pruned repository, a check constraint, a serialisation failure, a connection
    /// that dropped — and sends an operator looking for a backup that does not exist while the real
    /// fault goes unreported. That is silent degradation with a friendly face.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_store_failure_that_is_not_a_conflict_is_not_reported_as_one()
    {
        using var db = new RejectingContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("backup-recovery-race-" + Guid.NewGuid()).Options);

        db.BackupRepositories.Add(new BackupRepository
        {
            Id = _repositoryId, WorkspaceId = _workspace, Name = "Local",
            Type = BackupRepositoryType.Local, Engine = BackupEngineKind.Native,
            BasePath = Path.Combine(_root, "repo"), Status = BackupRepositoryStatus.Ready,
            IsEnabled = true
        });
        await db.SaveChangesAsync();

        db.RejectTheNextInsertWith = ForeignKeyViolation();

        var queue = async () => await Snapshots(db).QueueAsync(
            _workspace, _repositoryId, BackupTargetType.Directory, _source,
            null, BackupTrigger.Manual, default);

        await queue.Should().ThrowAsync<DbUpdateException>(
            "a failure the module cannot explain must surface as itself, not be dressed up as a " +
            "conflict that is not happening");
    }

    [Fact]
    public async Task A_restore_losing_the_insert_race_reads_as_the_same_refusal()
    {
        using var db = new RejectingContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("backup-recovery-race-" + Guid.NewGuid()).Options);

        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace, RepositoryId = _repositoryId,
            TargetType = BackupTargetType.Directory, TargetRef = _source,
            EngineSnapshotId = Guid.CreateVersion7().ToString("N"),
            Status = BackupSnapshotStatus.Completed
        };
        db.BackupSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        db.RejectTheNextInsertWith = UniqueViolation("IX_RestoreJobs_ActiveDestination");

        var result = await Restores(db).QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, Path.Combine(_options.RestoreRoot, "x"),
            RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("already running");
    }

    /// <summary>
    /// The restore half of the same rule. This one matters more, not less: a restore is the
    /// destructive direction, and "a restore into this destination is already running" told about a
    /// snapshot that was pruned between the read and the write would have an operator hunting for a
    /// concurrent restore instead of reading the fault they actually hit.
    /// </summary>
    [Fact]
    public async Task A_restore_refused_for_some_other_reason_is_not_reported_as_a_conflict()
    {
        using var db = new RejectingContext(
            new DbContextOptionsBuilder<HarboraDbContext>()
                .UseInMemoryDatabase("backup-recovery-race-" + Guid.NewGuid()).Options);

        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace, RepositoryId = _repositoryId,
            TargetType = BackupTargetType.Directory, TargetRef = _source,
            EngineSnapshotId = Guid.CreateVersion7().ToString("N"),
            Status = BackupSnapshotStatus.Completed
        };
        db.BackupSnapshots.Add(snapshot);
        await db.SaveChangesAsync();

        db.RejectTheNextInsertWith = ForeignKeyViolation();

        var queue = async () => await Restores(db).QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, Path.Combine(_options.RestoreRoot, "x"),
            RestoreConflictStrategy.Fail), default);

        await queue.Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>
    /// The model is only half the guarantee: a filtered index that never reaches a migration is a
    /// comment. Read the migration source, the way this repo's other structural tests do.
    /// </summary>
    [Fact]
    public void The_filtered_indexes_reach_a_migration()
    {
        var sources = MigrationSources();

        sources.Should().Contain(s =>
                s.Contains("IX_BackupSnapshots_ActiveTarget", StringComparison.Ordinal)
                && s.Contains("filter:", StringComparison.Ordinal),
            "the partial unique index is what makes the guard true under concurrency; without a " +
            "migration it exists only in the model");

        sources.Should().Contain(s =>
            s.Contains("IX_RestoreJobs_ActiveDestination", StringComparison.Ordinal)
            && s.Contains("filter:", StringComparison.Ordinal));
    }

    /// <summary>
    /// The upgrade must not be able to refuse to boot on a row the old schema permitted.
    ///
    /// <para>
    /// <c>IX_RestoreJobs_ActiveDestination</c> is a btree over <c>Destination</c>, and a btree index
    /// row cannot exceed roughly 2704 bytes. The column has always held 1024 characters, which in
    /// UTF-8 can be 4096 — so an install carrying an <b>active</b> restore with a long multi-byte
    /// destination meets <c>index row size … exceeds btree version 4 maximum 2704</c> during
    /// <c>CREATE UNIQUE INDEX</c>, and a migration that throws is a panel that will not start. That
    /// is the same shape — "a row the previous schema permitted breaks the migration" — that a
    /// narrowing <c>ALTER COLUMN</c> was deleted from this branch for having.
    /// </para>
    /// <para>
    /// <c>RestoreService</c>'s 512-character bound is on the insert, so it does nothing for rows
    /// written before the upgrade. The migration settles those first, exactly as it settles the
    /// duplicates the old read-then-insert guard let through: additive, deletes nothing, writes a
    /// reason on the row, and the row it settles is an active one the new bound could not re-create
    /// anyway.
    /// </para>
    /// </summary>
    [Fact]
    public void An_active_destination_too_long_for_the_index_is_settled_before_the_index_is_built()
    {
        var source = MigrationSources()
            .Single(s => s.Contains("IX_RestoreJobs_ActiveDestination", StringComparison.Ordinal));

        var settles = source.IndexOf("length(\"Destination\")", StringComparison.Ordinal);
        settles.Should().BeGreaterThan(-1,
            "nothing else stops an install holding a 1024-character active destination from " +
            "meeting CREATE UNIQUE INDEX as a failed boot");

        source.Should().Contain($"> {RestoreJob.MaxDestinationLength}",
            "the migration must settle exactly the rows the service bound would refuse, so an " +
            "upgraded install cannot hold a row it can no longer create");

        // The CreateIndex call itself, not the name wherever it is discussed in a comment.
        var buildsIndex = source.IndexOf(
            "name: \"IX_RestoreJobs_ActiveDestination\"", StringComparison.Ordinal);

        settles.Should().BeLessThan(buildsIndex,
            "settling after the index is created is settling after the boot already failed");

        source.Should().NotContain("RAISE",
            "a guard that stops the migration is the failure being fixed, not a fix for it");
    }

    // --- ahead of the worker --------------------------------------------------------------------

    /// <summary>
    /// The reconciler has to finish before anything that could touch these rows is started.
    ///
    /// <para>
    /// Hosted services run their <c>StartAsync</c> in registration order, and this module is
    /// registered <b>after</b> <c>AddHarboraInfrastructure</c> — which is where the job worker's
    /// startup gate is opened. So the registration order below is the real one, deliberately: the
    /// worker is released first. <c>BackupSnapshot</c> carries no concurrency token, so EF writes
    /// every changed column; a worker that finished a snapshot between this pass's read and its
    /// save would have <c>Completed</c> overwritten back to <c>Failed</c> — a backup that exists,
    /// with data sitting in the repository, recorded as never taken. That is the inverse of the
    /// honest-failure rule this whole task serves.
    /// </para>
    /// <para>
    /// <c>IHostedLifecycleService.StartingAsync</c> is what closes it: the host runs it on every
    /// hosted service before any <c>StartAsync</c>, so the order below stops mattering.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Nothing_the_host_releases_can_see_a_row_this_pass_has_not_settled()
    {
        SeedSnapshot(BackupSnapshotStatus.Running);
        var worker = new ReleaseObservation();

        using var host = new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_database));
                services.AddSingleton(Options.Create(new BackupFeatureOptions { Backup = true }));
                services.AddSingleton(Options.Create(_options));
                services.AddSingleton<ISystemClock>(new FixedClock(DateTimeOffset.UtcNow));

                // Registered FIRST, exactly as the real composition does it.
                services.AddSingleton<IHostedService>(sp =>
                    new WorkerReleaseSpy(sp.GetRequiredService<IServiceScopeFactory>(), worker));
                services.AddHostedService<BackupModuleReconciler>();
            })
            .Build();

        await host.StartAsync();
        await host.StopAsync();

        worker.StatusWhenReleased.Should().Be(BackupSnapshotStatus.Failed,
            "a worker released while the row still said Running could complete it and have this " +
            "pass overwrite the result");
    }

    /// <summary>
    /// …and none of it holds the port shut.
    ///
    /// <para>
    /// <c>StartingAsync</c> runs on every hosted service before <b>any</b> <c>StartAsync</c>,
    /// including Kestrel's — so whatever it does happens before the listener binds. Nothing sets
    /// <c>HostOptions.StartupTimeout</c>, whose default is infinite, and a blocking syscall would
    /// not be abortable if it did: <c>Directory.Exists</c> and <c>Directory.Delete</c> on a wedged
    /// NFS or SMB mount do not return an error, they hang. A staging directory on a network mount is
    /// an ordinary choice for backups.
    /// </para>
    /// <para>
    /// So the pass is in two halves. Settling the rows is fast, cancellable, and it is the half that
    /// races the job worker — it stays ahead of the gate. Sweeping the disk only touches directories
    /// of rows this pass has already settled, so moving it behind the listener re-races nothing.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Startup_settles_the_rows_and_leaves_the_disk_to_a_pass_behind_the_listener()
    {
        var staged = Path.Combine(_staging, "volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "customers.db"), "plaintext application data");

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running, BackupTargetType.DockerVolume, "app-data");
        snapshot.StagingPath = staged;
        Save(snapshot);

        var reconciler = Reconciler();

        await reconciler.StartingAsync(default);

        Read().BackupSnapshots.Single(s => s.Id == snapshot.Id).Status
            .Should().Be(BackupSnapshotStatus.Failed,
                "settling is what races the worker, so it is the half that has to be finished " +
                "before anything is released");
        Directory.Exists(staged).Should().BeTrue(
            "the listener does not bind until this returns, and a recursive delete over a wedged " +
            "mount does not return at all");

        await reconciler.StartedAsync(default);
        await reconciler.StagingSwept;

        Directory.Exists(staged).Should().BeFalse(
            "the copy is still plaintext application data and still has to go — a moment later, " +
            "with the panel answering requests while it does");
        Read().BackupSnapshots.Single(s => s.Id == snapshot.Id).StagingPath
            .Should().BeNull("the copy is gone, so the claim that one exists goes too");
    }

    [Fact]
    public async Task A_module_that_is_off_starts_no_background_sweep_either()
    {
        var staged = Path.Combine(_staging, "volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running);
        snapshot.StagingPath = staged;
        Save(snapshot);

        var reconciler = Reconciler(backupEnabled: false);
        await reconciler.StartingAsync(default);
        await reconciler.StartedAsync(default);
        await reconciler.StagingSwept;

        Directory.Exists(staged).Should().BeTrue(
            "a module that is off owns nothing, and that has to hold for the half that deletes too");
    }

    // --- the windows the sweep used to miss -----------------------------------------------------

    /// <summary>
    /// The staged copy is named from the snapshot, so it is findable from the row from the moment
    /// the copy STARTS — not from when it finishes. That distinction is the whole fix: the row's
    /// own <c>StagingPath</c> is not written until <c>AcquireAsync</c> returns, and copying is both
    /// the longest phase and the one that moves the data.
    /// </summary>
    [Theory]
    [InlineData(BackupTargetType.DockerVolume)]
    [InlineData(BackupTargetType.Database)]
    [InlineData(BackupTargetType.Application)]
    public async Task A_copy_still_being_staged_when_the_process_died_is_found_from_the_row(
        BackupTargetType targetType)
    {
        var snapshot = SeedSnapshot(
            BackupSnapshotStatus.Preparing, targetType, targetType.ToString());

        snapshot.StagingPath.Should().BeNull(
            "this is the row as a kill mid-copy leaves it — nothing has been written to it yet");

        var leaked = Path.Combine(
            _staging, BackupStagingLayout.StagedDirectoryFor(targetType, snapshot.Id)!);
        Directory.CreateDirectory(leaked);
        File.WriteAllText(Path.Combine(leaked, "half-copied.db"), "plaintext application data");

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(leaked).Should().BeFalse(
            "a directory named from a fresh Guid could never be found again by anything");
        result.StagingPathsSwept.Should().Be(1);
    }

    /// <summary>
    /// A second execution of a snapshot that is still in flight must not touch its staged copy.
    ///
    /// <para>
    /// <c>SnapshotLifecycle.CanTransition</c> returns true when <c>from == to</c>, deliberately:
    /// re-applying the state a snapshot already holds is how an idempotent retry behaves, and
    /// treating it as illegal would make every crash-and-resume look like a bug. Naming the staging
    /// directory from the snapshot id gave that permissiveness a consequence it did not have before
    /// — the stagers clear the directory before creating it, so a duplicate dispatch against a row
    /// still <c>Preparing</c> would delete exactly what the live execution is filling. The archive
    /// that survived would be a mixture of two moments with nothing saying so, which is the same
    /// failure the "clear it first" rule exists to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_second_run_of_a_snapshot_already_in_flight_leaves_its_staged_copy_alone()
    {
        var snapshot = SeedSnapshot(
            BackupSnapshotStatus.Preparing, BackupTargetType.DockerVolume, "app-data");

        // The directory the execution that is already running is filling right now.
        var staged = Path.Combine(_staging, BackupStagingLayout.VolumeDirectory(snapshot.Id));
        Directory.CreateDirectory(staged);
        var partial = Path.Combine(staged, "half-copied.db");
        File.WriteAllText(partial, "the first execution is still writing this");

        await Snapshots().RunAsync(snapshot.Id, default);

        File.Exists(partial).Should().BeTrue(
            "the execution already staging into this directory owns it; clearing it would fold half " +
            "of one backup into another");

        Read().BackupSnapshots.Single(s => s.Id == snapshot.Id).Status
            .Should().Be(BackupSnapshotStatus.Preparing,
                "the row belongs to the run that is still going, not to this one");
    }

    [Fact]
    public void A_directory_target_never_yields_a_path_for_the_sweep_to_delete()
    {
        BackupStagingLayout.StagedDirectoryFor(BackupTargetType.Directory, Guid.CreateVersion7())
            .Should().BeNull(
                "a directory target's source is the operator's own live data, and the sweep must " +
                "never be handed a path to it");
    }

    /// <summary>
    /// The engine tars the target into staging and encrypts it beside itself, removing both in a
    /// <c>finally</c> — which is exactly what a process kill skips. The unencrypted one is a
    /// plaintext archive of the entire target.
    /// </summary>
    [Fact]
    public async Task The_archive_the_engine_was_writing_when_the_process_died_is_removed()
    {
        var snapshot = SeedSnapshot(BackupSnapshotStatus.Running);

        var plain = Path.Combine(_staging, BackupStagingLayout.ArchiveFile(snapshot.Id));
        var encrypted = Path.Combine(_staging, BackupStagingLayout.EncryptedArchiveFile(snapshot.Id));
        File.WriteAllText(plain, "a tar.gz of the whole target, in the clear");
        File.WriteAllText(encrypted, "the same bytes, encrypted");

        var result = await Reconciler().ReconcileAsync(default);

        File.Exists(plain).Should().BeFalse(
            "an unencrypted archive of everything the target held must not survive the crash");
        File.Exists(encrypted).Should().BeFalse();
        result.StagingPathsSwept.Should().Be(2);
    }

    /// <summary>
    /// The other half of the same bargain, for reads rather than writes.
    ///
    /// <para>
    /// A browse and a restore each decrypt the artifact under a name of their own, which is what
    /// keeps two of them from deleting each other's copy — and it means the copy a kill abandons no
    /// longer gets overwritten by the next read of the same snapshot. It belongs to no row, so
    /// nothing points at it: a full plaintext archive with no way to find it but this.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_copy_a_read_was_decrypting_when_the_process_died_is_removed()
    {
        var snapshotId = Guid.CreateVersion7();
        var abandoned = Path.Combine(
            _staging, BackupStagingLayout.ReadArchiveFile(snapshotId, Guid.CreateVersion7()));
        var downloaded = Path.Combine(
            _staging, BackupStagingLayout.FetchedArchiveFile(snapshotId, Guid.CreateVersion7()));

        File.WriteAllText(abandoned, "a decrypted archive of the whole snapshot, in the clear");
        File.WriteAllText(downloaded, "the artifact this read had fetched");

        var reconciler = Reconciler();
        await reconciler.StartingAsync(default);
        await reconciler.StartedAsync(default);
        await reconciler.StagingSwept;

        File.Exists(abandoned).Should().BeFalse(
            "a plaintext archive nothing has a pointer to is the least discoverable leak there is");
        File.Exists(downloaded).Should().BeFalse();
    }

    /// <summary>
    /// And it is only ever read at the one moment nothing can be mid-read. <c>ReconcileAsync</c> is
    /// a "reconcile now" verb with no such guarantee, so it must not touch these — deleting the copy
    /// a live browse is part-way through is the exact fault the per-read naming exists to prevent.
    /// </summary>
    [Fact]
    public async Task Reconciling_on_demand_leaves_a_read_in_progress_alone()
    {
        var live = Path.Combine(
            _staging, BackupStagingLayout.ReadArchiveFile(Guid.CreateVersion7(), Guid.CreateVersion7()));
        File.WriteAllText(live, "a restore is reading this right now");

        await Reconciler().ReconcileAsync(default);

        File.Exists(live).Should().BeTrue();
    }

    // --- a delete that failed is retried, not forgotten ------------------------------------------

    /// <summary>
    /// A previous pass settled this row and could not remove the copy — a busy mount, a held
    /// handle. It kept the pointer, which is the only thing that can lead anyone back to a leaked
    /// plaintext copy of somebody's data. This pass is the retry.
    /// </summary>
    [Fact]
    public async Task A_settled_row_that_still_points_at_a_staged_copy_is_swept_on_the_next_restart()
    {
        var staged = Path.Combine(_staging, "volume-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staged);
        File.WriteAllText(Path.Combine(staged, "customers.db"), "plaintext application data");

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Failed);
        snapshot.FailureReason = "the original reason";
        snapshot.StagingPath = staged;
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(staged).Should().BeFalse();
        result.StagingPathsSwept.Should().Be(1);
        result.Snapshots.Should().Be(0, "the row was already settled; only the leak was outstanding");

        var after = Read().BackupSnapshots.Single(s => s.Id == snapshot.Id);
        after.StagingPath.Should().BeNull("the copy is gone, so the claim that one exists goes too");
        after.FailureReason.Should().Be("the original reason");
    }

    [Fact]
    public void A_delete_that_failed_keeps_its_pointer_so_the_next_restart_can_try_again()
    {
        BackupModuleReconciler.RemainingStagingPath([
                new SweepAttempt("/staging/volume-a", SweepResult.Removed),
                new SweepAttempt("/staging/volume-b", SweepResult.Failed)
            ])
            .Should().Be("/staging/volume-b",
                "clearing it would throw away the only pointer to a plaintext copy still on disk");
    }

    [Fact]
    public void A_sweep_that_left_nothing_behind_clears_the_pointer()
    {
        BackupModuleReconciler.RemainingStagingPath([
                new SweepAttempt("/staging/volume-a", SweepResult.Removed),
                new SweepAttempt("/staging/volume-b", SweepResult.Gone)
            ])
            .Should().BeNull();
    }

    /// <summary>
    /// A refused path is the one the pointer matters MOST for.
    ///
    /// <para>
    /// "Permanent verdict" is true about sweepability and says nothing about existence. A copy the
    /// sweep will not touch is a copy that is still there — and because it is outside the staging
    /// root, it is also the one nobody will find by listing that root. Clearing the column would
    /// delete the only record of where a plaintext copy of somebody's application data is sitting.
    /// </para>
    /// </summary>
    [Fact]
    public void A_path_the_sweep_refuses_keeps_its_pointer_because_the_copy_is_still_there()
    {
        BackupModuleReconciler.RemainingStagingPath([
                new SweepAttempt("/staging/volume-a", SweepResult.Removed),
                new SweepAttempt("/somewhere/else", SweepResult.Refused)
            ])
            .Should().Be("/somewhere/else",
                "the sweep refusing to delete it does not make it stop existing, and nothing else " +
                "records where it is");
    }

    /// <summary>
    /// A pointer at a copy that is not there any more must be able to clear itself.
    ///
    /// <para>
    /// The realistic way a pointer ends up outside the staging root is not an attack: it is an
    /// operator changing <c>BackupModuleOptions.StagingDirectory</c>. Every path written under the
    /// old root is then outside the new one, so the sweep refuses all of them — permanently, warning
    /// on every boot about copies a housekeeping job may have removed years ago. That is a warning
    /// nobody can act on and everybody learns to ignore.
    /// </para>
    /// <para>
    /// A refusal answers "may this sweep delete it", which is a different question from "is it still
    /// there" — and the second one is a single cheap stat. Asking it is what makes
    /// <c>SweepResult.Refused</c>'s promise ("the copy is still there") true rather than assumed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_pointer_at_a_copy_that_is_gone_clears_itself_even_where_the_sweep_may_not_touch_it()
    {
        // The shape a changed StagingDirectory leaves: a path under a root this module no longer
        // uses, and nothing at the other end of it.
        var vanished = Path.Combine(_root, "staging-as-it-was", "volume-" + Guid.NewGuid().ToString("N"));

        var snapshot = SeedSnapshot(BackupSnapshotStatus.Failed);
        snapshot.FailureReason = "the original reason";
        snapshot.StagingPath = vanished;
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        result.StagingPathsSwept.Should().Be(0, "there was nothing there to remove");

        var after = Read().BackupSnapshots.Single(s => s.Id == snapshot.Id);
        after.StagingPath.Should().BeNull(
            "no copy is at that path, so the row must stop claiming one is — a refusal that " +
            "repeats forever about data that is already gone teaches operators to ignore it");
        after.FailureReason.Should().Be("the original reason");
    }

    // --- fixtures -----------------------------------------------------------------------------

    /// <summary>
    /// Every migration's source. The generated designer files and the model snapshot are excluded:
    /// they describe the model the migrations arrive at, not anything that runs against a database,
    /// and the snapshot names every index — so leaving it in would let a model-only index satisfy a
    /// test whose whole point is that the index reaches a migration.
    /// </summary>
    private static List<string> MigrationSources() =>
        Directory.GetFiles(
                Path.Combine(
                    new DirectoryInfo(TestPaths.WebRoot).Parent!.Parent!.FullName,
                    "src", "Harbora.Data", "Migrations"),
                "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal)
                        && !f.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

    /// <summary>Whitespace-insensitive, so reformatting a filter is not a test failure.</summary>
    private static string Normalise(string? filter) =>
        string.Join(' ', (filter ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private BackupModuleReconciler Reconciler(bool backupEnabled = true) => new(
        _sp.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new BackupFeatureOptions { Backup = backupEnabled }),
        Options.Create(_options),
        new FixedClock(DateTimeOffset.UtcNow),
        NullLogger<BackupModuleReconciler>.Instance);

    private HarboraDbContext Read()
    {
        var scope = _sp.CreateScope();
        _scopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
    }

    private readonly List<IServiceScope> _scopes = [];

    private BackupSnapshotService Snapshots(HarboraDbContext? db = null) => new(
        db ?? Read(),
        new StubEngineResolver(),
        new StubCredentialReader(),
        new BackupTargetResolver(new FakeDockerEngine(), new StubDatabaseStager(),
            new StubApplicationStager(), Options.Create(_options),
            NullLogger<BackupTargetResolver>.Instance),
        _jobs, _notifications, new StubCaller(_workspace), new NoopAudit(),
        NullLogger<BackupSnapshotService>.Instance);

    private RestoreService Restores(HarboraDbContext? db = null) => new(
        db ?? Read(),
        new StubEngineResolver(),
        new StubCredentialReader(),
        new StubDatabaseRestores(),
        new BackupTargetResolver(new FakeDockerEngine(), new StubDatabaseStager(),
            new StubApplicationStager(), Options.Create(_options),
            NullLogger<BackupTargetResolver>.Instance),
        _jobs, _notifications, new StubCaller(_workspace), new NoopAudit(),
        Options.Create(_options), NullLogger<RestoreService>.Instance);

    private BackupSnapshot SeedSnapshot(
        BackupSnapshotStatus status,
        BackupTargetType targetType = BackupTargetType.Directory,
        string? targetRef = null)
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace,
            RepositoryId = _repositoryId,
            TargetType = targetType,
            TargetRef = targetRef ?? _source,
            Status = status,
            EngineSnapshotId = status is BackupSnapshotStatus.Completed
                ? Guid.CreateVersion7().ToString("N") : null
        };

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        db.BackupSnapshots.Add(snapshot);
        db.SaveChanges();
        return snapshot;
    }

    private RestoreJob SeedRestore(
        RestoreJobStatus status, string destination, Guid snapshotId,
        RestoreType type = RestoreType.Folder)
    {
        var job = new RestoreJob
        {
            WorkspaceId = _workspace,
            SnapshotId = snapshotId,
            RestoreType = type,
            Destination = destination,
            Status = status,
            RequestedByUserId = Guid.CreateVersion7()
        };

        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        db.RestoreJobs.Add(job);
        db.SaveChanges();
        return job;
    }

    private void Save(BackupSnapshot snapshot)
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        db.BackupSnapshots.Update(snapshot);
        db.SaveChanges();
    }

    /// <summary>
    /// The model as PostgreSQL sees it — no connection is opened. A filtered index is relational
    /// metadata the in-memory provider does not carry at all.
    /// </summary>
    private static HarboraDbContext PostgresModel() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options);

    public void Dispose()
    {
        foreach (var scope in _scopes) scope.Dispose();
        _sp.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    // --- stubs --------------------------------------------------------------------------------

    /// <summary>
    /// Stands in for the store, which the in-memory provider cannot make refuse anything.
    ///
    /// <para>
    /// Takes the exception to throw rather than a bool, because the thing under test is precisely
    /// the services' ability to tell ONE refusal from every other: a unique violation means "already
    /// running" and nothing else does.
    /// </para>
    /// </summary>
    private sealed class RejectingContext(DbContextOptions<HarboraDbContext> options)
        : HarboraDbContext(options)
    {
        public DbUpdateException? RejectTheNextInsertWith { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (RejectTheNextInsertWith is not { } refusal)
                return base.SaveChangesAsync(cancellationToken);

            RejectTheNextInsertWith = null;
            throw refusal;
        }
    }

    /// <summary>
    /// What PostgreSQL raises when the partial unique index rejects the second active row — the
    /// shape EF wraps it in, an outer <c>DbUpdateException</c> over a <c>PostgresException</c>.
    /// </summary>
    private static DbUpdateException UniqueViolation(string constraint) =>
        new("An error occurred while saving the entity changes.",
            new PostgresException(
                $"duplicate key value violates unique constraint \"{constraint}\"",
                "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

    /// <summary>
    /// A different store-level refusal of the same insert. The snapshot was pruned between the
    /// pre-check's read and this write, so the foreign key has nothing to point at — nothing
    /// whatever to do with something already running.
    /// </summary>
    private static DbUpdateException ForeignKeyViolation() =>
        new("An error occurred while saving the entity changes.",
            new PostgresException(
                "insert or update on table violates foreign key constraint",
                "ERROR", "ERROR", PostgresErrorCodes.ForeignKeyViolation));

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no database");
    }

    /// <summary>What the snapshot looked like at the moment the worker was let go.</summary>
    private sealed class ReleaseObservation
    {
        public BackupSnapshotStatus? StatusWhenReleased { get; set; }
    }

    /// <summary>
    /// Stands in for <c>JobStartupGateOpener</c>: a plain <c>IHostedService</c> whose
    /// <c>StartAsync</c> is the instant the job worker may start claiming. What it reads is what a
    /// worker could act on.
    /// </summary>
    private sealed class WorkerReleaseSpy(IServiceScopeFactory scopes, ReleaseObservation observed)
        : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

            observed.StatusWhenReleased = db.BackupSnapshots.IgnoreQueryFilters()
                .Select(s => (BackupSnapshotStatus?)s.Status).FirstOrDefault();

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class StubCaller(Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();
        public string? Email => "tester@harbora.test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class NoopAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class StubCredentialReader : IRepositoryCredentialReader
    {
        public Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken cancellationToken)
            => Task.FromResult<string?>("password");

        public Task<RepositoryCredentials?> GetCredentialsAsync(Guid repositoryId, CancellationToken cancellationToken)
            => Task.FromResult<RepositoryCredentials?>(null);
    }

    private sealed class StubEngineResolver : IBackupEngineResolver
    {
        public IReadOnlyCollection<BackupEngineKind> Available => [BackupEngineKind.Native];
        public IBackupEngine Resolve(BackupEngineKind kind) => new StubEngine();
    }

    private sealed class StubEngine : IBackupEngine
    {
        public BackupEngineKind Kind => BackupEngineKind.Native;

        public Task<BackupRepositoryResult> CreateRepositoryAsync(CreateBackupRepositoryRequest r, CancellationToken ct)
            => Task.FromResult(new BackupRepositoryResult(true, r.RepositoryId, false));

        public Task<BackupSnapshotResult> CreateSnapshotAsync(CreateBackupSnapshotRequest r, CancellationToken ct)
            => Task.FromResult(new BackupSnapshotResult(true, r.SnapshotId, r.SnapshotId.ToString("N"), 10, 5, 0, 1));

        public Task<RestoreResult> RestoreAsync(RestoreBackupRequest r, CancellationToken ct)
            => Task.FromResult(new RestoreResult(true, 1, 10, r.DestinationPath));

        public Task<BackupRepositoryHealthResult> CheckHealthAsync(Guid id, CancellationToken ct)
            => Task.FromResult(new BackupRepositoryHealthResult(true, true));

        public Task<IReadOnlyList<EngineSnapshot>> ListSnapshotsAsync(ListSnapshotsRequest r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EngineSnapshot>>([]);

        public Task<IReadOnlyList<EngineEntry>> BrowseSnapshotAsync(BrowseSnapshotRequest r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EngineEntry>>([]);

        public Task<EngineOperationResult> DeleteSnapshotAsync(DeleteSnapshotRequest r, CancellationToken ct)
            => Task.FromResult(new EngineOperationResult(true));
    }
}
