using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
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
/// 1.6 (redis-eviction): proving, through the fake engine, that a chosen <c>maxmemory</c>/
/// <c>maxmemory-policy</c> actually reaches a running Redis, that a stopped one is told the truth
/// about being merely queued, that an out-of-range cap is refused by name, and — the one this whole
/// feature exists not to break — that a Redis nobody has touched keeps exactly the command line it
/// has always had.
/// </summary>
public class RedisMemoryPolicyEngineTests
{
    [Fact]
    public async Task An_untouched_redis_instance_is_rebuilt_with_exactly_todays_command_line()
    {
        // The defining proof this feature must not fail: RedisEvictionPolicy/RedisMaxMemoryBytes are
        // both at their zero-value defaults here, the same as every Redis provisioned before this
        // shipped, and CommandArguments must therefore contribute nothing at all.
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Command.Should().Equal(
            ["redis-server", "--requirepass", h.PasswordFor(svc), "--appendonly", "yes"],
            "an instance nobody has set a policy on must be rebuilt with the exact command line it " +
            "has always had — not one extra argument");
    }

    [Fact]
    public async Task Provisioning_a_redis_with_a_chosen_policy_carries_it_on_the_run_request()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(evictionPolicy: RedisMemoryPolicy.AllKeysLru, maxMemoryBytes: 256L * 1024 * 1024);

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Command.Should().Equal(
            "redis-server", "--requirepass", h.PasswordFor(svc), "--appendonly", "yes",
            "--maxmemory-policy", "allkeys-lru", "--maxmemory", (256L * 1024 * 1024).ToString());
    }

    [Fact]
    public async Task Provisioning_bakes_the_stored_policy_in_and_clears_the_unpublished_flag()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(evictionPolicy: RedisMemoryPolicy.NoEviction, maxMemoryBytes: 64L * 1024 * 1024);
        svc.HasUnpublishedChanges = true;
        await h.SaveAsync();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var after = await h.ReadServiceAsync(svc.Id);
        after.HasUnpublishedChanges.Should().BeFalse(
            "the container was just rebuilt from this row's own settings, so they are no longer merely intended");
    }

    [Fact]
    public async Task A_change_on_a_running_instance_reaches_the_engine_through_a_live_config_set()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Running);

        var outcome = await h.Engine().UpdateRedisMemoryPolicyAsync(
            svc.Id, RedisMemoryPolicy.AllKeysLru, 128L * 1024 * 1024, default);

        outcome.WasRunning.Should().BeTrue();
        outcome.AppliedLive.Should().BeTrue();
        outcome.LiveApplyError.Should().BeNull();

        var oneOff = h.Docker.OneOffCommands.Should().ContainSingle().Subject;
        oneOff.Should().Contain("CONFIG SET maxmemory-policy", "the policy is not merely stored, it is sent live");
        oneOff.Should().Contain("CONFIG SET maxmemory '134217728'");

        // Policy before cap — see RedisMemoryPolicy.LiveApply's own doc for why the other order
        // leaves a window in which a full instance is over its brand-new maxmemory while still
        // holding noeviction.
        oneOff.IndexOf("maxmemory-policy", StringComparison.Ordinal).Should()
            .BeLessThan(oneOff.IndexOf("CONFIG SET maxmemory ", StringComparison.Ordinal));

        var stored = await h.ReadServiceAsync(svc.Id);
        stored.RedisEvictionPolicy.Should().Be(RedisMemoryPolicy.AllKeysLru);
        stored.RedisMaxMemoryBytes.Should().Be(128L * 1024 * 1024);

        // Live now is not the same claim as durable — a plain restart of this same container would
        // start it with the launch arguments it already has, which never included these flags.
        stored.HasUnpublishedChanges.Should().BeTrue(
            "a successful CONFIG SET does not survive a restart until the container is rebuilt");
    }

    [Fact]
    public async Task A_change_on_a_stopped_instance_is_marked_pending_rather_than_silently_dropped()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Stopped);

        var outcome = await h.Engine().UpdateRedisMemoryPolicyAsync(
            svc.Id, RedisMemoryPolicy.NoEviction, 0, default);

        outcome.WasRunning.Should().BeFalse();
        outcome.AppliedLive.Should().BeFalse();
        outcome.LiveApplyError.Should().BeNull("nothing was attempted, so there is nothing to have failed");

        h.Docker.Calls.Should().NotContain(c => c.Operation == nameof(FakeDockerEngine.RunOneOffAsync),
            "a stopped container has nothing to reach live");

        var stored = await h.ReadServiceAsync(svc.Id);
        stored.RedisEvictionPolicy.Should().Be(RedisMemoryPolicy.NoEviction);
        stored.HasUnpublishedChanges.Should().BeTrue("saved, but only a rebuild will make it real");
    }

    [Fact]
    public async Task An_out_of_range_maxmemory_is_refused_by_name_and_nothing_is_persisted()
    {
        using var h = new RedisEngineHarness();
        // A 100 MB container ceiling — RedisMemoryPolicy.Ceiling leaves 80 MB usable.
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Running, memoryLimitBytes: 100L * 1024 * 1024);

        var act = () => h.Engine().UpdateRedisMemoryPolicyAsync(
            svc.Id, RedisMemoryPolicy.AllKeysLru, 200L * 1024 * 1024, default);

        var thrown = await act.Should().ThrowAsync<RedisMemoryPolicyRefusedException>();
        thrown.Which.Message.Should().Contain("memory cap", "the refusal must name the setting, not just say 'operation failed'");
        thrown.Which.Message.Should().Contain("200 MB").And.Contain("80 MB",
            "naming the figures is what makes this actionable rather than a bare refusal");

        h.Docker.Calls.Should().NotContain(c => c.Operation == nameof(FakeDockerEngine.RunOneOffAsync));

        var stored = await h.ReadServiceAsync(svc.Id);
        stored.RedisEvictionPolicy.Should().BeNull("a refused request must change nothing");
        stored.RedisMaxMemoryBytes.Should().Be(0);
        stored.HasUnpublishedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task A_negative_maxmemory_is_refused_by_name()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Running);

        var act = () => h.Engine().UpdateRedisMemoryPolicyAsync(svc.Id, RedisMemoryPolicy.AllKeysLru, -1, default);

        await act.Should().ThrowAsync<RedisMemoryPolicyRefusedException>()
            .WithMessage("*negative*");
    }

    [Fact]
    public async Task An_unknown_policy_key_is_refused_by_its_own_name()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Running);

        var act = () => h.Engine().UpdateRedisMemoryPolicyAsync(svc.Id, "made-up-policy", 0, default);

        var thrown = await act.Should().ThrowAsync<RedisMemoryPolicyRefusedException>();
        thrown.Which.Message.Should().Contain("made-up-policy");
        thrown.Which.ReasonFa.Should().NotBeNullOrWhiteSpace("the refusal must be sayable in the reader's own language too");
    }

    [Fact]
    public async Task A_running_instance_that_refuses_the_live_change_reports_it_by_name_rather_than_claiming_success()
    {
        using var h = new RedisEngineHarness();
        var svc = await h.SeedRedisAsync(status: ServiceStatus.Running);
        h.Docker.OneOffExitCode = 1;
        h.Docker.OneOffOutput.Add("(error) ERR This instance has cluster support disabled");

        var outcome = await h.Engine().UpdateRedisMemoryPolicyAsync(
            svc.Id, RedisMemoryPolicy.AllKeysLru, 64L * 1024 * 1024, default);

        outcome.WasRunning.Should().BeTrue();
        outcome.AppliedLive.Should().BeFalse();
        outcome.LiveApplyError.Should().Contain("cluster support disabled",
            "a live refusal must be named, not folded into a generic 'operation failed'");

        // Still saved: a rebuild will pick it up even though the live attempt did not land.
        var stored = await h.ReadServiceAsync(svc.Id);
        stored.RedisEvictionPolicy.Should().Be(RedisMemoryPolicy.AllKeysLru);
        stored.HasUnpublishedChanges.Should().BeTrue();
    }

    [Fact]
    public async Task Only_redis_has_a_memory_eviction_policy_to_set()
    {
        using var h = new RedisEngineHarness();
        var postgres = await h.SeedRedisAsync(status: ServiceStatus.Running);
        postgres.Type = ManagedServiceType.PostgreSql;
        await h.SaveAsync();

        var act = () => h.Engine().UpdateRedisMemoryPolicyAsync(postgres.Id, RedisMemoryPolicy.AllKeysLru, 0, default);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon, seeding Redis rows —
/// the counterpart of <c>RotationHarness</c> (PostgreSQL-only) for the one engine that needs its own
/// memory settings exercised.</summary>
internal sealed class RedisEngineHarness : IDisposable
{
    private readonly string _database = "redis-memory-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly HarboraDbContext _db;

    public FakeDockerEngine Docker { get; } = new();
    public PassthroughProtector Protector { get; } = new();
    public FixedClock Clock { get; } = new();

    public RedisEngineHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        var project = new Harbora.Domain.Projects.Project
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Name = "shop", Slug = "shop"
        };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, ProjectId = project.Id,
            Name = "prod", Slug = "prod", IsDefault = true
        };
        _db.Projects.Add(project);
        _db.Environments.Add(environment);
        _db.SaveChanges();
        _environmentId = environment.Id;
    }

    private readonly Guid _environmentId;

    private HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    public ManagedServiceEngine Engine() => new(
        _db,
        new SingleEngineFactory(Docker),
        Protector,
        new NoopJobQueue(),
        new Harbora.Infrastructure.Billing.BillingGate(
            _db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<ManagedServiceEngine>.Instance);

    public async Task<ManagedService> SeedRedisAsync(
        string? evictionPolicy = null, long maxMemoryBytes = 0,
        ServiceStatus status = ServiceStatus.Running, long memoryLimitBytes = 0)
    {
        var name = "cache-" + Guid.NewGuid().ToString("N")[..8];
        var service = new ManagedService
        {
            WorkspaceId = _workspaceId,
            EnvironmentId = _environmentId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.Redis,
            Version = "7-alpine",
            ContainerName = $"harbora-svc-{name}",
            InternalPort = 6379,
            Username = "harbora",
            EncryptedPassword = Protector.Protect($"{name}-original-pw12"),
            DatabaseName = string.Empty,
            VolumeName = $"harbora-svc-{name}-data",
            Status = status,
            MemoryLimitBytes = memoryLimitBytes,
            RedisEvictionPolicy = evictionPolicy,
            RedisMaxMemoryBytes = maxMemoryBytes
        };
        _db.ManagedServices.Add(service);
        await _db.SaveChangesAsync();
        return service;
    }

    public string PasswordFor(ManagedService svc) => Protector.Unprotect(svc.EncryptedPassword);

    public async Task<ManagedService> ReadServiceAsync(Guid id)
    {
        using var db = Read();
        return await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public Task SaveAsync() => _db.SaveChangesAsync();

    public void Dispose() => _db.Dispose();
}
