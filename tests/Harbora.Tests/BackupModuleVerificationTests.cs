using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// Both namespaces declare an IBackupEngine — the platform's target-oriented service and this
// module's storage-engine port (ARCHITECTURE.md § 2). The scripted stub implements the module's.
using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Tests;

/// <summary>
/// The two promises the module made and did not keep (HARBORA-0013, HARBORA-0014).
///
/// <para>
/// <b>A backup nobody checked is a backup nobody has.</b> <c>JobKind.BackupVerify</c> had a handler
/// and no caller, so every module snapshot stayed <c>NotVerified</c> for ever while the Backup
/// Center rendered that column as though silence meant something.
/// </para>
/// <para>
/// <b>A restore with no way back is a deletion with extra steps.</b> <c>RestoreJob.SafetySnapshotRef</c>
/// was a mapped column nothing ever assigned; the legacy engine takes a copy of the destination
/// first and refuses to start when it cannot, and the module that is meant to replace it did not.
/// </para>
/// <para>
/// The headline tests are <see cref="A_completed_snapshot_is_verified_without_anyone_asking"/> and
/// <see cref="A_restore_that_cannot_take_a_safety_copy_does_not_happen"/>. Everything else exists to
/// keep those two true.
/// </para>
/// </summary>
public sealed class BackupModuleVerificationTests : IDisposable
{
    private readonly string _root;
    private readonly string _source;
    private readonly HarboraDbContext _db;
    private readonly RecordingJobQueue _jobs = new();
    private readonly RecordingBackupNotifications _notifications = new();
    private readonly ScriptedEngine _engine = new();
    private readonly BackupModuleOptions _options;
    private readonly BackupRepository _repository;
    private readonly Guid _workspace = Guid.CreateVersion7();

    /// <summary>Scopes for the reconciler, over the same store the services use.</summary>
    private readonly ServiceProvider _sp;

    public BackupModuleVerificationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-backup-verify", Guid.NewGuid().ToString("N"));
        _source = Path.Combine(_root, "source");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(Path.Combine(_root, "restore"));
        Directory.CreateDirectory(Path.Combine(_root, "staging"));

