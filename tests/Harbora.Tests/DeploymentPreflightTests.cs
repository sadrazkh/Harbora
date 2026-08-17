using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Jobs;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P7 (2026-08-17 app-environment-management design): three pre-flight checks at deploy queue time,
/// each refusing for its own named reason rather than letting a doomed build start and fail with a
/// raw Docker string. Capacity and disk are covered here, against the real
/// <see cref="DeploymentEngine"/>; the third item, a burned host port, lives in
/// <see cref="HostPortAllocatorTests"/> because it advances rather than refuses in the ordinary case.
/// </summary>
public class DeploymentPreflightTests
{
    private sealed class NoopQueue : IJobQueue
    {
        public int Enqueued;
        public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
        { Enqueued++; return Task.FromResult(Guid.NewGuid()); }
        public Task<Guid> EnqueueExclusiveAsync(JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null, CancellationToken ct = default)
        { Enqueued++; return Task.FromResult(Guid.NewGuid()); }
        public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class Clock : ISystemClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }

    /// <summary>Answers exactly what the test hands it, so capacity refusals and approvals are both
    /// deterministic without standing up a real node capacity computation.</summary>
    private sealed class StubScheduler(PlacementResult answer) : ISchedulerService
    {
        public Guid? AskedServerId { get; private set; }
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct) =>
            throw new NotSupportedException("Queue-time re-check calls CheckAsync, not PlaceAsync.");
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct)
        { AskedServerId = serverId; return Task.FromResult(answer); }
    }

    private static HarboraDbContext NewDb() =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("preflight-" + Guid.NewGuid()).Options);

    private static App SeedApp(HarboraDbContext db, Guid serverId)
    {
        var app = new App { WorkspaceId = Guid.NewGuid(), ServerId = serverId, Name = "web", Slug = "web" };
        db.Apps.Add(app);
        db.SaveChanges();
        return app;
    }

    // ---- capacity ------------------------------------------------------------------------------

    [Fact]
    public async Task A_node_with_no_capacity_left_refuses_the_queue_with_its_own_reason()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        var scheduler = new StubScheduler(PlacementResult.Fail("'web-01' does not have enough free capacity.", "«web-01» ظرفیت آزاد کافی ندارد."));
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock(), quota: null, scheduler: scheduler);

        var queue = () => engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var thrown = await queue.Should().ThrowAsync<CapacityRefusedException>();
        thrown.Which.Message.Should().Contain("capacity");
        thrown.Which.ReasonFa.Should().Contain("ظرفیت");
        scheduler.AskedServerId.Should().Be(serverId, "the check must ask about the app's own server, not a placeholder");
        db.Deployments.Should().BeEmpty("a refused queue attempt must not leave a half-created deployment behind");
    }

    [Fact]
    public async Task A_node_with_room_lets_the_deploy_queue_normally()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        var scheduler = new StubScheduler(PlacementResult.Placed(serverId));
        var queue = new NoopQueue();
        var engine = new DeploymentEngine(db, queue, new Clock(), quota: null, scheduler: scheduler);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        db.Deployments.Should().ContainSingle(d => d.Id == id);
        queue.Enqueued.Should().Be(1);
    }

    [Fact]
    public async Task No_scheduler_configured_skips_the_capacity_check_entirely()
    {
        // The 3-argument construction every other DeploymentEngine test in this suite already uses —
        // proving it still queues normally is what keeps this pre-flight opt-in for tests that were
        // never about capacity at all.
        using var db = NewDb();
        var app = SeedApp(db, Guid.CreateVersion7());
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        db.Deployments.Should().ContainSingle(d => d.Id == id);
    }

    // ---- disk ------------------------------------------------------------------------------------

    private static void SeedNode(HarboraDbContext db, Guid serverId, long freeDiskBytes)
    {
        db.Servers.Add(new Server { Id = serverId, Name = "web-01", Hostname = "10.0.0.9" });
        db.Nodes.Add(new Node
        {
            NodeId = "node-" + serverId, Name = "web-01", Status = NodeStatus.Online, Health = "healthy",
            ServerId = serverId, FreeDiskBytes = freeDiskBytes
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_node_below_the_configured_free_disk_floor_refuses_and_names_both_numbers()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        SeedNode(db, serverId, freeDiskBytes: 200L * 1024 * 1024); // 200 MB free
        var options = Options.Create(new MonitoringOptions { DeployMinFreeDiskBytes = 1L * 1024 * 1024 * 1024 }); // 1 GiB floor
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock(), quota: null, monitoringOptions: options);

        var queueAttempt = () => engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        var thrown = await queueAttempt.Should().ThrowAsync<LowDiskRefusedException>();
        // A test asserting only that it refused would pass on a refusal for the wrong reason — the
        // threshold it refused against has to be provably the configured one, not a guess.
        thrown.Which.FreeBytes.Should().Be(200L * 1024 * 1024);
        thrown.Which.ThresholdBytes.Should().Be(1L * 1024 * 1024 * 1024);
        thrown.Which.Message.Should().ContainAll("MB", "GB");
        thrown.Which.ReasonFa.Should().NotBeNullOrWhiteSpace();
        db.Deployments.Should().BeEmpty();
    }

    [Fact]
    public async Task The_disk_refusal_is_distinguishable_from_a_capacity_refusal()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        SeedNode(db, serverId, freeDiskBytes: 1024); // essentially nothing
        var options = Options.Create(new MonitoringOptions { DeployMinFreeDiskBytes = 1L * 1024 * 1024 * 1024 });
        var scheduler = new StubScheduler(PlacementResult.Placed(serverId)); // capacity is fine
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock(), quota: null, scheduler: scheduler, monitoringOptions: options);

        var queueAttempt = () => engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        // Both are InvalidOperationException-derived by design (matching the coalescing-conflict
        // throws already in this method), so the type itself is what a caller distinguishes on.
        (await queueAttempt.Should().ThrowAsync<LowDiskRefusedException>())
            .Which.Should().NotBeOfType<CapacityRefusedException>();
    }

    [Fact]
    public async Task A_node_with_room_on_disk_queues_normally()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        SeedNode(db, serverId, freeDiskBytes: 50L * 1024 * 1024 * 1024); // 50 GB free
        var options = Options.Create(new MonitoringOptions { DeployMinFreeDiskBytes = 1L * 1024 * 1024 * 1024 });
        var queue = new NoopQueue();
        var engine = new DeploymentEngine(db, queue, new Clock(), quota: null, monitoringOptions: options);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        db.Deployments.Should().ContainSingle(d => d.Id == id);
    }

    [Fact]
    public async Task A_server_with_no_node_row_is_read_as_unmeasured_rather_than_full()
    {
        // A server that predates node enrolment, or one that has never sent a heartbeat, has no
        // Node row at all. Refusing every deploy to it because a figure is missing would be worse
        // than the gap this item closes.
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        var options = Options.Create(new MonitoringOptions { DeployMinFreeDiskBytes = 1L * 1024 * 1024 * 1024 });
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock(), quota: null, monitoringOptions: options);

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        db.Deployments.Should().ContainSingle(d => d.Id == id);
    }

    [Fact]
    public async Task No_monitoring_options_configured_skips_the_disk_check_entirely()
    {
        using var db = NewDb();
        var serverId = Guid.CreateVersion7();
        var app = SeedApp(db, serverId);
        SeedNode(db, serverId, freeDiskBytes: 1); // would refuse under any real threshold
        var engine = new DeploymentEngine(db, new NoopQueue(), new Clock());

        var id = await engine.QueueDeploymentAsync(
            new DeploymentRequest(app.Id, DeploymentTrigger.Manual, Guid.NewGuid()), default);

        db.Deployments.Should().ContainSingle(d => d.Id == id);
    }
}
