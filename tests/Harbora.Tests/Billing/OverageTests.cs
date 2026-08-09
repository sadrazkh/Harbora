using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// Whether a plan's cap is a wall or a price line.
///
/// <para>
/// The decision belongs on the plan, not on the platform: a free tier's cap is the whole product,
/// and a pay-as-you-go tier's cap is a figure the customer is allowed to buy past. One flag decides
/// which, and it is <c>false</c> unless somebody has said otherwise — an unset flag reading as
/// "allowed" is the exact shape this branch exists to remove.
/// </para>
///
/// <para>
/// <b>What the tenant pays for the excess is the ordinary meter</b>, not a surcharge: an application
/// past the cap is charged its instance size's hourly rate like every other application, and a
/// volume past the cap is charged the plan's gibibyte-hour. <c>Plan.OverageCpuCoreHourMinor</c> and
/// its two neighbours are read by nothing on this branch. That is why the sale is conditional on the
/// meter running at all — see
/// <see cref="A_plan_sells_nothing_past_its_caps_while_billing_is_switched_off"/>.
/// </para>
/// </summary>
public class OverageTests : IDisposable
{
    private const long Mb = 1024L * 1024;
    private const long Gb = 1024L * Mb;

    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("overage-" + Guid.CreateVersion7()).Options);

    private readonly Guid _workspace = Guid.CreateVersion7();
    private readonly Guid _planId = Guid.CreateVersion7();

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// A workspace on a plan that either walls at its caps or sells past them. Every cap defaults to
    /// 0 — unlimited — so each test names only the one it is about and nothing else can bind first.
    /// </summary>
    private void GivenPlan(
        bool sellsOverage,
        bool suspended = false,
        int maxApps = 0,
        int maxServices = 0,
        long maxMemoryBytes = 0,
        double maxCpuCores = 0,
        long maxDiskBytes = 0,
        string allowedSizeKeys = "")
    {
        _db.Plans.Add(new Plan
        {
            Id = _planId,
            Name = "Starter",
            AllowsOverage = sellsOverage,
            MaxApps = maxApps,
            MaxServices = maxServices,
            MaxMemoryBytes = maxMemoryBytes,
            MaxCpuCores = maxCpuCores,
            MaxDiskBytes = maxDiskBytes,
            AllowedSizeKeys = allowedSizeKeys,
            IsEnabled = true
        });

        _db.Workspaces.Add(new Workspace
        {
            Id = _workspace, Name = "Acme", Slug = "acme", PlanId = _planId, IsSuspended = suspended
        });

        _db.InstanceSizes.AddRange(
            new InstanceSize
            {
                Id = Guid.CreateVersion7(), Key = "small", Name = "Small",
                CpuCores = 0.25, MemoryBytes = 256 * Mb, IsEnabled = true, SortOrder = 1
            },
            new InstanceSize
            {
                Id = Guid.CreateVersion7(), Key = "large", Name = "Large",
                CpuCores = 2, MemoryBytes = 2048 * Mb, IsEnabled = true, SortOrder = 9
            });

        _db.SaveChanges();
    }

    private void GivenApp(long memoryBytes = 0, double cpuCores = 0)
    {
        _db.Apps.Add(new App
        {
            Id = Guid.CreateVersion7(), WorkspaceId = _workspace, ServerId = Guid.CreateVersion7(),
            Name = "api", Slug = "api-" + Guid.NewGuid().ToString("n")[..6],
            MemoryLimitBytes = memoryBytes, CpuLimit = cpuCores
        });
        _db.SaveChanges();
    }

    private void GivenDatabase(long? storageBytes = null)
    {
        _db.ManagedServices.Add(new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = _workspace, ServerId = Guid.CreateVersion7(),
            Name = "shop", Type = ManagedServiceType.PostgreSql,
            ContainerName = "harbora-svc-shop-" + Guid.NewGuid().ToString("n")[..6],
            StorageBytes = storageBytes
        });
        _db.SaveChanges();
    }

    /// <summary>
    /// The quota service as the container builds it. <paramref name="billingEnabled"/> is the
    /// platform switch every other part of this feature already reads.
    /// </summary>
    private QuotaService Quota(bool billingEnabled = true) =>
        new(_db, Options.Create(new BillingOptions { Enabled = billingEnabled }));

    [Fact]
    public async Task A_plan_that_does_not_sell_overage_still_refuses_past_its_cap()
    {
        // Today's behaviour, kept. A free plan must stay a wall.
        GivenPlan(sellsOverage: false, maxApps: 1);
        GivenApp();

        var check = await Quota().CanAddAppAsync(_workspace, "small", null, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("App limit reached",
            "the refusal a tenant on a capped plan already sees must not change wording underneath them");
    }

    [Fact]
    public async Task A_plan_that_sells_overage_lets_the_tenant_past_its_cap()
    {
        GivenPlan(sellsOverage: true, maxApps: 1);
        GivenApp();

        var check = await Quota().CanAddAppAsync(_workspace, "small", null, default);

        check.Allowed.Should().BeTrue("the plan sells the excess; the tick charges the new app by the hour");
    }

    [Fact]
    public void A_plan_nobody_has_said_anything_about_does_not_sell_overage()
    {
        // The flag decides whether a customer can run up a bill past the figure they were sold, so
        // the answer for a plan nobody has touched has to be no. A default of true would sell
        // capacity on every plan that existed before this column did, silently, on upgrade.
        new Plan().AllowsOverage.Should().BeFalse();
    }

    [Fact]
    public async Task Overage_does_not_let_a_suspended_workspace_past()
    {
        // Selling capacity past a cap is not the same as selling it to somebody who is not paying.
        GivenPlan(sellsOverage: true, suspended: true, maxApps: 1);
        GivenApp();

        var check = await Quota().CanAddAppAsync(_workspace, "small", null, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("suspended");
    }

    [Fact]
    public async Task A_plan_that_sells_overage_lets_a_database_past_its_cap_too()
    {
        // The brief named only the application path. A plan that sells capacity past its application
        // cap and walls at its database cap is not a plan anybody chose: the caps come in one list on
        // one screen, and the flag sits above all of them.
        //
        // Every cap this method checks is breached at once, deliberately: the application path proves
        // each cap separately below, and what is in question here is whether the flag reaches all
        // four of this method's copies of them or only the first one somebody remembered.
        GivenPlan(sellsOverage: true,
            maxServices: 1, maxMemoryBytes: 256 * Mb, maxCpuCores: 0.5, maxDiskBytes: 1 * Gb);
        GivenApp(memoryBytes: 400 * Mb, cpuCores: 1);
        GivenDatabase(storageBytes: 3 * Gb);

        var check = await Quota().CanAddServiceAsync(_workspace, "small", default);

        check.Allowed.Should().BeTrue(
            "nothing here is a cap this plan walls at, and one of them refused: " + check.Reason);
    }

    [Fact]
    public async Task A_plan_that_does_not_sell_overage_still_refuses_a_database_past_its_cap()
    {
        GivenPlan(sellsOverage: false, maxServices: 1);
        GivenDatabase();

        var check = await Quota().CanAddServiceAsync(_workspace, "small", default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("Service limit reached");
    }

    [Fact]
    public async Task A_plan_that_sells_overage_lets_the_tenant_past_its_memory_cap()
    {
        GivenPlan(sellsOverage: true, maxMemoryBytes: 512 * Mb);
        GivenApp(memoryBytes: 400 * Mb);

        (await Quota().CanAddAppAsync(_workspace, "large", null, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_that_sells_overage_lets_the_tenant_past_its_cpu_cap()
    {
        // Memory deliberately unlimited, so CPU is the only cap that can bind and this cannot pass
        // because some other check was lifted.
        GivenPlan(sellsOverage: true, maxCpuCores: 2);
        GivenApp(cpuCores: 1.5);

        (await Quota().CanAddAppAsync(_workspace, "large", null, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_that_sells_overage_lets_the_tenant_past_its_disk_cap()
    {
        GivenPlan(sellsOverage: true, maxDiskBytes: 2 * Gb);
        GivenDatabase(storageBytes: 3 * Gb);

        (await Quota().CanAddAppAsync(_workspace, "small", null, default)).Allowed.Should().BeTrue();
    }

    [Fact]
    public async Task A_plan_that_sells_overage_still_refuses_an_instance_size_it_does_not_offer()
    {
        // A cap is a quantity and an allowed-size list is an entitlement, and only the first one is
        // for sale. Reading the flag as "ignore everything the plan says" would put a tenant on a
        // tier the provider may not have the hardware for, at a size nobody offered them.
        GivenPlan(sellsOverage: true, allowedSizeKeys: "small");

        var app = await Quota().CanAddAppAsync(_workspace, "large", null, default);
        app.Allowed.Should().BeFalse();
        app.Reason.Should().Contain("large");

        // Asserted on both paths in one test because they are one rule written twice: guarding only
        // the application copy would leave the database copy selling a size the plan does not offer.
        var database = await Quota().CanAddServiceAsync(_workspace, "large", default);
        database.Allowed.Should().BeFalse();
        database.Reason.Should().Contain("large");
    }

    [Fact]
    public async Task A_plan_sells_nothing_past_its_caps_while_billing_is_switched_off()
    {
        // The only thing that makes selling past a cap honest is the meter that charges for it, and
        // `Billing:Enabled` is false on every install that has not opted in — BillingTick returns
        // without charging anybody. Lifting the cap there would hand out capacity past a published
        // limit for nothing, on the shipped default, while this method reported success.
        GivenPlan(sellsOverage: true, maxApps: 1);
        GivenApp();

        var check = await Quota(billingEnabled: false).CanAddAppAsync(_workspace, "small", null, default);

        check.Allowed.Should().BeFalse();
        check.Reason.Should().Contain("App limit reached");
    }

    [Fact]
    public async Task A_tenant_the_plan_sells_capacity_to_is_still_reported_as_over_its_limits()
    {
        // Allowed past is not "inside". The operator's list of who a limit is biting is read from the
        // same usage figures, and a workspace hidden from it because its plan sells overage is a
        // workspace nobody can see running away with the host. The flag decides what the create
        // button does, and nothing else.
        GivenPlan(sellsOverage: true, maxApps: 1);
        GivenApp();
        GivenApp();

        var usage = await Quota().GetUsageAsync(_workspace, default);

        usage.MaxApps.Should().Be(1, "the cap is still what the customer was sold");
        PlanOverage.For(usage).Should().ContainSingle(b => b.Resource == PlanResource.Apps);
    }
}
