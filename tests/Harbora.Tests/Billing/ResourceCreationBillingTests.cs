using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class ResourceCreationBillingTests
{
    private static readonly DateTimeOffset DuringHour =
        new(2026, 8, 9, 19, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("src/Harbora.Web/Controllers/AppsController.cs")]
    [InlineData("src/Harbora.Web/Controllers/DatabasesController.cs")]
    [InlineData("src/Harbora.Infrastructure/Templates/TemplateDeploymentService.cs")]
    [InlineData("src/Harbora.Infrastructure/Projects/EnvironmentCloner.cs")]
    [InlineData("src/Harbora.Infrastructure/Projects/PreviewEnvironmentService.cs")]
    public void Every_resource_creation_entry_point_uses_the_same_prepaid_save(string relativePath)
    {
        var root = Path.GetFullPath(Path.Combine(TestPaths.WebRoot, "..", ".."));
        File.ReadAllText(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)))
            .Should().Contain("creationBilling.SaveAsync",
                $"{relativePath} creates billable resources and must not persist them around the money gate");
    }

    [Fact]
    public async Task An_empty_wallet_refuses_creation_and_persists_nothing()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 0, rate: 125);
        var app = App(workspaceId, size.Key);
        db.Apps.Add(app);

        var act = () => Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);

        var error = await act.Should().ThrowAsync<CreationPaymentRequiredException>();
        error.Which.Message.Should().Contain("no resource was created");
        error.Which.ReasonFa.Should().Contain("هیچ منبعی ساخته نشد");

        db.ChangeTracker.Clear();
        (await db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.BillingLedger.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(0);
    }

    [Fact]
    public async Task Creation_and_its_first_hour_charge_commit_together()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 500, rate: 125);
        var app = App(workspaceId, size.Key);
        db.Apps.Add(app);

        var charged = await Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);

        charged.Should().Be(125);
        db.ChangeTracker.Clear();
        (await db.Apps.IgnoreQueryFilters().SingleAsync()).Id.Should().Be(app.Id);
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(375);
        var line = await db.BillingLedger.IgnoreQueryFilters().SingleAsync();
        line.AmountMinor.Should().Be(-125);
        line.BillingHour.Should().Be(new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero));
        line.ResourceType.Should().Be(BilledResourceType.App);
        line.ResourceId.Should().Be(app.Id);
        line.RunState.Should().Be(BilledRunState.Running);
    }

    [Fact]
    public async Task A_mail_resource_uses_its_direct_price_and_never_needs_an_instance_size()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, _) = Seed(db, balance: 500, rate: 125);
        var id = Guid.CreateVersion7();

        var charged = await Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.MailDomain, id, "example.com", null, 80)], default);

        charged.Should().Be(80);
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(420);
        var line = await db.BillingLedger.IgnoreQueryFilters().SingleAsync();
        line.ResourceType.Should().Be(BilledResourceType.MailDomain);
        line.ResourceId.Should().Be(id);
        line.RatePerHourMinor.Should().Be(80);
    }

    [Fact]
    public async Task An_unpriced_mail_resource_is_refused_instead_of_becoming_free()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, _) = Seed(db, balance: 500, rate: 125);

        var act = () => Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.Mailbox, Guid.CreateVersion7(), "hello@example.com", null)], default);

        await act.Should().ThrowAsync<CreationPaymentRequiredException>();
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(500);
    }

    [Fact]
    public async Task A_stack_is_refused_as_one_unit_when_its_combined_first_hour_is_not_funded()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 200, rate: 125);
        var app = App(workspaceId, size.Key);
        var service = new ManagedService
        {
            WorkspaceId = workspaceId,
            Name = "database",
            ContainerName = "database",
            VolumeName = "database-data",
            InstanceSizeKey = size.Key
        };
        db.AddRange(app, service);

        var act = () => Billing(db).SaveAsync(workspaceId,
            [
                new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey),
                new(BilledResourceType.Service, service.Id, service.Name, service.InstanceSizeKey)
            ], default);

        await act.Should().ThrowAsync<CreationPaymentRequiredException>();
        db.ChangeTracker.Clear();
        (await db.Apps.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.ManagedServices.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(200);
    }

    [Fact]
    public async Task The_hourly_tick_does_not_charge_the_prepaid_first_hour_twice()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 500, rate: 125);
        var app = App(workspaceId, size.Key);
        app.Status = AppStatus.Running;
        db.Apps.Add(app);

        await Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);
        db.ChangeTracker.Clear();

        await Harness.Tick(db).ChargeHourAsync(
            new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero), default);

        db.ChangeTracker.Clear();
        (await db.BillingLedger.IgnoreQueryFilters()
            .Where(l => l.ResourceType == BilledResourceType.App && l.ResourceId == app.Id)
            .CountAsync()).Should().Be(1);
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(375);
    }

    [Fact]
    public async Task Paying_exactly_one_hour_lets_only_that_prepaid_resource_start_at_zero()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 125, rate: 125);
        var app = App(workspaceId, size.Key);
        db.Apps.Add(app);

        await Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);
        db.ChangeTracker.Clear();

        var gate = new BillingGate(
            db,
            Options.Create(new BillingOptions { Enabled = true }),
            new FixedClock(DuringHour));

        (await gate.CanStartAsync(workspaceId, BilledResourceType.App, app.Id, default))
            .Allowed.Should().BeTrue("this app has already paid for the current hour");
        (await gate.CanStartAsync(workspaceId, BilledResourceType.App, Guid.CreateVersion7(), default))
            .Allowed.Should().BeFalse("another app cannot spend the first app's prepaid hour");
        (await gate.CanStartAsync(workspaceId, default))
            .Allowed.Should().BeFalse("a workspace-level caller has not identified a prepaid resource");
    }

    [Fact]
    public async Task An_unpriced_size_is_refused_instead_of_becoming_free()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 500, rate: null);
        var app = App(workspaceId, size.Key);
        db.Apps.Add(app);

        var act = () => Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);

        (await act.Should().ThrowAsync<CreationPaymentRequiredException>())
            .Which.Message.Should().Contain("no running hourly price");
    }

    [Fact]
    public async Task Creation_that_would_cross_the_monthly_spend_limit_persists_nothing()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 500, rate: 125);
        (await db.Workspaces.SingleAsync(w => w.Id == workspaceId)).MonthlySpendLimitMinor = 200;
        db.BillingLedger.Add(new BillingLedgerEntry
        {
            WorkspaceId = workspaceId,
            BillingHour = DuringHour.AddHours(-1),
            Kind = LedgerKind.Charge,
            AmountMinor = -100,
            ResourceType = BilledResourceType.App,
            ResourceId = Guid.NewGuid()
        });
        await db.SaveChangesAsync();
        var app = App(workspaceId, size.Key);
        db.Apps.Add(app);

        var act = () => Billing(db).SaveAsync(workspaceId,
            [new(BilledResourceType.App, app.Id, app.Name, app.InstanceSizeKey)], default);

        (await act.Should().ThrowAsync<CreationPaymentRequiredException>())
            .Which.Message.Should().Contain("monthly spend limit");
        db.ChangeTracker.Clear();
        (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.Id == app.Id)).Should().BeFalse();
        (await db.Wallets.IgnoreQueryFilters().SingleAsync()).BalanceMinor.Should().Be(500);
    }

    [Fact]
    public async Task Hourly_charge_that_would_cross_the_hard_limit_is_withheld_and_suspends_the_workspace()
    {
        await using var db = Harness.SystemContext();
        var (workspaceId, size) = Seed(db, balance: 500, rate: 125);
        var workspace = await db.Workspaces.SingleAsync(w => w.Id == workspaceId);
        workspace.MonthlySpendLimitMinor = 100;
        var app = App(workspaceId, size.Key);
        app.Status = AppStatus.Running;
        db.Apps.Add(app);
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(
            new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero), default);

        db.ChangeTracker.Clear();
        (await db.BillingLedger.IgnoreQueryFilters().AnyAsync()).Should().BeFalse();
        var after = await db.Workspaces.IgnoreQueryFilters().SingleAsync(w => w.Id == workspaceId);
        after.IsSuspended.Should().BeTrue();
        after.SuspendedReason.Should().Be(SuspensionReason.SpendLimit);
    }

    private static ResourceCreationBilling Billing(Harbora.Data.HarboraDbContext db) =>
        new(db, new FixedClock(DuringHour), Options.Create(new BillingOptions
        {
            Enabled = true,
            Currency = "IRR"
        }));

    private static (Guid WorkspaceId, InstanceSize Size) Seed(
        Harbora.Data.HarboraDbContext db, long balance, long? rate)
    {
        var plan = new Plan { Name = "Customer", BaseRatePerHourMinor = 0 };
        var workspace = new Workspace
        {
            Name = "Customer",
            Slug = Guid.NewGuid().ToString("N"),
            PlanId = plan.Id
        };
        var size = new InstanceSize
        {
            Key = "test-" + Guid.NewGuid().ToString("N"),
            Name = "Test",
            RunningRatePerHourMinor = rate,
            StoppedRatePerHourMinor = rate
        };
        db.AddRange(plan, workspace, size,
            new Wallet { WorkspaceId = workspace.Id, BalanceMinor = balance, Currency = "IRR" });
        db.SaveChanges();
        return (workspace.Id, size);
    }

    private static App App(Guid workspaceId, string sizeKey) => new()
    {
        WorkspaceId = workspaceId,
        Name = "app",
        Slug = Guid.NewGuid().ToString("N"),
        InstanceSizeKey = sizeKey,
        Status = AppStatus.Created
    };
}
