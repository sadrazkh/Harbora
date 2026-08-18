using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Identity;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whatever gates app creation has to count a replicated app's memory and CPU once per replica, not
/// once total — three containers hold three times what one does, and a quota that measured only one
/// would let a customer scale straight past a cap this class exists to enforce.
/// </summary>
public class QuotaReplicaTests : IDisposable
{
    private const long Mb = 1024L * 1024;

    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("quota-replicas-" + Guid.CreateVersion7()).Options);

    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly Guid _planId = Guid.CreateVersion7();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private void GivenWallPlan(long maxMemoryBytes = 0, double maxCpuCores = 0, int maxApps = 0)
    {
        _db.Plans.Add(new Plan
        {
            Id = _planId, Name = "Starter", AllowsOverage = false,
            MaxMemoryBytes = maxMemoryBytes, MaxCpuCores = maxCpuCores, MaxApps = maxApps, IsEnabled = true
        });
        _db.Workspaces.Add(new Workspace { Id = _workspace, Name = "Acme", Slug = "acme", PlanId = _planId });
        _db.SaveChanges();
    }

    private void GivenApp(long memoryBytes, double cpuCores = 0, int? replicas = null)
    {
        _db.Apps.Add(new App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = _workspace, ServerId = Guid.CreateVersion7(),
            Name = "api", Slug = "api-" + Guid.NewGuid().ToString("n")[..6],
            MemoryLimitBytes = memoryBytes, CpuLimit = cpuCores, DesiredReplicas = replicas
        });
        _db.SaveChanges();
    }

    private QuotaService Quota() => new(_db, Options.Create(new BillingOptions { Enabled = true }));

    [Fact]
    public async Task Three_replicas_of_a_200mb_app_commit_600mb_not_200()
    {
        GivenWallPlan(maxMemoryBytes: 512 * Mb);
        GivenApp(memoryBytes: 200 * Mb, replicas: 3);

        var usage = await Quota().GetUsageAsync(_workspace, default);

        usage.MemoryUsedBytes.Should().Be(600 * Mb,
            "three containers at 200 MB each hold 600 MB, whatever the app row says as a single number");
    }

    [Fact]
    public async Task A_replicated_apps_memory_blocks_a_new_app_a_single_reading_would_have_allowed()
    {
        // 200 MB × 3 replicas = 600 MB already committed against a 512 MB cap — over it before
        // anything new is even considered. A quota that read DesiredReplicas as 1 would see 200 MB
        // used and wave a new 300 MB app straight through.
        GivenWallPlan(maxMemoryBytes: 512 * Mb);
        GivenApp(memoryBytes: 200 * Mb, replicas: 3);

        var check = await Quota().CanAddAppAsync(_workspace, null, null, default);

        check.Allowed.Should().BeFalse("the workspace is already over its cap once replicas are counted honestly");
        check.Reason.Should().Contain("Memory");
    }

    [Fact]
    public async Task Three_replicas_of_a_cpu_bound_app_commit_three_times_the_cores()
    {
        GivenWallPlan(maxCpuCores: 2);
        GivenApp(memoryBytes: 0, cpuCores: 1, replicas: 3);

        var usage = await Quota().GetUsageAsync(_workspace, default);

        usage.CpuUsed.Should().Be(3, "three replicas at one core each hold three cores, not one");
    }

    [Fact]
    public async Task An_app_with_no_replicas_set_still_commits_exactly_its_own_limit()
    {
        // DesiredReplicas null means "never touched", not "zero" — the ordinary case for almost
        // every app today, and it must count exactly as it always has.
        GivenWallPlan(maxMemoryBytes: 512 * Mb);
        GivenApp(memoryBytes: 200 * Mb, replicas: null);

        var usage = await Quota().GetUsageAsync(_workspace, default);

        usage.MemoryUsedBytes.Should().Be(200 * Mb);
    }
}
