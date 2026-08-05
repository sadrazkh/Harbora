using System.Reflection;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Common;
using Harbora.Modules.Sync.Contracts;
using Harbora.Modules.Sync.Domain;
using Harbora.Modules.Sync.Infrastructure;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The versioned sync API.
///
/// <para>
/// Same shape as the backup API's tests, plus the one thing peculiar to this module: the endpoints
/// that would let a caller mistake sync for backup do not exist, and the ones that could mislead say
/// so in the payload.
/// </para>
/// </summary>
public sealed class SyncApiTests : IDisposable
{
    private readonly string _root;
    private readonly string _databaseName = "sync-api-" + Guid.NewGuid();
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly SyncModuleOptions _options;
    private readonly RecordingSyncEngine _engine = new();

    public SyncApiTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "harbora-sync-api", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "shared"));

        _db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(_databaseName).Options,
            new FixedWorkspaceScope(_workspace));

        _options = new SyncModuleOptions
        {
            AllowedRoots = [Path.Combine(_root, "shared")],
            AllowEncryptedNode = true
        };
    }

    private SyncApiController Api(bool enabled = true, string? idempotencyKey = null)
    {
        var service = new SyncSpaceService(
            _db, _engine, new PassthroughProtector(), new SilentAudit(), new Caller(_workspace),
            Options.Create(_options), NullLogger<SyncSpaceService>.Instance);

        var controller = new SyncApiController(
            _db, service, _engine,
            new IdempotencyStore(_db, new FixedClock(DateTimeOffset.UtcNow)),
            new Caller(_workspace),
            Options.Create(new SyncFeatureOptions { Sync = enabled }),
            Options.Create(_options));

        var http = new DefaultHttpContext();
        if (idempotencyKey is not null) http.Request.Headers["Idempotency-Key"] = idempotencyKey;
        controller.ControllerContext = new ControllerContext { HttpContext = http };

        return controller;
    }

    private const string DeviceId =
        "P56IOI7-MZJNU2Y-IQGDRE6-I2JQOTP-ZLQGRQD-D5JQNSY-JYQMQVL-QAKZQAP";

    private SyncDevice AddDevice(bool untrusted = false, Guid? workspaceId = null)
    {
        var device = new SyncDevice
        {
            WorkspaceId = workspaceId ?? _workspace,
            Name = untrusted ? "Storage node" : "Laptop",
            EngineDeviceId = untrusted
                ? DeviceId
                : "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH",
            IsUntrusted = untrusted,
            Status = SyncDeviceStatus.Connected
        };
        _db.SyncDevices.Add(device);
        _db.SaveChanges();
        return device;
    }

    private SyncSpace AddSpace(Guid? workspaceId = null)
    {
        var space = new SyncSpace
        {
            WorkspaceId = workspaceId ?? _workspace,
            Name = "Documents " + Guid.NewGuid().ToString("N")[..6],
            LocalPath = Path.Combine(_root, "shared"),
            EngineFolderId = "harbora-" + Guid.NewGuid().ToString("N"),
            Status = SyncSpaceStatus.UpToDate
        };
        _db.SyncSpaces.Add(space);
        _db.SaveChanges();
        return space;
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

    // ---- the flag -------------------------------------------------------------------------------

    [Fact]
    public async Task Every_route_is_absent_while_the_feature_is_off()
    {
        var api = Api(enabled: false);

        (await api.ListSpaces()).Should().BeOfType<NotFoundResult>();
        (await api.ListDevices()).Should().BeOfType<NotFoundResult>();
        (await api.ListPairings()).Should().BeOfType<NotFoundResult>();
        (await api.ListConflicts()).Should().BeOfType<NotFoundResult>();
        (await api.GetNode(default)).Should().BeOfType<NotFoundResult>();
    }

    // ---- sync is not backup ---------------------------------------------------------------------

    /// <summary>
    /// The API surface itself has to make the distinction, not just the docs: a client author reading
    /// the route list must not find anything that looks like recovery.
    /// </summary>
    [Fact]
    public void The_api_offers_nothing_that_looks_like_a_restore()
    {
        var routes = typeof(SyncApiController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name);

        routes.Should().NotContain(n =>
            n.Contains("Restore", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Snapshot", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Recover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_node_endpoint_says_plainly_that_sync_is_not_a_backup()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(Body<object>(await Api().GetNode(default)));

        json.Should().Contain("not a backup");
    }

    [Fact]
    public async Task Removing_a_pairing_says_the_device_keeps_its_files()
    {
        var space = AddSpace();
        var device = AddDevice();
        _db.SyncSpaceMembers.Add(new SyncSpaceMember
        {
            WorkspaceId = _workspace,
            SyncSpaceId = space.Id,
            SyncDeviceId = device.Id,
            Mode = SyncMode.SendAndReceive
        });
        await _db.SaveChangesAsync();

        var json = System.Text.Json.JsonSerializer.Serialize(
            Body<object>(await Api().DeletePairing(space.Id, device.Id, default)));

        // Sync is not remote wipe, and a client must not be able to imply that it is.
        json.Should().Contain("keeps the files");
    }

    // ---- secrets ---------------------------------------------------------------------------------

    /// <summary>
    /// The folder encryption password is the one secret this module holds. Asserted over the DTO's
    /// properties, so a field added later fails this test rather than shipping.
    /// </summary>
    [Fact]
    public async Task A_pairing_response_never_carries_the_folder_password()
    {
        var space = AddSpace();
        var device = AddDevice(untrusted: true);

        await Api().CreatePairing(new CreatePairingBody(
            space.Id, device.Id, SyncMode.EncryptedReceiveOnly, "a-very-long-secret-password"), default);

        var pairings = Body<IEnumerable<SyncPairingDto>>(await Api().ListPairings(space.Id));
        var json = System.Text.Json.JsonSerializer.Serialize(pairings);

        json.Should().NotContain("a-very-long-secret-password");

        typeof(SyncPairingDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Password", StringComparison.OrdinalIgnoreCase));

        // The fact that it IS encrypted is reportable; the password is not.
        pairings.Should().ContainSingle().Which.IsEncrypted.Should().BeTrue();
    }

    // ---- validation reaches the API ---------------------------------------------------------------

    [Fact]
    public async Task An_untrusted_device_cannot_be_paired_in_a_plaintext_mode()
    {
        var space = AddSpace();
        var device = AddDevice(untrusted: true);

        var result = await Api().CreatePairing(
            new CreatePairingBody(space.Id, device.Id, SyncMode.SendAndReceive), default);

        var problem = Body<ValidationProblemDetails>(result);
        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        System.Text.Json.JsonSerializer.Serialize(problem.Errors).Should().Contain("readable");
    }

    [Fact]
    public async Task A_space_outside_the_allowed_roots_is_refused()
    {
        var result = await Api().CreateSpace(
            new CreateSyncSpaceBody("Escape", "/etc"), default);

        Body<ProblemDetails>(result).Title.Should().Contain("not inside");
    }

    [Fact]
    public async Task A_malformed_device_id_is_refused()
    {
        var result = await Api().RegisterDevice(
            new RegisterSyncDeviceBody("Laptop", "not-a-device-id"), default);

        Body<ProblemDetails>(result).Title.Should().Contain("device id");
    }

    // ---- paging, filtering, sorting -----------------------------------------------------------------

    [Fact]
    public async Task Pages_and_reports_the_total()
    {
        for (var i = 0; i < 5; i++) AddSpace();

        var page = Body<PagedResponse<SyncSpaceDto>>(await Api().ListSpaces(page: 2, pageSize: 2));

        page.Items.Should().HaveCount(2);
        page.TotalCount.Should().Be(5);
        page.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task Rejects_an_unknown_sort_field()
    {
        var problem = Body<ProblemDetails>(await Api().ListSpaces(sort: "whatever"));

        problem.Status.Should().Be(StatusCodes.Status400BadRequest);
        problem.Extensions.Should().ContainKey("field");
    }

    [Fact]
    public async Task Rejects_an_unknown_status_filter()
    {
        Body<ProblemDetails>(await Api().ListSpaces(status: "Probably-fine"))
            .Title.Should().Contain("Probably-fine");
    }

    [Fact]
    public async Task Clamps_an_unreasonable_page_size()
    {
        AddSpace();

        Body<PagedResponse<SyncSpaceDto>>(await Api().ListSpaces(pageSize: 100_000))
            .PageSize.Should().BeLessThanOrEqualTo(200);
    }

    // ---- idempotency ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_repeated_idempotency_key_replays_the_first_pairing()
    {
        var space = AddSpace();
        var device = AddDevice();
        var body = new CreatePairingBody(space.Id, device.Id, SyncMode.SendAndReceive);

        var first = await Api(idempotencyKey: "pair-once").CreatePairing(body, default);
        var second = await Api(idempotencyKey: "pair-once").CreatePairing(body, default);

        first.Should().BeOfType<AcceptedResult>();
        second.Should().BeOfType<AcceptedResult>();

        _engine.Pairings.Should().HaveCount(1, "the retry must not share the folder a second time");
    }

    [Fact]
    public async Task An_unusable_idempotency_key_is_an_error_rather_than_being_ignored()
    {
        var space = AddSpace();
        var device = AddDevice();

        var problem = Body<ProblemDetails>(await Api(idempotencyKey: new string('k', 200))
            .CreatePairing(new CreatePairingBody(space.Id, device.Id), default));

        problem.Extensions["field"].Should().Be("Idempotency-Key");
        _engine.Pairings.Should().BeEmpty();
    }

    // ---- tenancy --------------------------------------------------------------------------------------

    [Fact]
    public async Task Another_workspaces_space_is_neither_listed_nor_fetchable()
    {
        var mine = AddSpace();
        var theirs = AddSpace(workspaceId: Guid.CreateVersion7());

        var page = Body<PagedResponse<SyncSpaceDto>>(await Api().ListSpaces());
        page.Items.Select(s => s.Id).Should().Contain(mine.Id).And.NotContain(theirs.Id);

        (await Api().GetSpace(theirs.Id, default)).Should().BeOfType<NotFoundResult>();
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a locked temp file is not a test failure */ }
    }

    // ---- stubs -----------------------------------------------------------------------------------------

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
            string? metadataJson = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>Records what the engine was asked to do, so a retry can be shown not to repeat it.</summary>
    private sealed class RecordingSyncEngine : ISyncEngine
    {
        public List<PairSyncDeviceRequest> Pairings { get; } = [];

        public Task<SyncDeviceResult> RegisterDeviceAsync(RegisterSyncDeviceRequest r, CancellationToken ct)
            => Task.FromResult(new SyncDeviceResult(true, r.DeviceId, r.EngineDeviceId));

        public Task<SyncFolderResult> CreateFolderAsync(CreateSyncFolderRequest r, CancellationToken ct)
            => Task.FromResult(new SyncFolderResult(true, r.FolderId, $"harbora-{r.FolderId:N}"));

        public Task<PairDeviceResult> PairDeviceAsync(PairSyncDeviceRequest r, CancellationToken ct)
        {
            Pairings.Add(r);
            return Task.FromResult(new PairDeviceResult(true));
        }

        public Task<SyncFolderStatusResult> GetFolderStatusAsync(Guid folderId, CancellationToken ct)
            => Task.FromResult(new SyncFolderStatusResult(true, SyncSpaceStatus.UpToDate));

        public Task<SyncOperationResult> SetPausedAsync(Guid folderId, bool paused, CancellationToken ct)
            => Task.FromResult(new SyncOperationResult(true));

        public Task<SyncOperationResult> UnpairDeviceAsync(PairSyncDeviceRequest r, CancellationToken ct)
            => Task.FromResult(new SyncOperationResult(true));

        public Task<IReadOnlyList<SyncConflictFile>> ListConflictsAsync(Guid folderId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SyncConflictFile>>([]);

        public Task<IReadOnlyList<SyncDeviceConnection>> ListConnectionsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<SyncDeviceConnection>>([]);

        public Task<string?> GetLocalDeviceIdAsync(CancellationToken ct)
            => Task.FromResult<string?>(DeviceId);
    }
}
