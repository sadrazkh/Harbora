using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which machine an admin-tool session comes up on (HARBORA-0059).
///
/// <para>
/// Unlike a grant, the admin tool needs something published back to this panel's own proxy: Traefik
/// addresses a route's target by container name on its own Docker networks, so a session started on
/// another server would get a route naming a container this panel's daemon has never heard of — a
/// 502 with no sign of why. That is exactly the constraint <c>DockerTcpGateway</c> already enforces
/// for external access, and <see cref="AdminerService"/> now makes the same check before it starts
/// anything.
/// </para>
/// </summary>
public sealed class AdminerServiceHostTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("adminer-host-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;
    private readonly RecordingProxyEngine _proxy;
    private readonly PassthroughProtector _protector = new();
    private readonly FixedClock _clock = new();

    public AdminerServiceHostTests()
    {
        _engines = new FakeServerEngineFactory(_panel);
        _proxy = new RecordingProxyEngine(() => _db.Routes.ToList());
    }

    private AdminerService Service(IProxyEngine? proxy = null) => new(
        _db, _engines, proxy ?? _proxy,
        new ManagedServiceEngine(
            _db, _engines, _protector, new NoopJobQueue(),
            new Harbora.Infrastructure.Billing.BillingGate(
                _db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
            Options.Create(new HarboraRuntimeOptions()), _clock,
            NullLogger<ManagedServiceEngine>.Instance),
        _protector, _clock,
        Options.Create(new HarboraRuntimeOptions()),
        NullLogger<AdminerService>.Instance);

    /// <summary>
    /// Cancels the token it is handed on its first call, then throws — standing in for the operator
    /// navigating away while <c>OpenAsync</c> is waiting on the proxy apply. Every later call (the
    /// withdrawal's own republish, on <see cref="CancellationToken.None"/>) is forwarded to the real
    /// <see cref="RecordingProxyEngine"/> instead, so that half of the fix is exercised for real
    /// rather than assumed.
    /// </summary>
    private sealed class CancelsOnFirstApply(CancellationTokenSource cts, IProxyEngine inner) : IProxyEngine
    {
        private bool _first = true;

        public ProxyConfigPreview Preview(IReadOnlyList<Route> routes) => inner.Preview(routes);
        public ProxyValidationResult Validate(IReadOnlyList<Route> routes) => inner.Validate(routes);

        public Task<ProxyApplyResult> ApplyAllAsync(Guid? callerWorkspaceId, CancellationToken ct)
        {
            if (_first)
            {
                _first = false;
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
            }
            return inner.ApplyAllAsync(callerWorkspaceId, ct);
        }
    }

    private ManagedService Database(Guid serverId, Guid workspaceId, Guid environmentId)
    {
        var svc = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspaceId,
            EnvironmentId = environmentId,
            ServerId = serverId,
            Name = "orders",
            Type = ManagedServiceType.PostgreSql,
            ContainerName = "harbora-orders",
            DatabaseName = "orders",
            Username = "postgres",
            EncryptedPassword = _protector.Protect("admin_secret"),
            InternalPort = 5432,
            Status = ServiceStatus.Running
        };
        _db.ManagedServices.Add(svc);
        _db.SaveChanges();
        return svc;
    }

    private (Guid WorkspaceId, Guid EnvironmentId) SeedEnvironment()
    {
        var workspaceId = Guid.CreateVersion7();
        var projectId = Guid.CreateVersion7();
        var environmentId = Guid.CreateVersion7();
        _db.Add(new Harbora.Domain.Identity.Workspace { Id = workspaceId, Name = "Acme", Slug = "acme" });
        _db.Add(new Harbora.Domain.Projects.Project
        { Id = projectId, WorkspaceId = workspaceId, Name = "Shop", Slug = "shop" });
        _db.Add(new Harbora.Domain.Projects.Environment
        {
            Id = environmentId, WorkspaceId = workspaceId, ProjectId = projectId,
            Name = "Production", Slug = "production", IsDefault = true
        });
        _db.SaveChanges();
        return (workspaceId, environmentId);
    }

    [Fact]
    public async Task A_database_on_this_machine_gets_a_session()
    {
        var (workspaceId, environmentId) = SeedEnvironment();
        var service = Database(Guid.Empty, workspaceId, environmentId);

        var result = await Service().OpenAsync(service.Id, default);

        result.Ok.Should().BeTrue(result.Refusal);
        _panel.Calls.Should().Contain(c => c.Operation == "RunContainerAsync");
    }

    [Fact]
    public async Task A_database_on_another_machine_is_refused_before_anything_starts()
    {
        var (workspaceId, environmentId) = SeedEnvironment();
        var serverId = Guid.NewGuid();
        var remote = new FakeDockerEngine();
        _engines.On(serverId, remote);
        var service = Database(serverId, workspaceId, environmentId);

        var result = await Service().OpenAsync(service.Id, default);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().NotBeNullOrWhiteSpace();
        result.Refusal.Should().Contain("orders");
        remote.Calls.Should().BeEmpty("a container started on the remote host would have no route reaching it");
        _panel.Calls.Should().BeEmpty("nothing should run here either, on a promise the proxy cannot keep");
        (await _db.Routes.CountAsync()).Should().Be(0, "no route may be published for a session that never started");
    }

    [Fact]
    public async Task A_server_that_cannot_be_resolved_becomes_a_named_refusal_not_an_exception()
    {
        var (workspaceId, environmentId) = SeedEnvironment();
        var serverId = Guid.NewGuid();
        _engines.Unreachable(serverId, "no agent endpoint and no node is enrolled on it");
        var service = Database(serverId, workspaceId, environmentId);

        var result = await Service().OpenAsync(service.Id, default);

        result.Ok.Should().BeFalse();
        result.Refusal.Should().Contain("orders");
        result.Refusal.Should().Contain("no agent endpoint");
        _panel.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// HARBORA-0066: the route save and the proxy apply used to run on the web request's own token,
    /// unguarded — so a cancellation landing between them (the operator navigating away right after
    /// the route committed but before the apply could even start) left an ENABLED route row
    /// published to a container nobody wired up, exactly the state the returned-failure branch right
    /// next to it already knew how to undo. This proves the cancellation path now takes the same
    /// route back.
    /// </summary>
    [Fact]
    public async Task A_cancellation_between_the_save_and_the_apply_withdraws_the_route_and_the_container()
    {
        var (workspaceId, environmentId) = SeedEnvironment();
        var service = Database(Guid.Empty, workspaceId, environmentId);
        using var cts = new CancellationTokenSource();
        var cancellingProxy = new CancelsOnFirstApply(cts, _proxy);

        var act = async () => await Service(cancellingProxy).OpenAsync(service.Id, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "the caller's own request-aborted token should surface, not a swallowed refusal");
        (await _db.Routes.CountAsync()).Should().Be(0,
            "a cancellation between the save and the apply must withdraw the route it just committed, " +
            "the same as a returned failure does");
        _panel.Calls.Should().Contain(c => c.Operation == "RemoveContainerAsync",
            "the container started before the save must be removed too, not left running for nothing");
    }

    public void Dispose() => _db.Dispose();
}
