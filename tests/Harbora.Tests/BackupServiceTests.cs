using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// Both namespaces used here declare an IBackupEngine — the platform's target-oriented service and
// the module's storage-engine port (ARCHITECTURE.md § 2). The stub implements the module's.
using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Tests;

/// <summary>
/// The service layer: what gets queued, what gets refused, and who is allowed to see it.
///
/// <para>
/// These are the checks that only exist above the engine — tenant scoping, restore confirmation,
/// destination confinement and duplicate suppression. The engine cannot enforce any of them,
/// because by the time it is called the decision has already been made.
/// </para>
/// </summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _root;
    private readonly HarboraDbContext _db;
    private readonly RecordingJobQueue _jobs = new();
    private readonly RecordingBackupNotifications _notifications = new();
    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly Guid _otherWorkspace = Guid.CreateVersion7();
    private readonly BackupRepository _repository;
    private readonly BackupModuleOptions _options;

    private readonly string _databaseName = "backup-services-" + Guid.NewGuid();

    /// <summary>A second context over the SAME store, under a different tenant scope.</summary>
    private HarboraDbContext ContextFor(IWorkspaceScope scope) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_databaseName).Options,
        scope);

    public BackupServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-backup-services", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "source"));
        Directory.CreateDirectory(Path.Combine(_root, "restore"));

        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(_databaseName).Options);

        _options = new BackupModuleOptions
        {
            RestoreRoot = Path.Combine(_root, "restore"),
            StagingDirectory = Path.Combine(_root, "staging"),
            AllowedSourceRoots = [Path.Combine(_root, "source")]
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

    private BackupSnapshotService Snapshots() => new(
        _db,
        new StubEngineResolver(),
        new StubCredentialReader("password"),
        new BackupTargetResolver(Options.Create(_options)),
        _jobs,
        _notifications,
        new StubCaller(_workspace),
        new NoopAudit(),
        NullLogger<BackupSnapshotService>.Instance);

    private RestoreService Restores() => new(
        _db,
        new StubEngineResolver(),
        new StubCredentialReader("password"),
        _jobs,
        _notifications,
        new StubCaller(_workspace),
        new NoopAudit(),
        Options.Create(_options),
        NullLogger<RestoreService>.Instance);

    private BackupSnapshot AddCompletedSnapshot(Guid workspaceId)
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = workspaceId,
            RepositoryId = _repository.Id,
            TargetType = BackupTargetType.Directory,
            TargetRef = Path.Combine(_root, "source"),
            EngineSnapshotId = Guid.CreateVersion7().ToString("N"),
            Status = BackupSnapshotStatus.Completed,
            FilesCount = 3
        };
        _db.BackupSnapshots.Add(snapshot);
        _db.SaveChanges();
        return snapshot;
    }

    // --- queueing ---------------------------------------------------------------------------

    [Fact]
    public async Task Queues_a_snapshot_for_an_allowed_directory()
    {
        var result = await Snapshots().QueueAsync(
            _workspace, _repository.Id, BackupTargetType.Directory,
            Path.Combine(_root, "source"), null, BackupTrigger.Manual, default);

        result.Succeeded.Should().BeTrue(result.Error);
        _jobs.Enqueued.Should().ContainSingle(j => j.Kind == JobKind.BackupSnapshot);
    }

    /// <summary>
    /// A backup engine pointed anywhere is an arbitrary-file read with a download button on the end.
    /// </summary>
    [Fact]
    public async Task Refuses_a_directory_outside_the_allowed_roots()
    {
        var result = await Snapshots().QueueAsync(
            _workspace, _repository.Id, BackupTargetType.Directory,
            Path.Combine(_root, "not-allowed"), null, BackupTrigger.Manual, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("not inside");
        _jobs.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Refuses_a_second_backup_of_a_target_already_running()
    {
        var service = Snapshots();
        var target = Path.Combine(_root, "source");

        var first = await service.QueueAsync(
            _workspace, _repository.Id, BackupTargetType.Directory, target, null, BackupTrigger.Manual, default);
        var second = await service.QueueAsync(
            _workspace, _repository.Id, BackupTargetType.Directory, target, null, BackupTrigger.Manual, default);

        first.Succeeded.Should().BeTrue();
        second.Succeeded.Should().BeFalse();
        second.Error.Should().Contain("already running");
    }

    // --- restore guards ---------------------------------------------------------------------

    [Fact]
    public async Task Queues_a_restore_into_an_empty_destination_without_confirmation()
    {
        var snapshot = AddCompletedSnapshot(_workspace);
        var destination = Path.Combine(_options.RestoreRoot, "fresh");

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeTrue(result.Error);
        _jobs.Enqueued.Should().ContainSingle(j => j.Kind == JobKind.BackupRestore);
    }

    /// <summary>
    /// A checkbox is a thing people click. Typing the name of what is about to be overwritten is the
    /// only cheap control that distinguishes "I meant this one" from "I clicked the row above".
    /// </summary>
    [Fact]
    public async Task Refuses_to_overwrite_live_data_without_the_typed_confirmation()
    {
        var snapshot = AddCompletedSnapshot(_workspace);
        var destination = Path.Combine(_options.RestoreRoot, "live");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "in-use.txt"), "production data");

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Overwrite), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("live");
        _jobs.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Accepts_an_overwrite_when_the_destination_name_is_typed_back()
    {
        var snapshot = AddCompletedSnapshot(_workspace);
        var destination = Path.Combine(_options.RestoreRoot, "live");
        Directory.CreateDirectory(destination);
        await File.WriteAllTextAsync(Path.Combine(destination, "in-use.txt"), "production data");

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Overwrite,
            ConfirmationText: "live"), default);

        result.Succeeded.Should().BeTrue(result.Error);

        var job = await _db.RestoreJobs.FirstAsync(r => r.Id == result.RestoreJobId);
        job.OverwritesLiveTarget.Should().BeTrue("the audit trail must record that live data was at risk");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("/etc/passwd")]
    public async Task Refuses_a_restore_destination_outside_the_restore_root(string destination)
    {
        var snapshot = AddCompletedSnapshot(_workspace);

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, destination, RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain(_options.RestoreRoot);
    }

    [Fact]
    public async Task Refuses_to_restore_a_snapshot_that_never_completed()
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = _workspace,
            RepositoryId = _repository.Id,
            TargetRef = "x",
            Status = BackupSnapshotStatus.Failed
        };
        _db.BackupSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();

        var result = await Restores().QueueAsync(_workspace, new RestoreRequest(
            snapshot.Id, RestoreType.Folder, Path.Combine(_options.RestoreRoot, "x"),
            RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("Failed");
    }

    // --- tenancy ----------------------------------------------------------------------------

    /// <summary>
    /// The query filter is what enforces this, and the point of the test is that the SERVICE relies
    /// on it rather than on a caller remembering to add a workspace clause.
    /// </summary>
    /// <summary>
    /// Isolation comes from the model's global query filter, not from every call site remembering a
    /// workspace clause. This asserts the filter itself over the same store the services use.
    /// </summary>
    [Fact]
    public async Task Another_workspaces_snapshot_is_invisible_through_a_scoped_context()
    {
        var mine = AddCompletedSnapshot(_workspace);
        var theirs = AddCompletedSnapshot(_otherWorkspace);

        using var scoped = ContextFor(new FixedWorkspaceScope(_workspace));

        var visible = await scoped.BackupSnapshots.Select(s => s.Id).ToListAsync();

        visible.Should().Contain(mine.Id);
        visible.Should().NotContain(theirs.Id, "another tenant's backup must not be readable");
    }

    /// <summary>
    /// The counterpart, and the one that has bitten this codebase before: background work runs
    /// unscoped and MUST see every tenant. A sweeper that accidentally ran scoped would read nothing
    /// and report success having done nothing.
    /// </summary>
    [Fact]
    public async Task Background_work_running_unscoped_sees_every_tenant()
    {
        var mine = AddCompletedSnapshot(_workspace);
        var theirs = AddCompletedSnapshot(_otherWorkspace);

        using var system = ContextFor(SystemWorkspaceScope.Instance);

        var visible = await system.BackupSnapshots.Select(s => s.Id).ToListAsync();

        visible.Should().Contain([mine.Id, theirs.Id]);
    }

    [Fact]
    public async Task A_restore_of_another_workspaces_snapshot_is_refused()
    {
        var theirs = AddCompletedSnapshot(_otherWorkspace);

        using var scoped = ContextFor(new FixedWorkspaceScope(_workspace));
        var restores = new RestoreService(
            scoped, new StubEngineResolver(), new StubCredentialReader("password"), _jobs,
            _notifications, new StubCaller(_workspace), new NoopAudit(),
            Options.Create(_options), NullLogger<RestoreService>.Instance);

        var result = await restores.QueueAsync(_workspace, new RestoreRequest(
            theirs.Id, RestoreType.Folder, Path.Combine(_options.RestoreRoot, "x"),
            RestoreConflictStrategy.Fail), default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("no longer exists",
            "a snapshot in another workspace must be indistinguishable from one that is not there");
        _jobs.Enqueued.Should().BeEmpty();
    }

    // --- scheduling -------------------------------------------------------------------------

    [Fact]
    public void Next_run_is_computed_in_the_policys_timezone()
    {
        var policy = new BackupPolicy
        {
            Enabled = true,
            Schedule = "0 3 * * *",
            Timezone = "UTC"
        };

        var after = new DateTimeOffset(2026, 3, 10, 10, 0, 0, TimeSpan.Zero);
        var next = BackupPolicyService.NextRun(policy, after);

        next.Should().NotBeNull();
        next!.Value.UtcDateTime.Hour.Should().Be(3);
        next.Value.Should().BeAfter(after);
    }

    [Fact]
    public void A_disabled_policy_has_no_next_run()
    {
        var policy = new BackupPolicy { Enabled = false, Schedule = "0 3 * * *", Timezone = "UTC" };

        BackupPolicyService.NextRun(policy, DateTimeOffset.UtcNow).Should().BeNull();
    }

    [Fact]
    public void An_unparseable_schedule_has_no_next_run_rather_than_a_guess()
    {
        var policy = new BackupPolicy { Enabled = true, Schedule = "every other tuesday", Timezone = "UTC" };

        BackupPolicyService.NextRun(policy, DateTimeOffset.UtcNow).Should().BeNull();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    // --- stubs ------------------------------------------------------------------------------

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

    private sealed class StubCredentialReader(string password) : IRepositoryCredentialReader
    {
        public Task<string?> GetPasswordAsync(Guid repositoryId, CancellationToken cancellationToken)
            => Task.FromResult<string?>(password);

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

/// <summary>Records what was queued, so a test can assert work was handed to the worker.</summary>
internal sealed class RecordingJobQueue : IJobQueue
{
    public List<(JobKind Kind, Guid TargetId)> Enqueued { get; } = [];

    public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, CancellationToken ct = default)
    {
        Enqueued.Add((kind, targetId));
        return Task.FromResult(Guid.CreateVersion7());
    }

    public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default)
        => Task.FromResult(false);
}

internal sealed class RecordingBackupNotifications : IBackupNotificationService
{
    public List<BackupNotification> Sent { get; } = [];

    public Task SendAsync(BackupNotification notification, CancellationToken cancellationToken)
    {
        Sent.Add(notification);
        return Task.CompletedTask;
    }
}
