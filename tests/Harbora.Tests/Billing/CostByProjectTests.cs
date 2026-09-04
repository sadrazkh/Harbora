using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// <c>WalletService.BreakdownByProjectAsync</c> — the same rows <c>BreakdownAsync</c> already returns,
/// sorted into the project and environment each resource sits in today, so "what does staging cost us"
/// has an answer without a second copy of the money arithmetic.
/// </summary>
public class CostByProjectTests
{
    /// <summary>The day every breakdown here is taken over — mirrors <c>WalletServiceTests.Day</c>.</summary>
    private static readonly DateTimeOffset Day = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    private static Guid SeedProject(BillingContext db, Guid workspaceId, string name)
    {
        var project = new Harbora.Domain.Projects.Project
        {
            WorkspaceId = workspaceId, Name = name, Slug = Slugify(name)
        };
        db.Projects.Add(project);
        return project.Id;
    }

    private static Guid SeedEnvironment(BillingContext db, Guid workspaceId, Guid projectId, string name)
    {
        var environment = new Harbora.Domain.Projects.Environment
        {
            WorkspaceId = workspaceId, ProjectId = projectId, Name = name, Slug = Slugify(name)
        };
        db.Environments.Add(environment);
        return environment.Id;
    }

    private static Guid SeedApp(BillingContext db, Guid workspaceId, Guid environmentId, string name)
    {
        var app = new App
        {
            WorkspaceId = workspaceId, EnvironmentId = environmentId, Name = name, Slug = Slugify(name)
        };
        db.Apps.Add(app);
        return app.Id;
    }

    private static string Slugify(string name) => name.ToLowerInvariant().Replace(" ", "-");

    // --- the whole point: the groups still add up to the workspace total --------------------

