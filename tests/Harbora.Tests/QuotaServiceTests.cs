using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The plan limits, against a real database.
///
/// `MaxDiskBytes` was on the plan, on the pricing screen, and checked nowhere — a limit that could
/// be sold and was never applied. Enforcing it means summing what has actually been measured, and
/// being careful about the half that has not.
/// </summary>
public class QuotaServiceTests : IDisposable
{
    private readonly HarboraDbContext _db;
    private readonly Guid _workspace = Guid.NewGuid();
    private readonly Guid _planId = Guid.NewGuid();

    private const long Gb = 1024L * 1024 * 1024;

    public QuotaServiceTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("quota-" + Guid.NewGuid()).Options);
    }

    public void Dispose() => _db.Dispose();

    private QuotaService Service() => new(_db);

    private void GivenPlan(long maxDiskGb, int maxApps = 0, int maxServices = 0)
    {
        _db.Plans.Add(new Plan
        {
            Id = _planId, Name = "Test", MaxDiskBytes = maxDiskGb * Gb,
            MaxApps = maxApps, MaxServices = maxServices, IsEnabled = true
        });
        _db.Workspaces.Add(new Workspace { Id = _workspace, Name = "Acme", Slug = "acme", PlanId = _planId });
        _db.SaveChanges();
    }

    private Guid GivenAppWithVolume(long? measuredBytes)
    {
        var app = new App { WorkspaceId = _workspace, Name = "a", Slug = "a-" + Guid.NewGuid().ToString("N")[..6] };
        app.Volumes.Add(new Volume { Name = "v-" + Guid.NewGuid().ToString("N")[..6], MountPath = "/data", StorageBytes = measuredBytes });
        _db.Apps.Add(app);
        _db.SaveChanges();
        return app.Id;
    }

    private void GivenDatabase(long? measuredBytes)
    {
        _db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = _workspace, Name = "db", Type = ManagedServiceType.PostgreSql,
            ContainerName = "harbora-svc-db-" + Guid.NewGuid().ToString("N")[..6],
            StorageBytes = measuredBytes
        });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Disk_use_adds_up_databases_and_app_volumes_together()
    {
        // Both take room on the same disk, so counting one and not the other is a limit that only
        // half applies.
        GivenPlan(maxDiskGb: 10);
        GivenDatabase(3 * Gb);
        GivenAppWithVolume(2 * Gb);

        var usage = await Service().DiskUsageAsync(_workspace, default);

        usage.MeasuredBytes.Should().Be(5 * Gb);
        usage.UnmeasuredResources.Should().Be(0);
    }

    [Fact]
    public async Task A_volume_nobody_has_measured_is_counted_as_unknown_not_as_empty()
    {
        // Assuming zero is how a quota quietly stops being one.
        GivenPlan(maxDiskGb: 10);
        GivenDatabase(1 * Gb);
        GivenAppWithVolume(null);

        var usage = await Service().DiskUsageAsync(_workspace, default);

        usage.MeasuredBytes.Should().Be(1 * Gb);
        usage.UnmeasuredResources.Should().Be(1);
    }

    [Fact]
    public async Task Another_workspaces_disk_is_not_counted_against_this_one()
    {
        GivenPlan(maxDiskGb: 10);
        GivenDatabase(1 * Gb);

        var stranger = Guid.NewGuid();
        _db.ManagedServices.Add(new ManagedService
        {
            WorkspaceId = stranger, Name = "theirs", Type = ManagedServiceType.PostgreSql,
            ContainerName = "harbora-svc-theirs", StorageBytes = 500 * Gb
        });
        var app = new App { WorkspaceId = stranger, Name = "theirs", Slug = "theirs" };
        app.Volumes.Add(new Volume { Name = "vtheirs", MountPath = "/data", StorageBytes = 400 * Gb });
        _db.Apps.Add(app);
        await _db.SaveChangesAsync();

        (await Service().DiskUsageAsync(_workspace, default)).MeasuredBytes.Should().Be(1 * Gb);
    }

    [Fact]
    public async Task A_workspace_over_its_disk_limit_cannot_add_an_app()
    {
        GivenPlan(maxDiskGb: 2);
        GivenDatabase(3 * Gb);

        var check = await Service().CanAddAppAsync(_workspace, null, null, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("disk");
    }

    [Fact]
    public async Task A_workspace_over_its_disk_limit_cannot_add_a_database_either()
    {
        // The limit is about the disk, not about which screen someone is on.
        GivenPlan(maxDiskGb: 2);
        GivenDatabase(3 * Gb);

        var check = await Service().CanAddServiceAsync(_workspace, null, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("disk");
    }

    [Fact]
    public async Task A_workspace_within_its_limit_is_unaffected()
    {
        GivenPlan(maxDiskGb: 10);
        GivenDatabase(1 * Gb);

        (await Service().CanAddAppAsync(_workspace, null, null, default)).Allowed.Should().BeTrue();
        (await Service().CanAddServiceAsync(_workspace, null, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_with_no_disk_limit_never_refuses_on_disk()
    {
        // 0 means unlimited here as it does for every other field, and every existing plan has 0.
        GivenPlan(maxDiskGb: 0);
        GivenDatabase(500 * Gb);

        (await Service().CanAddAppAsync(_workspace, null, null, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task Nothing_measured_yet_does_not_block_anybody()
    {
        // Refusing on a measurement nobody took would make the platform unusable for a reason
        // nobody could see.
        GivenPlan(maxDiskGb: 1);
        GivenAppWithVolume(null);
        GivenDatabase(null);

        (await Service().CanAddAppAsync(_workspace, null, null, default)).Allowed.Should().BeTrue();
    }
}
