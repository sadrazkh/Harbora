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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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
        var database = "backup-recovery-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(database));
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

        await Reconciler().StartAsync(default);

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

        await Reconciler(backupEnabled: false).StartAsync(default);

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

        var start = async () => await reconciler.StartAsync(default);

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
        snapshot.StagingPath = escape == ".."
            ? Path.Combine(_staging, "..", "live-data")
            : outside;
        Save(snapshot);

        var result = await Reconciler().ReconcileAsync(default);

        Directory.Exists(outside).Should().BeTrue(
            "the sweep may only remove what is inside the module's own staging directory");
        result.StagingPathsSwept.Should().Be(0);
        result.Snapshots.Should().Be(1, "the row is still settled; only the deletion is refused");
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

        var dump = Path.Combine(_staging, $"dbrestore-{job.Id:N}");
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

        // The wire values of Pending, Preparing and Running — the three the queue guard treats as
        // active. Written as numbers because that is what the column holds.
        filter.Should().Contain("Status").And.Contain("0").And.Contain("1").And.Contain("2");
        filter.Should().NotContain("4", "a completed snapshot must never block the next backup");
    }

    [Fact]
    public void At_most_one_active_restore_per_destination_is_a_rule_the_database_holds()
    {
        using var db = PostgresModel();

        var index = db.Model.FindEntityType(typeof(RestoreJob))!.GetIndexes()
            .Should().ContainSingle(i => i.IsUnique).Subject;

        index.Properties.Select(p => p.Name).Should().Equal(nameof(RestoreJob.Destination));

        var filter = index.GetFilter();
        filter.Should().NotBeNullOrWhiteSpace();
        filter.Should().Contain("Status").And.Contain("0").And.Contain("1");
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

        db.RejectTheNextInsert = true;

        var result = await Snapshots(db).QueueAsync(
            _workspace, _repositoryId, BackupTargetType.Directory, _source,
            null, BackupTrigger.Manual, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("already running",
            "the loser of the race is in exactly the situation the pre-check describes");
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

        db.RejectTheNextInsert = true;

        var result = await Restores(db).QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, Path.Combine(_options.RestoreRoot, "x"),
            RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("already running");
    }

    /// <summary>
    /// The model is only half the guarantee: a filtered index that never reaches a migration is a
    /// comment. Read the migration source, the way this repo's other structural tests do.
    /// </summary>
    [Fact]
    public void The_filtered_indexes_reach_a_migration()
    {
        var directory = Path.Combine(
            new DirectoryInfo(TestPaths.WebRoot).Parent!.Parent!.FullName,
            "src", "Harbora.Data", "Migrations");

        var sources = Directory.GetFiles(directory, "*.cs")
            .Where(f => !f.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToList();

        sources.Should().Contain(s =>
                s.Contains("IX_BackupSnapshots_ActiveTarget", StringComparison.Ordinal)
                && s.Contains("filter:", StringComparison.Ordinal),
            "the partial unique index is what makes the guard true under concurrency; without a " +
            "migration it exists only in the model");

        sources.Should().Contain(s =>
            s.Contains("IX_RestoreJobs_ActiveDestination", StringComparison.Ordinal)
            && s.Contains("filter:", StringComparison.Ordinal));
    }

    // --- fixtures -----------------------------------------------------------------------------

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

    /// <summary>Stands in for the partial unique index, which the in-memory provider ignores.</summary>
    private sealed class RejectingContext(DbContextOptions<HarboraDbContext> options)
        : HarboraDbContext(options)
    {
        public bool RejectTheNextInsert { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (!RejectTheNextInsert) return base.SaveChangesAsync(cancellationToken);

            RejectTheNextInsert = false;
            throw new DbUpdateException(
                "23505: duplicate key value violates unique constraint \"IX_BackupSnapshots_ActiveTarget\"");
        }
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => throw new InvalidOperationException("no database");
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