    [Fact]
    public async Task Groups_sum_to_exactly_the_workspace_total_the_ungrouped_bill_reports()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Storefront");
        var staging = SeedEnvironment(db, ws, project, "staging");
        var production = SeedEnvironment(db, ws, project, "production");
        var api = SeedApp(db, ws, production, "api");
        var worker = SeedApp(db, ws, staging, "worker");

        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: api, name: "api"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -400, resourceId: worker, name: "worker"));
        // No resource at all — the plan minimum — so it cannot be attributed to any project.
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -4_000, LedgerKind.PlanMinimumTopUp, BilledResourceType.PlanBase,
            resourceId: null, name: "Starter", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var wallets = WalletHarness.Wallets(db);
        var flat = await wallets.BreakdownAsync(ws, Day, Day.AddDays(1), default);
        var groups = await wallets.BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        var workspaceTotal = flat.Sum(c => c.TotalMinor);
        groups.Sum(g => g.TotalMinor).Should().Be(workspaceTotal);
        groups.Sum(g => g.Environments.Sum(e => e.TotalMinor)).Should().Be(workspaceTotal);
        groups.SelectMany(g => g.Environments).SelectMany(e => e.Costs).Sum(c => c.TotalMinor)
            .Should().Be(workspaceTotal);
    }

    [Fact]
    public async Task Every_groups_resource_rows_are_the_exact_figures_the_ungrouped_bill_reports()
    {
        // Not merely equal totals — the identical ResourceCost values, partitioned rather than
        // recomputed, so a group's row can never quietly drift from what BreakdownAsync says.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Storefront");
        var environment = SeedEnvironment(db, ws, project, "production");
        var api = SeedApp(db, ws, environment, "api");
        for (var h = 0; h < 3; h++)
            db.BillingLedger.Add(WalletHarness.Line(ws, Day.AddHours(h), -1_000, resourceId: api, name: "api"));
        await db.SaveChangesAsync();

        var wallets = WalletHarness.Wallets(db);
        var flat = await wallets.BreakdownAsync(ws, Day, Day.AddDays(1), default);
        var groups = await wallets.BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        var grouped = groups.Single().Environments.Single().Costs.Single();
        grouped.Should().Be(flat.Single(), "the same record, not a second one that happens to agree today");
    }

    [Fact]
    public async Task No_charges_this_period_is_an_empty_list_of_groups_not_a_fabricated_zero_group()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        groups.Should().BeEmpty();
    }

    // --- one project, split further by environment --------------------------------------------

    [Fact]
    public async Task One_project_splits_into_its_own_environments()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Storefront");
        var staging = SeedEnvironment(db, ws, project, "staging");
        var production = SeedEnvironment(db, ws, project, "production");
        var api = SeedApp(db, ws, production, "api");
        var worker = SeedApp(db, ws, staging, "worker");
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -3_000, resourceId: api, name: "api"));
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -700, resourceId: worker, name: "worker"));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        var storefront = groups.Should().ContainSingle(g => g.ProjectId == project).Subject;
        storefront.Environments.Select(e => e.EnvironmentName).Should().BeEquivalentTo(["staging", "production"]);
        storefront.Environments.Single(e => e.EnvironmentId == production).TotalMinor.Should().Be(-3_000);
        storefront.Environments.Single(e => e.EnvironmentId == staging).TotalMinor.Should().Be(-700);
    }

    [Fact]
    public async Task A_databases_disk_line_resolves_to_the_same_place_as_the_database_itself()
    {
        // ServiceVolume deliberately reuses the ManagedService's own id (see BilledResourceType's own
        // remarks) — the one intentional id collision this report has to get right rather than split
        // into two places by accident.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Data");
        var environment = SeedEnvironment(db, ws, project, "production");
        var service = new ManagedService
        {
            WorkspaceId = ws, EnvironmentId = environment, Name = "orders-db",
            ContainerName = "harbora-svc-orders-db", VolumeName = "harbora-svc-orders-db-data"
        };
        db.ManagedServices.Add(service);
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -1_000, type: BilledResourceType.Service, resourceId: service.Id, name: "orders-db"));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -200, type: BilledResourceType.ServiceVolume, resourceId: service.Id,
            name: "orders-db", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        var group = groups.Should().ContainSingle(g => g.ProjectId == project).Subject;
        group.TotalMinor.Should().Be(-1_200);
        group.Environments.Single().Costs.Should().HaveCount(2);
    }

    // --- attribution follows where a resource is placed NOW ------------------------------------

    [Fact]
    public async Task Spend_follows_a_resource_to_wherever_it_is_placed_now_not_where_it_was_charged()
    {
        // The decision this feature has to make and defend: an hour is attributed by the resource's
        // CURRENT project/environment, because the ledger line itself never recorded either at charge
        // time (see BreakdownAsync's own remarks on why ResourceName is copied rather than joined —
        // project and environment are not copied at all). Moving a workload moves its whole history
        // with it in this report.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var projectA = SeedProject(db, ws, "Project A");
        var projectB = SeedProject(db, ws, "Project B");
        var staging = SeedEnvironment(db, ws, projectA, "staging");
        var production = SeedEnvironment(db, ws, projectB, "production");
        var appId = SeedApp(db, ws, staging, "worker");
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: appId, name: "worker"));
        await db.SaveChangesAsync();

        // Promoted to project B's production environment after the hour was already charged.
        var app = await db.Apps.IgnoreQueryFilters().SingleAsync(a => a.Id == appId);
        app.EnvironmentId = production;
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        groups.Should().ContainSingle(g => g.ProjectId == projectB)
            .Which.Environments.Should().ContainSingle(e => e.EnvironmentId == production)
            .Which.Costs.Should().ContainSingle(c => c.Name == "worker");
        groups.Should().NotContain(g => g.ProjectId == projectA,
            "the app's whole history follows it to where it is placed today");
    }

    // --- Unassigned: visible and named, never dropped -------------------------------------------

    [Fact]
    public async Task A_deleted_apps_spend_lands_in_a_named_unassigned_group_rather_than_vanishing()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var app = new App { WorkspaceId = ws, Name = "gone", Slug = "gone" };
        db.Apps.Add(app);
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: app.Id, name: "gone"));
        await db.SaveChangesAsync();

        db.Apps.Remove(await db.Apps.IgnoreQueryFilters().SingleAsync(a => a.Id == app.Id));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        var unassigned = groups.Should().ContainSingle().Subject;
        unassigned.IsUnassigned.Should().BeTrue();
        unassigned.ProjectId.Should().BeNull();
        unassigned.TotalMinor.Should().Be(-1_000, "the money is still on the report, just not attributed to a project");
    }

    [Fact]
    public async Task A_mailbox_charge_lands_in_unassigned_because_mail_has_no_project_of_its_own()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Storefront");
        var environment = SeedEnvironment(db, ws, project, "production");
        var api = SeedApp(db, ws, environment, "api");
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: api, name: "api"));
        db.BillingLedger.Add(WalletHarness.Line(
            ws, Day, -50, type: BilledResourceType.Mailbox, resourceId: Guid.NewGuid(),
            name: "support@acme.example", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        groups.Should().Contain(g => g.ProjectId == project && g.TotalMinor == -1_000);
        var unassigned = groups.Should().ContainSingle(g => g.IsUnassigned).Subject;
        unassigned.TotalMinor.Should().Be(-50);
        groups.Sum(g => g.TotalMinor).Should().Be(-1_050, "nothing was dropped off the total");
    }

    // --- tenancy, both directions ----------------------------------------------------------------

    [Fact]
    public async Task One_workspaces_groups_never_carry_another_workspaces_spend()
    {
        await using var db = WalletHarness.SystemContext();
        var acme = WalletHarness.SeedWorkspace(db);
        var other = WalletHarness.SeedWorkspace(db);
        var acmeProject = SeedProject(db, acme, "Acme project");
        var acmeEnvironment = SeedEnvironment(db, acme, acmeProject, "production");
        var acmeApp = SeedApp(db, acme, acmeEnvironment, "acme-api");
        var otherProject = SeedProject(db, other, "Other project");
        var otherEnvironment = SeedEnvironment(db, other, otherProject, "production");
        var otherApp = SeedApp(db, other, otherEnvironment, "other-api");

        db.BillingLedger.Add(WalletHarness.Line(acme, Day, -1_000, resourceId: acmeApp, name: "acme-api"));
        db.BillingLedger.Add(WalletHarness.Line(other, Day, -9_000, resourceId: otherApp, name: "other-api"));
        await db.SaveChangesAsync();

        var wallets = WalletHarness.Wallets(db);
        var acmeGroups = await wallets.BreakdownByProjectAsync(acme, Day, Day.AddDays(1), includeForecast: false, default);
        var otherGroups = await wallets.BreakdownByProjectAsync(other, Day, Day.AddDays(1), includeForecast: false, default);

        // The right workspace's rows present —
        acmeGroups.Should().ContainSingle(g => g.ProjectId == acmeProject && g.TotalMinor == -1_000);
        otherGroups.Should().ContainSingle(g => g.ProjectId == otherProject && g.TotalMinor == -9_000);
        // — and the other workspace's absent, in both directions.
        acmeGroups.Should().NotContain(g => g.ProjectId == otherProject);
        otherGroups.Should().NotContain(g => g.ProjectId == acmeProject);
    }

    // --- three states: data, empty, not-measured — per group, reusing CostForecast/BurnRate -----

    [Fact]
    public async Task Each_groups_forecast_state_is_its_own_rather_than_the_workspaces()
    {
        // WalletHarness.Now is 2026-08-09 20:30 UTC, so 19:00 that day is the newest hour the tick
        // could possibly have priced by now — see CostForecastTests' own remarks for the same anchor.
        var lastEndedHour = new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);

        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 50_000);

        var steadyProject = SeedProject(db, ws, "Steady");
        var steadyApp = SeedApp(db, ws, SeedEnvironment(db, ws, steadyProject, "production"), "steady-api");
        var freshProject = SeedProject(db, ws, "Fresh");
        var freshApp = SeedApp(db, ws, SeedEnvironment(db, ws, freshProject, "production"), "fresh-api");
        var idleProject = SeedProject(db, ws, "Idle");
        var idleApp = SeedApp(db, ws, SeedEnvironment(db, ws, idleProject, "production"), "idle-api");

        // Steady: a full day of its own history, ending on the newest hour the tick could have
        // priced — real "data": a projected figure.
        for (var h = 0; h < 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, lastEndedHour.AddHours(-h), -500, resourceId: steadyApp, name: "steady-api"));

        // Fresh: five hours of its OWN history — below the bar even though the workspace as a whole
        // clears it easily through Steady's lines. "Not measured": no projection is shown.
        for (var h = 0; h < 5; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, lastEndedHour.AddHours(-h), -500, resourceId: freshApp, name: "fresh-api"));

        // Idle: a full day of history, but the newest ended hour itself has no charge for it — a
        // real, computed zero. "Empty": nothing currently costing money, not the same fact as Fresh's
        // unmeasured one.
        for (var h = 1; h <= 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, lastEndedHour.AddHours(-h), -300, resourceId: idleApp, name: "idle-api"));

        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db).BreakdownByProjectAsync(
            ws, lastEndedHour.AddHours(-30), lastEndedHour.AddHours(3), includeForecast: true, default);

        var steady = groups.Single(g => g.ProjectId == steadyProject).Forecast!;
        steady.HasEnoughHistory.Should().BeTrue();
        steady.BurnRateHourlyMinor.Should().Be(500, "\"data\": a real, non-zero projection");

        var fresh = groups.Single(g => g.ProjectId == freshProject).Forecast!;
        fresh.HasEnoughHistory.Should().BeFalse(
            "\"not measured\": this project's own history is 5 hours, not the workspace's 24+");

        var idle = groups.Single(g => g.ProjectId == idleProject).Forecast!;
        idle.HasEnoughHistory.Should().BeTrue();
        idle.BurnRateHourlyMinor.Should().Be(
            0, "\"empty\": a computed zero is a different fact from Fresh's unmeasured one");
    }

    [Fact]
    public async Task No_forecast_is_computed_for_a_closed_period_for_any_group()
    {
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db);
        var project = SeedProject(db, ws, "Closed");
        var environment = SeedEnvironment(db, ws, project, "production");
        var app = SeedApp(db, ws, environment, "api");
        db.BillingLedger.Add(WalletHarness.Line(ws, Day, -1_000, resourceId: app, name: "api"));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db)
            .BreakdownByProjectAsync(ws, Day, Day.AddDays(1), includeForecast: false, default);

        groups.Should().OnlyContain(g => g.Forecast == null);
        groups.SelectMany(g => g.Environments).Should().OnlyContain(e => e.Forecast == null);
    }

    [Fact]
    public async Task The_unassigned_groups_forecast_is_the_same_burn_rate_arithmetic_too()
    {
        var lastEndedHour = new DateTimeOffset(2026, 8, 9, 19, 0, 0, TimeSpan.Zero);
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(db, balanceMinor: 50_000);

        // Plan-minimum lines, unattributable to any project, steady enough to earn a projection.
        for (var h = 0; h < 24; h++)
            db.BillingLedger.Add(WalletHarness.Line(
                ws, lastEndedHour.AddHours(-h), -200, LedgerKind.PlanMinimumTopUp, BilledResourceType.PlanBase,
                resourceId: null, name: "Starter", state: BilledRunState.NotApplicable));
        await db.SaveChangesAsync();

        var groups = await WalletHarness.Wallets(db).BreakdownByProjectAsync(
            ws, lastEndedHour.AddHours(-30), lastEndedHour.AddHours(3), includeForecast: true, default);

        var unassigned = groups.Should().ContainSingle(g => g.IsUnassigned).Subject;
        unassigned.Forecast.Should().NotBeNull();
        unassigned.Forecast!.HasEnoughHistory.Should().BeTrue();
        unassigned.Forecast!.BurnRateHourlyMinor.Should().Be(200);
    }
}