        // Named once, outside the lambda: a name built inside it would give every scope a database
        // of its own and nothing would ever be read back.
        var database = "backup-verify-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(database));
        _sp = services.BuildServiceProvider();

        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(database).Options);

        _options = new BackupModuleOptions
        {
            RestoreRoot = Path.Combine(_root, "restore"),
            StagingDirectory = Path.Combine(_root, "staging"),
            AllowedSourceRoots = [_source]
        };

        _repository = new BackupRepository
        {
            WorkspaceId = _workspace,
            Name = "Local",
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            BasePath = Path.Combine(_root, "repo"),
            Status = BackupRepositoryStatus.Ready
        };
        _db.BackupRepositories.Add(_repository);
        _db.SaveChanges();
    }

    // --- verification -------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the first half. Nobody presses anything: a snapshot that finished is
    /// checked, and the row says so.
    /// </summary>
    [Fact]
    public async Task A_completed_snapshot_is_verified_without_anyone_asking()
    {
        _engine.Entries = [Entry("app.conf")];

        var queued = await Snapshots().QueueAsync(
            _workspace, _repository.Id, BackupTargetType.Directory, _source,
            null, BackupTrigger.Manual, default);
        queued.Succeeded.Should().BeTrue(queued.Error);

        await Snapshots().RunAsync(queued.SnapshotId!.Value, default);

        var verification = _jobs.Enqueued.Should()
            .ContainSingle(j => j.Kind == JobKind.BackupVerify,
                "a completed snapshot must hand itself to the verifier").Subject;
        verification.TargetId.Should().Be(queued.SnapshotId!.Value);

        // And the handler that job names actually settles the row.
        await Verifier().ExecuteAsync(verification.TargetId, default);

        var snapshot = await _db.BackupSnapshots.FirstAsync(s => s.Id == queued.SnapshotId);
        snapshot.VerificationStatus.Should().Be(BackupVerificationStatus.Passed);
        snapshot.VerifiedAt.Should().NotBeNull();
        snapshot.Status.Should().Be(BackupSnapshotStatus.Completed,
            "verification records itself in VerificationStatus and must not move the snapshot's own state");
    }

    /// <summary>
    /// The failure this check exists to find: the row claims files, the repository has none of them.
    /// </summary>
    [Fact]
    public async Task A_snapshot_that_claims_files_the_engine_cannot_list_is_recorded_as_failed()
    {
        var snapshot = AddCompletedSnapshot(filesCount: 12);
        _engine.Entries = [];

        await Verifier().ExecuteAsync(snapshot.Id, default);

        var checked_ = await _db.BackupSnapshots.FirstAsync(s => s.Id == snapshot.Id);
        checked_.VerificationStatus.Should().Be(BackupVerificationStatus.Failed);
        checked_.VerificationNote.Should().Contain("none can be read back");
        _notifications.Sent.Should().ContainSingle(n =>
            n.Kind == BackupNotificationKind.SnapshotVerificationFailed);
    }

    /// <summary>
    /// A failed check must not read as a failed backup. The snapshot itself is untouched — the data
    /// is in the repository, the row says so, and only the verification column changed.
    /// </summary>
    [Fact]
    public async Task A_failed_verification_does_not_fail_the_backup()
    {
        var snapshot = AddCompletedSnapshot(filesCount: 12);
        _engine.Entries = [];

        await Verifier().ExecuteAsync(snapshot.Id, default);

        var checked_ = await _db.BackupSnapshots.FirstAsync(s => s.Id == snapshot.Id);
        checked_.Status.Should().Be(BackupSnapshotStatus.Completed);
        checked_.FailureReason.Should().BeNull("nothing about the BACKUP failed");
    }

    /// <summary>"Verify now": an operator can ask again, and the answer replaces the old one.</summary>
    [Fact]
    public async Task Verify_now_re_runs_the_check_and_updates_the_row()
    {
        var snapshot = AddCompletedSnapshot(filesCount: 12);
        snapshot.VerificationStatus = BackupVerificationStatus.Failed;
        snapshot.VerificationNote = "The backup recorded files but none can be read back.";
        await _db.SaveChangesAsync();

        var controller = Center();
        var response = await controller.VerifySnapshot(snapshot.Id, default);

        response.Should().BeOfType<RedirectToActionResult>();
        var job = _jobs.Enqueued.Should()
            .ContainSingle(j => j.Kind == JobKind.BackupVerify).Subject;
        job.TargetId.Should().Be(snapshot.Id);

        _engine.Entries = [Entry("app.conf")];
        await Verifier().ExecuteAsync(job.TargetId, default);

        var rechecked = await _db.BackupSnapshots.FirstAsync(s => s.Id == snapshot.Id);
        rechecked.VerificationStatus.Should().Be(BackupVerificationStatus.Passed);
        rechecked.VerificationNote.Should().NotContain("none can be read back");
    }

    [Fact]
    public async Task Verify_now_refuses_a_snapshot_that_never_completed()
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace,
            RepositoryId = _repository.Id,
            TargetType = BackupTargetType.Directory,
            TargetRef = _source,
            Status = BackupSnapshotStatus.Failed
        };
        _db.BackupSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();

        var result = await Snapshots().QueueVerificationAsync(snapshot.Id, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("completed");
        _jobs.Enqueued.Should().BeEmpty();
    }

    /// <summary>The routes do not exist while the module is off, rather than existing and refusing.</summary>
    [Fact]
    public async Task Verify_now_is_not_a_route_while_the_module_is_off()
    {
        var snapshot = AddCompletedSnapshot(filesCount: 1);

        var response = await Center(enabled: false).VerifySnapshot(snapshot.Id, default);

        response.Should().BeOfType<NotFoundResult>();
        _jobs.Enqueued.Should().BeEmpty();
    }

    /// <summary>The button an operator presses has to be on the page the column is on.</summary>
    [Fact]
    public void The_backup_center_offers_verify_now_beside_the_verification_column()
    {
        var markup = File.ReadAllText(
            Path.Combine(TestPaths.WebRoot, "Views", "BackupCenter", "Index.cshtml"));

        markup.Should().Contain("asp-action=\"VerifySnapshot\"",
            "a column that can say 'not verified' needs a way to change that");
    }

    // --- the way back -------------------------------------------------------------------------

    /// <summary>
    /// The whole point of the second half. The copy could not be taken, so the restore did not
    /// start — and the destination still holds exactly what it held before.
    /// </summary>
    [Fact]
    public async Task A_restore_that_cannot_take_a_safety_copy_does_not_happen()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        _engine.CreateOutcome = _ => new BackupSnapshotResult(
            false, Guid.Empty, Error: "the repository is full");

        await Restores().RunAsync(job, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        row.Status.Should().Be(RestoreJobStatus.Failed);
        row.FailureReason.Should().Contain("was not started");
        row.FailureReason.Should().Contain("nothing at the destination was changed");
        row.FailureReason.Should().Contain("the repository is full");

        _engine.Calls.Should().NotContain(c => c.StartsWith("restore:", StringComparison.Ordinal),
            "a restore with no way back must never reach the engine");
        (await File.ReadAllTextAsync(Path.Combine(destination, "in-use.txt")))
            .Should().Be("production data");
    }

    [Fact]
    public async Task A_restore_that_overwrites_live_data_records_where_the_safety_copy_went()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        await Restores().RunAsync(job, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        row.Status.Should().Be(RestoreJobStatus.Completed, row.FailureReason);
        row.SafetySnapshotRef.Should().NotBeNullOrWhiteSpace();

        Guid.TryParse(row.SafetySnapshotRef, out var safetyId).Should().BeTrue(
            "the reference has to lead somewhere an operator can actually restore from");

        var safety = await _db.BackupSnapshots.FirstAsync(s => s.Id == safetyId);
        safety.TriggeredBy.Should().Be(BackupTrigger.Safety);
        safety.TargetRef.Should().Be(destination);
        safety.Status.Should().Be(BackupSnapshotStatus.Completed);
        safety.PolicyId.Should().BeNull("a safety copy belongs to no schedule and is never pruned by one");
    }

    /// <summary>Order is the whole guarantee: copy first, overwrite second.</summary>
    [Fact]
    public async Task The_safety_copy_is_taken_before_anything_is_written()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        await Restores().RunAsync(job, default);

        var create = _engine.Calls.FindIndex(c => c.StartsWith("create:", StringComparison.Ordinal));
        var restore = _engine.Calls.FindIndex(c => c.StartsWith("restore:", StringComparison.Ordinal));

        create.Should().BeGreaterThanOrEqualTo(0);
        restore.Should().BeGreaterThan(create);
    }

    /// <summary>
    /// The moment the reference earns its keep: the restore broke, and the operator is told where
    /// the copy of what used to be there is.
    /// </summary>
    [Fact]
    public async Task A_restore_that_fails_after_the_safety_copy_says_where_it_is()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        _engine.RestoreOutcome = _ => new RestoreResult(false, Error: "the archive ended early");

        await Restores().RunAsync(job, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        row.Status.Should().Be(RestoreJobStatus.Failed);
        row.SafetySnapshotRef.Should().NotBeNullOrWhiteSpace();
        row.FailureReason.Should().Contain(row.SafetySnapshotRef!,
            "an operator reading the failure must not have to go and find the way back");
    }

    /// <summary>
    /// Nothing live, nothing to copy. A restore into an empty directory pays no safety cost — and a
    /// repository that refuses one must not therefore refuse the restore.
    /// </summary>
    [Fact]
    public async Task A_restore_into_an_empty_destination_takes_no_safety_copy()
    {
        var destination = Path.Combine(_options.RestoreRoot, "fresh");
        var snapshot = AddCompletedSnapshot(filesCount: 3);

        var queued = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Fail), default);
        queued.Succeeded.Should().BeTrue(queued.Error);

        _engine.CreateOutcome = _ => new BackupSnapshotResult(
            false, Guid.Empty, Error: "the repository is full");

        await Restores().RunAsync(queued.RestoreJobId!.Value, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == queued.RestoreJobId);
        row.Status.Should().Be(RestoreJobStatus.Completed, row.FailureReason);
        row.SafetySnapshotRef.Should().BeNull();
        _engine.Calls.Should().NotContain(c => c.StartsWith("create:", StringComparison.Ordinal));
    }

    /// <summary>The safety copy is a snapshot like any other, so it gets checked like any other.</summary>
    [Fact]
    public async Task The_safety_copy_is_handed_to_the_verifier_too()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");
        _jobs.Enqueued.Clear();

        await Restores().RunAsync(job, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        _jobs.Enqueued.Should().ContainSingle(j =>
            j.Kind == JobKind.BackupVerify && j.TargetId == Guid.Parse(row.SafetySnapshotRef!));
    }

    /// <summary>
    /// The case that needs the reference most. A restart caught the restore part-way, so something
    /// may already be on disk — and the row that says so also says what can be put back.
    /// </summary>
    [Fact]
    public async Task A_restore_a_restart_interrupted_still_names_its_safety_copy()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        await Restores().RunAsync(job, default);

        // The state a hard restart leaves behind: the copy was taken and recorded, and the row is
        // still claiming to be running because nothing got to finish it.
        var interrupted = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        interrupted.SafetySnapshotRef.Should().NotBeNullOrWhiteSpace();
        interrupted.Status = RestoreJobStatus.Running;
        interrupted.CompletedAt = null;
        interrupted.FailureReason = null;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await Reconciler().SettleAsync(default);

        var settled = await _db.RestoreJobs.AsNoTracking().FirstAsync(r => r.Id == job);
        settled.Status.Should().Be(RestoreJobStatus.Failed);
        settled.FailureReason.Should().Contain("may already have been written");
        settled.FailureReason.Should().Contain(settled.SafetySnapshotRef!,
            "the restore that was cut in half is the one that most needs its way back named");
    }

    /// <summary>
    /// The reason is trimmed to make room; the pointer to the only copy of the previous contents is
    /// the part that must survive the column bound.
    /// </summary>
    [Fact]
    public async Task A_very_long_failure_still_ends_with_the_way_back()
    {
        var destination = LiveDestination("live", "production data");
        var job = await QueueOverwritingRestoreAsync(destination, confirmation: "live");

        _engine.RestoreOutcome = _ => new RestoreResult(false, Error: new string('x', 4000));

        await Restores().RunAsync(job, default);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == job);
        row.FailureReason!.Length.Should().BeLessThanOrEqualTo(2048,
            "the column holds 2048 characters and a reason that will not save is a reason lost");
        row.FailureReason.Should().Contain(row.SafetySnapshotRef!);
    }

    /// <summary>Where the restore is reported, the way back is reported beside it.</summary>
    [Fact]
    public void The_backup_center_shows_the_safety_copy_beside_the_restore()
    {
        var markup = File.ReadAllText(
            Path.Combine(TestPaths.WebRoot, "Views", "BackupCenter", "Index.cshtml"));

        markup.Should().Contain("SafetySnapshotRef",
            "a recorded way back nobody can see is not a way back");
    }

    // --- fixtures -----------------------------------------------------------------------------

    private static EngineEntry Entry(string name) =>
        new(name, name, false, 10, DateTimeOffset.UtcNow);

    private BackupSnapshot AddCompletedSnapshot(long filesCount)
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace,
            RepositoryId = _repository.Id,
            TargetType = BackupTargetType.Directory,
            TargetRef = _source,
            EngineSnapshotId = Guid.CreateVersion7().ToString("N"),
            Status = BackupSnapshotStatus.Completed,
            FilesCount = filesCount
        };
        _db.BackupSnapshots.Add(snapshot);
        _db.SaveChanges();
        return snapshot;
    }

    private string LiveDestination(string name, string content)
    {
        var destination = Path.Combine(_options.RestoreRoot, name);
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "in-use.txt"), content);
        return destination;
    }

    private async Task<Guid> QueueOverwritingRestoreAsync(string destination, string confirmation)
    {
        var snapshot = AddCompletedSnapshot(filesCount: 3);

        var queued = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Overwrite,
            ConfirmationText: confirmation), default);

        queued.Succeeded.Should().BeTrue(queued.Error);

        var row = await _db.RestoreJobs.FirstAsync(r => r.Id == queued.RestoreJobId);
        row.OverwritesLiveTarget.Should().BeTrue("this fixture exists to exercise the destructive path");

        return queued.RestoreJobId!.Value;
    }

    private BackupTargetResolver Targets() => new(
        new FakeDockerEngine(), new StubDatabaseStager(), new StubApplicationStager(),
        Options.Create(_options), NullLogger<BackupTargetResolver>.Instance);

    private BackupSnapshotService Snapshots() => new(
        _db, new SingleEngineResolver(_engine), new StubPassword("password"), Targets(), _jobs,
        _notifications, new TestCaller(_workspace), new SilentAuditLog(),
        NullLogger<BackupSnapshotService>.Instance);

    private RestoreService Restores() => new(
        _db, new SingleEngineResolver(_engine), new StubPassword("password"),
        new StubDatabaseRestores(), Targets(), _jobs, _notifications, new TestCaller(_workspace),
        new SilentAuditLog(), Options.Create(_options), NullLogger<RestoreService>.Instance);

    private BackupModuleReconciler Reconciler() => new(
        _sp.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new BackupFeatureOptions { Backup = true }),
        Options.Create(_options),
        new FixedClock(DateTimeOffset.UtcNow),
        NullLogger<BackupModuleReconciler>.Instance);

    private BackupVerifyJobHandler Verifier() => new(
        _db, new SingleEngineResolver(_engine), new StubPassword("password"), _notifications,
        NullLogger<BackupVerifyJobHandler>.Instance);

    private BackupCenterController Center(bool enabled = true)
    {
        var controller = new BackupCenterController(
            new BackupRepositoryService(_db, new SingleEngineResolver(_engine),
                new PassthroughProtector(), new SilentAuditLog(), new TestCaller(_workspace),
                NullLogger<BackupRepositoryService>.Instance),
            Snapshots(),
            new BackupPolicyService(_db),
            Restores(),
            new TestCaller(_workspace),
            Options.Create(new BackupFeatureOptions { Backup = enabled }),
            Options.Create(_options))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        controller.TempData = new TempDataDictionary(
            controller.HttpContext, new NoopTempDataProvider());

        return controller;
    }

    public void Dispose()
    {
        _db.Dispose();
        _sp.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    // --- stubs --------------------------------------------------------------------------------

    /// <summary>
    /// One engine instance for every resolve, so a test can read the calls back in order.
    /// </summary>
    private sealed class SingleEngineResolver(IBackupEngine engine) : IBackupEngineResolver
    {
        public IReadOnlyCollection<BackupEngineKind> Available => [BackupEngineKind.Native];
        public IBackupEngine Resolve(BackupEngineKind kind) => engine;
    }

    private sealed class ScriptedEngine : IBackupEngine
    {
        public BackupEngineKind Kind => BackupEngineKind.Native;

        /// <summary>Every engine call in the order it was made — "create:", "restore:", "browse:".</summary>
        public List<string> Calls { get; } = [];

        public IReadOnlyList<EngineEntry> Entries { get; set; } = [];

        public Func<CreateBackupSnapshotRequest, BackupSnapshotResult> CreateOutcome { get; set; } =
            r => new BackupSnapshotResult(true, r.SnapshotId, r.SnapshotId.ToString("N"), 10, 5, 0, 3);

        public Func<RestoreBackupRequest, RestoreResult> RestoreOutcome { get; set; } =
            r => new RestoreResult(true, 3, 30, r.DestinationPath);

        public Task<BackupRepositoryResult> CreateRepositoryAsync(
            CreateBackupRepositoryRequest r, CancellationToken ct)
            => Task.FromResult(new BackupRepositoryResult(true, r.RepositoryId, false));

        public Task<BackupSnapshotResult> CreateSnapshotAsync(
            CreateBackupSnapshotRequest r, CancellationToken ct)
        {
            Calls.Add("create:" + r.SourcePath);
            return Task.FromResult(CreateOutcome(r));
        }

        public Task<RestoreResult> RestoreAsync(RestoreBackupRequest r, CancellationToken ct)
        {
            Calls.Add("restore:" + r.DestinationPath);
            return Task.FromResult(RestoreOutcome(r));
        }

        public Task<BackupRepositoryHealthResult> CheckHealthAsync(Guid id, CancellationToken ct)
            => Task.FromResult(new BackupRepositoryHealthResult(true, true));

        public Task<IReadOnlyList<EngineSnapshot>> ListSnapshotsAsync(
            ListSnapshotsRequest r, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<EngineSnapshot>>([]);

        public Task<IReadOnlyList<EngineEntry>> BrowseSnapshotAsync(
            BrowseSnapshotRequest r, CancellationToken ct)
        {
            Calls.Add("browse:" + r.EngineSnapshotId);
            return Task.FromResult(Entries);
        }

        public Task<EngineOperationResult> DeleteSnapshotAsync(
            DeleteSnapshotRequest r, CancellationToken ct)
            => Task.FromResult(new EngineOperationResult(true));
    }

    private sealed class StubPassword(string password) : IRepositoryCredentialReader
    {
        public Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken ct)
            => Task.FromResult<string?>(password);

        public Task<RepositoryCredentials?> GetCredentialsAsync(Guid repositoryId, CancellationToken ct)
            => Task.FromResult<RepositoryCredentials?>(null);
    }

    private sealed class TestCaller(Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();
        public string? Email => "tester@harbora.test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class SilentAuditLog : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class NoopTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) =>
            new Dictionary<string, object?>();

        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
