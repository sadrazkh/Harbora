using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A database's share of the host.
///
/// Applications were sized, capped and counted against the workspace's quota. Databases were not:
/// the container was created with no memory or CPU limit at all, and the quota's memory figure
/// summed applications only. So a plan could measure half of what a workspace was using, report the
/// tenant as comfortably inside it, and the host could still run out of memory.
/// </summary>
public class ManagedServiceResourcePlanTests
{
    private const long MB = 1024 * 1024;

    private static HarboraDbContext Db() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("plan-" + Guid.CreateVersion7()).Options);

    private static (HarboraDbContext Db, QuotaService Quota, Guid Workspace) Build(
        long maxMemory = 1024 * MB, double maxCpu = 4, string? allowedSizes = null)
    {
        var db = Db();
        var workspace = Guid.CreateVersion7();

        var plan = new Plan
        {
            Id = Guid.CreateVersion7(), Name = "Starter", IsDefault = true,
            MaxApps = 100, MaxServices = 100,
            MaxMemoryBytes = maxMemory, MaxCpuCores = maxCpu,
            AllowedSizeKeys = allowedSizes ?? string.Empty
        };
        db.Add(plan);
        db.Add(new Harbora.Domain.Identity.Workspace
        {
            Id = workspace, Name = "Acme", Slug = "acme", PlanId = plan.Id
        });

        db.AddRange(
            new InstanceSize { Id = Guid.CreateVersion7(), Key = "nano", Name = "Nano", CpuCores = 0.25, MemoryBytes = 256 * MB, IsEnabled = true, SortOrder = 1 },
            new InstanceSize { Id = Guid.CreateVersion7(), Key = "large", Name = "Large", CpuCores = 2, MemoryBytes = 2048 * MB, IsEnabled = true, SortOrder = 9 });

        db.SaveChanges();
        return (db, new QuotaService(db), workspace);
    }

    private static ManagedService Database(Guid workspace, long memory, double cpu) => new()
    {
        Id = Guid.CreateVersion7(), WorkspaceId = workspace, ServerId = Guid.CreateVersion7(),
        Name = "shop", ContainerName = "harbora-svc-shop", Type = ManagedServiceType.PostgreSql,
        InternalPort = 5432, MemoryLimitBytes = memory, CpuLimit = cpu
    };

    [Fact]
    public async Task A_database_counts_towards_the_memory_quota()
    {
        // It did not. The snapshot summed applications, so a workspace full of databases measured
        // as empty and every check against the plan passed.
        var (db, quota, workspace) = Build(maxMemory: 512 * MB);
        db.Add(Database(workspace, 400 * MB, 1));
        await db.SaveChangesAsync();

        var usage = await quota.GetUsageAsync(workspace, default);

        usage.MemoryUsedBytes.Should().Be(400 * MB);
    }

    [Fact]
    public async Task A_database_that_would_not_fit_is_refused()
    {
        var (db, quota, workspace) = Build(maxMemory: 512 * MB);
        db.Add(Database(workspace, 400 * MB, 1));
        await db.SaveChangesAsync();

        var check = await quota.CanAddServiceAsync(workspace, "large", default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("Memory");
    }

    [Fact]
    public async Task A_database_that_fits_is_allowed()
    {
        var (db, quota, workspace) = Build(maxMemory: 1024 * MB);
        db.Add(Database(workspace, 256 * MB, 0.25));
        await db.SaveChangesAsync();

        (await quota.CanAddServiceAsync(workspace, "nano", default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_database_is_refused_a_size_the_plan_does_not_allow()
    {
        // The same rule an application gets. Without it a plan could restrict applications to nano
        // and a database of any size could sit beside them.
        var (_, quota, workspace) = Build(allowedSizes: "nano");

        var check = await quota.CanAddServiceAsync(workspace, "large", default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("large");
    }

    [Fact]
    public async Task The_cpu_quota_counts_databases_too()
    {
        // Memory deliberately generous, so CPU is the only constraint that can bind. With the
        // default ceiling the memory check fired first and this passed for the wrong reason.
        var (db, quota, workspace) = Build(maxMemory: 8192 * MB, maxCpu: 2);
        db.Add(Database(workspace, 128 * MB, 1.5));
        await db.SaveChangesAsync();

        var check = await quota.CanAddServiceAsync(workspace, "large", default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("CPU");
    }

    [Fact]
    public async Task A_database_from_before_this_is_unlimited_and_counts_as_nothing()
    {
        // Zero still means no ceiling, for every service created before databases had a plan.
        // Retro-fitting a limit onto a running database would cap it below what it already uses and
        // the kernel would kill it — a data-loss event dressed up as a quota fix.
        var (db, quota, workspace) = Build(maxMemory: 512 * MB);
        db.Add(Database(workspace, 0, 0));
        await db.SaveChangesAsync();

        (await quota.GetUsageAsync(workspace, default)).MemoryUsedBytes.Should().Be(0);
        (await quota.CanAddServiceAsync(workspace, "nano", default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Applications_and_databases_share_one_budget()
    {
        // The point of counting both: they run on the same host and take from the same pool.
        var (db, quota, workspace) = Build(maxMemory: 512 * MB);
        db.Add(new Harbora.Domain.Apps.App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace, ServerId = Guid.CreateVersion7(),
            Name = "api", Slug = "api", MemoryLimitBytes = 300 * MB, CpuLimit = 1
        });
        db.Add(Database(workspace, 200 * MB, 0.5));
        await db.SaveChangesAsync();

        (await quota.GetUsageAsync(workspace, default)).MemoryUsedBytes.Should().Be(500 * MB);
        (await quota.CanAddServiceAsync(workspace, "nano", default)).Allowed.Should().BeFalse();
    }
}
