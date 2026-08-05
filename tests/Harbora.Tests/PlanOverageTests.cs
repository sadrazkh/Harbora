using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Tenancy;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who a limit change is already biting.
///
/// Lowering a limit takes nothing away from anyone, so the only sign it did anything is this list.
/// It covered applications, databases and CPU — memory and disk, the two an operator is most likely
/// to be lowering and the two that actually cost something on the host, were not checked at all.
/// </summary>
public class PlanOverageTests
{
    private static WorkspaceUsage Usage(
        int apps = 0, int maxApps = 0,
        int services = 0, int maxServices = 0,
        long memory = 0, long maxMemory = 0,
        double cpu = 0, double maxCpu = 0,
        long disk = 0, long maxDisk = 0, int unmeasured = 0) =>
        new("Standard", apps, maxApps, services, maxServices, memory, maxMemory, cpu, maxCpu,
            Suspended: false, DiskUsedBytes: disk, MaxDiskBytes: maxDisk, DiskUnmeasured: unmeasured);

    [Fact]
    public void A_workspace_inside_every_limit_is_on_no_list()
    {
        PlanOverage.For(Usage(apps: 2, maxApps: 5, memory: 512, maxMemory: 2048)).Should().BeEmpty();
    }

    [Fact]
    public void Memory_over_the_plan_is_reported()
    {
        // The one that was missing. An operator halving a plan's memory saw nothing change.
        var breaches = PlanOverage.For(Usage(memory: 4096, maxMemory: 2048));

        breaches.Should().ContainSingle()
            .Which.Should().Be(new PlanBreach(PlanResource.Memory, 4096, 2048));
    }

    [Fact]
    public void Disk_over_the_plan_is_reported()
    {
        PlanOverage.For(Usage(disk: 30, maxDisk: 20))
            .Should().ContainSingle().Which.Resource.Should().Be(PlanResource.Disk);
    }

    [Fact]
    public void Exactly_at_the_limit_is_inside_it()
    {
        // "Over" means over. A tenant sitting precisely on their limit has not broken it, and
        // putting them on a warning list is how the list stops being read.
        PlanOverage.For(Usage(apps: 5, maxApps: 5, memory: 2048, maxMemory: 2048, disk: 20, maxDisk: 20))
            .Should().BeEmpty();
    }

    [Fact]
    public void Zero_means_unlimited_and_nothing_is_over_unlimited()
    {
        PlanOverage.For(Usage(apps: 99, services: 99, memory: long.MaxValue, cpu: 64, disk: long.MaxValue))
            .Should().BeEmpty();
    }

    [Fact]
    public void Cpu_arithmetic_does_not_invent_a_breach()
    {
        // 0.1 + 0.2 is 0.30000000000000004 in binary floating point. Three quarter-core apps on a
        // 0.3-core plan is exactly at the limit, and reporting it as over is a bug that only ever
        // shows up on somebody else's tenant list.
        PlanOverage.For(Usage(cpu: 0.1 + 0.2, maxCpu: 0.3)).Should().BeEmpty();
    }

    [Fact]
    public void Cpu_genuinely_over_is_still_reported()
    {
        // The tolerance must not be a hole. A tenth of a core over is over.
        PlanOverage.For(Usage(cpu: 0.4, maxCpu: 0.3))
            .Should().ContainSingle().Which.Resource.Should().Be(PlanResource.Cpu);
    }

    [Fact]
    public void Every_limit_that_is_broken_is_listed_not_just_the_first()
    {
        // A workspace over on three things needs three lines. Stopping at the first turns a plan
        // change into a game of whack-a-mole.
        var breaches = PlanOverage.For(Usage(
            apps: 6, maxApps: 5,
            services: 3, maxServices: 1,
            memory: 4096, maxMemory: 1024,
            cpu: 4, maxCpu: 2,
            disk: 50, maxDisk: 10));

        breaches.Select(b => b.Resource).Should().Equal(
            PlanResource.Apps, PlanResource.Services, PlanResource.Memory,
            PlanResource.Cpu, PlanResource.Disk);
    }

    [Fact]
    public void Both_figures_are_carried_so_the_line_can_say_how_far_over()
    {
        // "Over their plan" on its own is not actionable. The first question is always "by how
        // much", and the answer must not be recomputed by whatever renders it.
        var breach = PlanOverage.For(Usage(apps: 9, maxApps: 4)).Single();

        breach.Used.Should().Be(9);
        breach.Limit.Should().Be(4);
    }

    [Fact]
    public void Volumes_nobody_measured_do_not_put_a_tenant_on_the_list()
    {
        // An unmeasured volume is unknown, not large. Guessing here would name tenants as over a
        // limit on the strength of a number nobody collected.
        PlanOverage.For(Usage(disk: 5, maxDisk: 20, unmeasured: 12)).Should().BeEmpty();
    }
}
