using System.Reflection;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Jobs;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Infrastructure.Common;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

using IBackupEngine = Harbora.Modules.Backup.Contracts.IBackupEngine;

namespace Harbora.Tests;

/// <summary>
/// The versioned backup API: paging, filtering, sorting, idempotency, and the two things an API over
/// a backup system must never get wrong — returning a secret, or serving another tenant's data.
/// </summary>
public sealed class BackupApiTests : IDisposable
{
    private readonly string _root;
    private readonly string _databaseName = "backup-api-" + Guid.NewGuid();
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly BackupRepository _repository;
    private readonly BackupModuleOptions _options;
    private readonly RecordingJobQueue _jobs = new();

    public BackupApiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-backup-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "restore"));
        Directory.CreateDirectory(Path.Combine(_root, "source"));

        _db = Context();

        _options = new BackupModuleOptions
        {
            RestoreRoot = Path.Combine(_root, "restore"),
            StagingDirectory = Path.Combine(_root, "staging"),
            AllowedSourceRoots = [Path.Combine(_root, "source")]
        };

        _repository = new BackupRepository
        {
            WorkspaceId = _workspace,
            Name = "Primary",
            Type = BackupRepositoryType.Local,
            Engine = BackupEngineKind.Native,
            BasePath = Path.Combine(_root, "repo"),
            Status = BackupRepositoryStatus.Ready,
            EncryptedPassword = "cipher-text-that-must-never-be-returned",
            EncryptedCredentials = "secret-access-key-cipher"
        };
        _db.BackupRepositories.Add(_repository);
        _db.SaveChanges();
    }

    private HarboraDbContext Context() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_databaseName).Options,
        new FixedWorkspaceScope(_workspace));

    private BackupApiController Api(bool enabled = true, string? idempotencyKey = null)
    {
        var snapshots = new BackupSnapshotService(
            _db, new StubResolver(), new StubCredentials(), Resolver(), _jobs,
            new RecordingBackupNotifications(), new Caller(_workspace), new SilentAudit(),
            NullLogger<BackupSnapshotService>.Instance);

        var restores = new RestoreService(
            _db, new StubResolver(), new StubCredentials(), new StubDatabaseRestores(), Resolver(),
            _jobs, new RecordingBackupNotifications(), new Caller(_workspace), new SilentAudit(),
            Options.Create(_options), NullLogger<RestoreService>.Instance);

        var controller = new BackupApiController(
            _db,
            new BackupRepositoryService(_db, new StubResolver(), new PassthroughProtector(),
                new SilentAudit(), new Caller(_workspace), NullLogger<BackupRepositoryService>.Instance),
            snapshots,
            new BackupPolicyService(_db),
            restores,
            Resolver(),
            new IdempotencyStore(_db, new FixedClock(DateTimeOffset.UtcNow)),
            new Caller(_workspace),
            Options.Create(new BackupFeatureOptions { Backup = enabled }),
            Options.Create(_options));

        var http = new DefaultHttpContext();
        if (idempotencyKey is not null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return controller;
    }

    private BackupTargetResolver Resolver() => new(
        new FakeDockerEngine(), new StubDatabaseStager(), new StubApplicationStager(),
        Options.Create(_options), NullLogger<BackupTargetResolver>.Instance);

    private BackupSnapshot AddSnapshot(
        BackupSnapshotStatus status = BackupSnapshotStatus.Completed,
        string target = "src", long stored = 100, Guid? workspaceId = null)
    {
        var snapshot = new BackupSnapshot
        {
            WorkspaceId = workspaceId ?? _workspace,
            RepositoryId = _repository.Id,
            TargetType = BackupTargetType.Directory,
            TargetRef = target,
            EngineSnapshotId = Guid.CreateVersion7().ToString("N"),
            Status = status,
            StoredSizeBytes = stored
        };
        _db.BackupSnapshots.Add(snapshot);
        _db.SaveChanges();
        return snapshot;
    }

    private static T Body<T>(IActionResult result) where T : class
    {
        var value = result switch
        {
            OkObjectResult ok => ok.Value,
            AcceptedResult accepted => accepted.Value,
            CreatedAtActionResult created => created.Value,
            ObjectResult other => other.Value,
            _ => null
        };
        value.Should().NotBeNull();
        return (T)value!;
    }

    // ---- the flag ---------------------------------------------------------------------------

    [Fact]
    public async Task Every_route_is_absent_while_the_feature_is_off()
    {
        var api = Api(enabled: false);

        (await api.ListRepositories()).Should().BeOfType<NotFoundResult>();
        (await api.ListSnapshots()).Should().BeOfType<NotFoundResult>();
        (await api.ListPolicies()).Should().BeOfType<NotFoundResult>();
        (await api.ListRestores()).Should().BeOfType<NotFoundResult>();
        api.ListTargets().Should().BeOfType<NotFoundResult>();
    }

    // ---- secrets ----------------------------------------------------------------------------

    /// <summary>
    /// The response contract is the last place a credential can escape. Asserted over the DTO's
    /// actual properties rather than by eye, so a field added later fails this test.
    /// </summary>
    [Fact]
    public async Task A_repository_response_carries_no_secret_in_any_form()
    {
        var page = Body<PagedResponse<RepositoryDto>>(await Api().ListRepositories());
        var dto = page.Items.Should().ContainSingle().Subject;

        var serialised = System.Text.Json.JsonSerializer.Serialize(dto);
        serialised.Should().NotContain("cipher-text-that-must-never-be-returned");
        serialised.Should().NotContain("secret-access-key-cipher");

        var names = typeof(RepositoryDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name);
        names.Should().NotContain(n =>
            n.Contains("Password", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Secret", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Credential", StringComparison.OrdinalIgnoreCase)
            || n.Contains("AccessKey", StringComparison.OrdinalIgnoreCase));
    }

    // ---- paging, filtering, sorting ----------------------------------------------------------

    [Fact]
    public async Task Pages_results_and_reports_how_many_there_are()
    {
        for (var i = 0; i < 7; i++) AddSnapshot();

        var page = Body<PagedResponse<SnapshotDto>>(await Api().ListSnapshots(page: 2, pageSize: 3));

        page.Items.Should().HaveCount(3);
        page.Page.Should().Be(2);
        page.TotalCount.Should().Be(7);
        page.TotalPages.Should().Be(3);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Clamps_an_unreasonable_page_size_rather_than_serving_everything()
    {
        for (var i = 0; i < 5; i++) AddSnapshot();

        var page = Body<PagedResponse<SnapshotDto>>(await Api().ListSnapshots(pageSize: 100_000));

        page.PageSize.Should().BeLessThanOrEqualTo(200, "a limit a caller can raise without bound is not a limit");
    }

    [Fact]
    public async Task Filters_snapshots_by_status()
    {
        AddSnapshot(BackupSnapshotStatus.Completed);
        AddSnapshot(BackupSnapshotStatus.Failed);
        AddSnapshot(BackupSnapshotStatus.Failed);

        var page = Body<PagedResponse<SnapshotDto>>(await Api().ListSnapshots(status: "Failed"));

        page.TotalCount.Should().Be(2);
        page.Items.Should().OnlyContain(s => s.Status == "Failed");
    }

    [Fact]
    public async Task Rejects_an_unknown_filter_value_instead_of_silently_ignoring_it()
    {
        var result = await Api().ListSnapshots(status: "Mostly-fine");

        var problem = Body<ProblemDetails>(result);
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Title.Should().Contain("Mostly-fine");
    }

    [Fact]
    public async Task Rejects_an_unknown_sort_field()
    {
        var problem = Body<ProblemDetails>(await Api().ListSnapshots(sort: "whatever"));

        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Extensions.Should().ContainKey("field");
    }

    [Fact]
    public async Task Sorts_snapshots_by_size()
    {
        AddSnapshot(stored: 10);
        AddSnapshot(stored: 900);
        AddSnapshot(stored: 50);

        var page = Body<PagedResponse<SnapshotDto>>(await Api().ListSnapshots(sort: "-size"));

        page.Items.Select(s => s.StoredSizeBytes).Should().BeInDescendingOrder();
    }

    // ---- idempotency -------------------------------------------------------------------------

    /// <summary>
    /// Matters most for restore: a retried request that ran twice would overwrite data a second time.
    /// </summary>
    [Fact]
    public async Task A_repeated_idempotency_key_replays_the_first_result()
    {
        var body = new CreateSnapshotBody(
            _repository.Id, BackupTargetType.Directory, Path.Combine(_root, "source"));

        var first = await Api(idempotencyKey: "retry-me").CreateSnapshot(body, default);
        var second = await Api(idempotencyKey: "retry-me").CreateSnapshot(body, default);

        first.Should().BeOfType<AcceptedResult>();
        second.Should().BeOfType<AcceptedResult>();

        var firstId = first.GetType().GetProperty("Value")!.GetValue(first)!;
        var secondId = second.GetType().GetProperty("Value")!.GetValue(second)!;

        SnapshotIdOf(firstId).Should().Be(SnapshotIdOf(secondId));
        ReplayedFlag(secondId).Should().BeTrue();
        ReplayedFlag(firstId).Should().BeFalse();

        _jobs.Enqueued.Count(j => j.Kind == JobKind.BackupSnapshot)
            .Should().Be(1, "the second call must not queue a second backup");
    }

    [Fact]
    public async Task A_different_idempotency_key_starts_new_work()
    {
        var body = new CreateSnapshotBody(
            _repository.Id, BackupTargetType.Directory, Path.Combine(_root, "source"));

        await Api(idempotencyKey: "first").CreateSnapshot(body, default);

        // The service's own per-target guard refuses a concurrent second backup of the same target,
        // which is the correct outcome and separate from idempotency.
        var second = await Api(idempotencyKey: "second").CreateSnapshot(body, default);

        second.Should().BeOfType<ConflictObjectResult>();
        Body<ProblemDetails>(second).Title.Should().Contain("already running");
    }

    [Fact]
    public async Task An_unusable_idempotency_key_is_an_error_rather_than_being_ignored()
    {
        var api = Api(idempotencyKey: new string('k', 200));
        var body = new CreateSnapshotBody(
            _repository.Id, BackupTargetType.Directory, Path.Combine(_root, "source"));

        var problem = Body<ProblemDetails>(await api.CreateSnapshot(body, default));

        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Extensions["field"].Should().Be("Idempotency-Key");
        _jobs.Enqueued.Should().BeEmpty();
    }

    // ---- tenancy ------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspaces_snapshot_is_not_listed_and_not_fetchable()
    {
        var mine = AddSnapshot();
        var theirs = AddSnapshot(workspaceId: Guid.CreateVersion7());

        var page = Body<PagedResponse<SnapshotDto>>(await Api().ListSnapshots());
        page.Items.Select(s => s.Id).Should().Contain(mine.Id).And.NotContain(theirs.Id);

        (await Api().GetSnapshot(theirs.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    // ---- targets --------------------------------------------------------------------------------

    [Fact]
    public void Targets_endpoint_says_what_this_deployment_can_actually_back_up()
    {
        var body = Body<object>(Api().ListTargets());
        var json = System.Text.Json.JsonSerializer.Serialize(body);

        json.Should().Contain("Directory").And.Contain("DockerVolume");
        json.Should().Contain("Application", "an unsupported type is listed with its reason, not hidden");
    }

    // ---- restore ---------------------------------------------------------------------------------

    [Fact]
    public async Task Restore_refuses_a_destination_outside_the_restore_root()
    {
        var snapshot = AddSnapshot();

        var result = await Api().CreateRestore(
            new CreateRestoreBody(snapshot.Id, "/etc"), default);

        Body<ProblemDetails>(result).Title.Should().Contain(_options.RestoreRoot);
    }

    private static Guid SnapshotIdOf(object payload) =>
        (Guid)payload.GetType().GetProperty("snapshotId")!.GetValue(payload)!;

    private static bool ReplayedFlag(object payload) =>
        (bool)payload.GetType().GetProperty("replayed")!.GetValue(payload)!;

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    // ---- stubs --------------------------------------------------------------------------------

    private sealed class Caller(Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId { get; } = Guid.CreateVersion7();
        public string? Email => "api@harbora.test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId { get; } = workspaceId;
    }

    private sealed class SilentAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null,
            string? ipAddress = null, string? actorEmailOverride = null, Guid? userIdOverride = null,
            string? metadataJson = null, Guid? workspaceId = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class StubCredentials : IRepositoryCredentialReader
    {
        public Task<string?> GetPasswordAsync(Guid id, CancellationToken ct) => Task.FromResult<string?>("pw");
        public Task<RepositoryCredentials?> GetCredentialsAsync(Guid id, CancellationToken ct)
            => Task.FromResult<RepositoryCredentials?>(null);
    }

    private sealed class StubResolver : IBackupEngineResolver
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
            => Task.FromResult(new BackupSnapshotResult(true, r.SnapshotId, r.SnapshotId.ToString("N")));
        public Task<RestoreResult> RestoreAsync(RestoreBackupRequest r, CancellationToken ct)
            => Task.FromResult(new RestoreResult(true));
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
