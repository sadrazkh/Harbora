using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Tenancy;
using Harbora.Web.Controllers;
using Harbora.Web.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// Whether an administrator can put a price on anything at all.
///
/// <para>
/// Every rate column on this branch arrived through a migration and was then reachable from
/// nowhere: <c>PlansController.CreatePlan</c>, <c>UpdatePlan</c>, <c>CreateSize</c> and
/// <c>UpdateSize</c> bind explicit scalar parameter lists rather than a model, and not one of those
/// lists named a rate. The columns therefore stayed unset on every install, and an unset rate is a
/// workload nobody is charged for while each hourly tick reports success — this project's signature
/// failure, arrived at through a parameter list.
/// </para>
///
/// <para>
/// <b>The round-trip assertions name each column, one line each, rather than looping over
/// reflection.</b> A loop would have to be driven by the same parameter list it is meant to be
/// checking, so a column left out of that list would be left out of the loop too and the suite
/// would go green over the exact gap it exists to find.
/// <see cref="An_eighth_rate_column_has_to_be_named_by_this_suite_before_it_can_ship"/> is the
/// other half: it is the only reflection here, and it fails when a rate column exists that the
/// lines below do not mention.
/// </para>
/// </summary>
public class RateAdminTests
{
    // ---- fixture -----------------------------------------------------------------------------

    private sealed class Quota : IQuotaService
    {
        // None of the four actions under test consults the quota service; throwing rather than
        // returning a polite empty reading means a change that starts consulting it says so here
        // instead of being absorbed.
        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct) =>
            throw new NotSupportedException("the rate actions do not read usage");

        public Task<QuotaCheck> CanAddAppAsync(
            Guid workspaceId, string? instanceSizeKey, Guid? excludeAppId, CancellationToken ct) =>
            throw new NotSupportedException("the rate actions do not check quota");

        public Task<QuotaCheck> CanAddServiceAsync(
            Guid workspaceId, string? instanceSizeKey, CancellationToken ct) =>
            throw new NotSupportedException("the rate actions do not check quota");
    }

    private sealed class Caller(Guid workspaceId, Guid userId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => "operator@example.test";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => workspaceId;
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<(string Action, string? TargetType, string? TargetId, string? Metadata)> Entries { get; } = [];

        public Task LogAsync(
            string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            CancellationToken ct = default)
        {
            Entries.Add((action, targetType, targetId, metadataJson));
            return Task.CompletedTask;
        }
    }

    private sealed class NullTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }

    private sealed record Fixture(HarboraDbContext Db, PlansController Controller, RecordingAudit Audit)
    {
        public string? Error => Controller.TempData["Error"] as string;
    }

    private static Fixture Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("rate-admin-" + Guid.CreateVersion7()).Options);

        var audit = new RecordingAudit();
        var controller = new PlansController(db, new Quota(), new Caller(Guid.CreateVersion7(), Guid.CreateVersion7()), audit)
        {
            ControllerContext = new ControllerContext
            {
                // CreatePlan and UpdatePlan refuse anybody who is not the provider before they read
                // a single field, so the principal is part of the fixture rather than an afterthought.
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Owner")], "test"))
                }
            }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new NullTempDataProvider());

        return new Fixture(db, controller, audit);
    }

    private static Plan Existing(HarboraDbContext db)
    {
        var plan = new Plan
        {
            Id = Guid.CreateVersion7(),
            Name = "Standard",
            NameFa = "استاندارد",
            MaxApps = 3,
            MaxServices = 2,
            MaxMemoryBytes = 2048L * 1024 * 1024,
            MaxCpuCores = 2,
            MaxDiskBytes = 40L * 1024 * 1024 * 1024,
            MonthlyPrice = 25m
        };
        db.Plans.Add(plan);
        db.SaveChanges();
        return plan;
    }

    private static InstanceSize ExistingSize(HarboraDbContext db)
    {
        var size = new InstanceSize
        {
            Id = Guid.CreateVersion7(),
            Key = "small",
            Name = "Small",
            NameFa = "کوچک",
            CpuCores = 1,
            MemoryBytes = 1024L * 1024 * 1024,
            DiskBytes = 20L * 1024 * 1024 * 1024,
            SortOrder = 3
        };
        db.InstanceSizes.Add(size);
        db.SaveChanges();
        return size;
    }

    // ---- the seven columns, one line each ------------------------------------------------------

    [Fact]
    public async Task A_plan_created_with_prices_comes_back_from_the_database_carrying_every_one_of_them()
    {
        var f = Build();

        await f.Controller.CreatePlan(
            "Standard", maxApps: 3, maxServices: 2, maxMemoryMb: 2048, maxCpu: 2, maxDiskGb: 40,
            maxMembers: 0, maxProjects: 0, maxEnvironments: 0, maxDomains: 0, maxVolumes: 0, maxBackupSchedules: 0,
            allowedSizeKeys: "small", monthlyPrice: 25m,
            baseRatePerHour: "1.50",
            diskGbHour: "0.25",
            allowsOverage: true, ct: default);

        var plan = await f.Db.Plans.SingleAsync();

        // Named one by one on purpose. A parameter list that silently omits one column is how this
        // whole gap was created, and a loop would be driven by that same list.
        plan.BaseRatePerHourMinor.Should().Be(150);
        plan.DiskGbHourMinor.Should().Be(25);

        // Asserted on the create path as well as the edit one, because they are two parameter
        // lists and two object initialisers. A mutation run found this exact hole: every rate
        // assertion here was duplicated across create and update, and the flag was only checked on
        // update — so CreatePlan could have dropped it and a new plan sold as "may burst past its
        // caps" would have been a wall from the moment it was offered, with nothing red.
        plan.AllowsOverage.Should().BeTrue();

        // MonthlyPrice is the pre-existing display figure on the plan card, not a rate. It is
        // asserted here so a refactor that "tidied" the two into one path is caught: a decimal
        // nobody charges and a long the tick spends are different things.
        plan.MonthlyPrice.Should().Be(25m);
    }

    [Fact]
    public async Task A_size_created_with_prices_comes_back_from_the_database_carrying_both_of_them()
    {
        var f = Build();

        await f.Controller.CreateSize(
            key: "xlarge", name: "X-Large", cpuCores: 8, memoryMb: 8192, diskGb: 160, sortOrder: 9,
            runningRate: "12.00", stoppedRate: "1.20", ct: default);

        var size = await f.Db.InstanceSizes.SingleAsync();

        size.RunningRatePerHourMinor.Should().Be(1200);
        size.StoppedRatePerHourMinor.Should().Be(120);
    }

    [Fact]
    public async Task An_eighth_rate_column_has_to_be_named_by_this_suite_before_it_can_ship()
    {
        // The only reflection in this file, and it looks the other way round from the round-trips
        // above: it does not check that the named columns work, it checks that no unnamed one
        // exists. A rate added to Plan or InstanceSize and left out of the parameter lists would
        // otherwise be uncovered in exactly the silence this whole task was written about.
        //
        // "long?" is the whole population: BillingRatesTests pins every rate as long?, and the
        // limit columns beside them (MaxMemoryBytes, DiskBytes…) are non-nullable long, so nothing
        // that is not a price is caught here.
        var found = new[] { typeof(Plan), typeof(InstanceSize) }
            .SelectMany(t => t.GetProperties()
                .Where(p => p.PropertyType == typeof(long?))
                .Select(p => $"{t.Name}.{p.Name}"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        found.Should().BeEquivalentTo(
        [
            "InstanceSize.RunningRatePerHourMinor",
            "InstanceSize.StoppedRatePerHourMinor",
            "Plan.BaseRatePerHourMinor",
            "Plan.DiskGbHourMinor",
        ], "every rate column has to be set by an admin form, so a new one must be added to the "
         + "round-trip assertions in this file at the same time as it is added to the entity");

        // Four, not the seven the migrations first created. Plan.OverageCpuCoreHourMinor and its
        // two neighbours were removed rather than put on this form: nothing ever read them, the
        // excess past a cap is charged at the ordinary meter, and a price box an operator can fill
        // in that nothing collects is worse than no box — they would set a burst rate, be charged
        // nothing extra for ever, and every tick would report success. Bringing them back means
        // wiring the tick first, and this assertion is where that argument has to be had.
        found.Should().NotContain(n => n.Contains("Overage", StringComparison.Ordinal));
    }

    // ---- editing -------------------------------------------------------------------------------

    [Fact]
    public async Task Editing_a_plan_writes_the_prices_that_were_typed()
    {
        var f = Build();
        var plan = Existing(f.Db);

        await f.Controller.UpdatePlan(
            plan.Id, "Standard", maxApps: 3, maxServices: 2, maxMemoryMb: 2048, maxCpu: 2,
            maxDiskGb: 40, maxMembers: 0, maxProjects: 0, maxEnvironments: 0,
            maxDomains: 0, maxVolumes: 0, maxBackupSchedules: 0,
            allowedSizeKeys: "small", monthlyPrice: 25m,
            baseRatePerHour: "9.99",
            diskGbHour: "5.55",
            allowsOverage: true, ct: default);

        var saved = await f.Db.Plans.SingleAsync();

        saved.BaseRatePerHourMinor.Should().Be(999);
        saved.DiskGbHourMinor.Should().Be(555);

        // Task 7 gave the plan this flag and no screen ever set it, so a plan's caps were walls on
        // every install however the operator meant them. It rides on this form because it is the
        // same defect as the rates: a column the admin panel cannot reach.
        saved.AllowsOverage.Should().BeTrue();
    }

    [Fact]
    public async Task Editing_a_size_writes_the_prices_that_were_typed()
    {
        var f = Build();
        var size = ExistingSize(f.Db);

        await f.Controller.UpdateSize(
            size.Id, "Small", cpuCores: 1, memoryMb: 1024, diskGb: 20, sortOrder: 3,
            runningRate: "3.00", stoppedRate: "0.30", ct: default);

        var saved = await f.Db.InstanceSizes.SingleAsync();

        saved.RunningRatePerHourMinor.Should().Be(300);
        saved.StoppedRatePerHourMinor.Should().Be(30);
    }

    // ---- null is not zero ----------------------------------------------------------------------

    [Fact]
    public async Task A_price_box_left_empty_leaves_the_plan_unpriced_rather_than_free()
    {
        var f = Build();

        await f.Controller.CreatePlan(
            "Standard", 3, 2, 2048, 2, 40, 0, 0, 0, 0, 0, 0, "small", 25m,
            baseRatePerHour: "", diskGbHour: "   ", allowsOverage: false, ct: default);

        var plan = await f.Db.Plans.SingleAsync();

        // The distinction the whole branch turns on. A form that wrote 0 for an empty box would
        // report a deliberately free plan where the truth is that nobody has priced it yet.
        // Whitespace counts as empty: a box an operator cleared usually keeps its space bar.
        plan.BaseRatePerHourMinor.Should().BeNull();
        plan.DiskGbHourMinor.Should().BeNull();
    }

    [Fact]
    public async Task A_price_box_left_empty_leaves_a_size_unpriced_rather_than_free()
    {
        var f = Build();

        await f.Controller.CreateSize("xlarge", "X-Large", 8, 8192, 160, 9,
            runningRate: null, stoppedRate: "", ct: default);

        var size = await f.Db.InstanceSizes.SingleAsync();

        size.RunningRatePerHourMinor.Should().BeNull();
        size.StoppedRatePerHourMinor.Should().BeNull();
    }

    [Fact]
    public async Task A_price_typed_as_zero_is_stored_as_free_rather_than_as_unpriced()
    {
        var f = Build();

        await f.Controller.CreatePlan(
            "Free tier", 1, 0, 256, 0.25, 5, 0, 0, 0, 0, 0, 0, "nano", 0m,
            baseRatePerHour: "0", diskGbHour: "0.00", allowsOverage: false, ct: default);

        var plan = await f.Db.Plans.SingleAsync();

        // A free tier is a legitimate thing to sell, and it has to be expressible as something
        // other than "not answered". Zero here is an answer.
        plan.BaseRatePerHourMinor.Should().Be(0);
        plan.DiskGbHourMinor.Should().Be(0);
    }

    [Fact]
    public async Task A_size_priced_at_zero_is_free_rather_than_unpriced()
    {
        var f = Build();

        await f.Controller.CreateSize("free", "Free", 0.25, 256, 5, 0,
            runningRate: "0", stoppedRate: "0", ct: default);

        var size = await f.Db.InstanceSizes.SingleAsync();

        size.RunningRatePerHourMinor.Should().Be(0);
        size.StoppedRatePerHourMinor.Should().Be(0);
    }

    [Fact]
    public async Task Clearing_a_price_that_was_set_puts_it_back_to_unpriced()
    {
        var f = Build();
        var plan = Existing(f.Db);
        plan.BaseRatePerHourMinor = 500;
        plan.DiskGbHourMinor = 25;
        await f.Db.SaveChangesAsync();

        await f.Controller.UpdatePlan(
            plan.Id, "Standard", 3, 2, 2048, 2, 40, 0, 0, 0, 0, 0, 0, "small", 25m,
            baseRatePerHour: "", diskGbHour: "", allowsOverage: false, ct: default);

        var saved = await f.Db.Plans.SingleAsync();

        // Withdrawing a price has to be possible, and it is not the same as setting it to zero:
        // one says "this is free", the other says "this needs pricing again".
        saved.BaseRatePerHourMinor.Should().BeNull();
        saved.DiskGbHourMinor.Should().BeNull();
    }

    [Fact]
    public async Task A_price_is_read_to_the_minor_unit_rather_than_rounded_to_the_major_one()
    {
        var f = Build();

        await f.Controller.CreateSize("small", "Small", 1, 1024, 20, 3,
            runningRate: "0.07", stoppedRate: "1234.56", ct: default);

        var size = await f.Db.InstanceSizes.SingleAsync();

        size.RunningRatePerHourMinor.Should().Be(7);
        size.StoppedRatePerHourMinor.Should().Be(123456);
    }

    // ---- refusals ------------------------------------------------------------------------------

    [Fact]
    public async Task A_negative_plan_price_is_refused_and_no_plan_is_created()
    {
        var f = Build();

        var result = await f.Controller.CreatePlan(
            "Standard", 3, 2, 2048, 2, 40, 0, 0, 0, 0, 0, 0, "small", 25m,
            baseRatePerHour: "-1.00", diskGbHour: null, allowsOverage: false, ct: default);

        result.Should().BeOfType<RedirectToActionResult>();
        (await f.Db.Plans.CountAsync()).Should().Be(0, "a negative rate is a machine that pays customers to run workloads");
        f.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task A_negative_size_price_is_refused_and_no_size_is_created()
    {
        var f = Build();

        await f.Controller.CreateSize("xlarge", "X-Large", 8, 8192, 160, 9,
            runningRate: "12.00", stoppedRate: "-0.01", ct: default);

        (await f.Db.InstanceSizes.CountAsync()).Should().Be(0);
        f.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData("not a price", null)]
    [InlineData(null, "not a price")]
    public async Task A_refused_price_leaves_every_other_field_of_the_plan_as_it_was(
        string? baseRate, string? diskRate)
    {
        var f = Build();
        var plan = Existing(f.Db);
        plan.BaseRatePerHourMinor = 500;
        await f.Db.SaveChangesAsync();

        await f.Controller.UpdatePlan(
            plan.Id, "Renamed", maxApps: 99, maxServices: 99, maxMemoryMb: 1, maxCpu: 99,
            maxDiskGb: 99, maxMembers: 0, maxProjects: 0, maxEnvironments: 0,
            maxDomains: 0, maxVolumes: 0, maxBackupSchedules: 0,
            allowedSizeKeys: "nano", monthlyPrice: 999m,
            baseRatePerHour: baseRate, diskGbHour: diskRate, allowsOverage: false, ct: default);

        var saved = await f.Db.Plans.AsNoTracking().SingleAsync();

        // The refusal is the whole action, not the price field on its own. Saving the caps and
        // dropping the price would leave a plan half-edited by a form that said it had refused.
        //
        // Both boxes get a row. Each is guarded by its own statement, and a mutation run showed
        // that covering only the first left the second free to fall through and save: the box an
        // operator got wrong decided whether the refusal was real.
        saved.Name.Should().Be("Standard");
        saved.MaxApps.Should().Be(3);
        saved.BaseRatePerHourMinor.Should().Be(500);
    }

    [Theory]
    [InlineData("not a price", null)]
    [InlineData(null, "not a price")]
    public async Task A_refused_price_leaves_every_other_field_of_the_size_as_it_was(
        string? running, string? stopped)
    {
        var f = Build();
        var size = ExistingSize(f.Db);
        size.RunningRatePerHourMinor = 500;
        await f.Db.SaveChangesAsync();

        await f.Controller.UpdateSize(size.Id, "Renamed", cpuCores: 99, memoryMb: 1, diskGb: 99,
            sortOrder: 99, runningRate: running, stoppedRate: stopped, ct: default);

        var saved = await f.Db.InstanceSizes.AsNoTracking().SingleAsync();

        saved.Name.Should().Be("Small");
        saved.CpuCores.Should().Be(1);
        saved.RunningRatePerHourMinor.Should().Be(500);
    }

    [Fact]
    public async Task A_price_that_is_not_a_figure_is_refused_and_the_message_names_the_box()
    {
        var f = Build();

        await f.Controller.CreateSize("xlarge", "X-Large", 8, 8192, 160, 9,
            runningRate: "twelve", stoppedRate: null, ct: default);

        (await f.Db.InstanceSizes.CountAsync()).Should().Be(0);

        // Four price boxes on one screen: "that is not a number" without saying which one sends an
        // operator to check all of them.
        f.Error.Should().Contain("Running", "the operator has to be told which box to look at");
    }

    [Fact]
    public async Task A_refusal_reaches_a_Persian_operator_in_Persian()
    {
        var f = Build();
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("fa");

            await f.Controller.CreateSize("xlarge", "X-Large", 8, 8192, 160, 9,
                runningRate: "twelve", stoppedRate: null, ct: default);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }

        // The panel is bilingual and this refusal is new, so it is written in both rather than
        // inheriting the English-only refusals that predate the rule.
        f.Error.Should().NotBeNull();
        f.Error.Should().NotContain("must be a figure");
    }

    // ---- the audit trail -----------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_plans_prices_is_written_to_the_audit_trail_with_the_figures()
    {
        var f = Build();
        var plan = Existing(f.Db);

        await f.Controller.UpdatePlan(
            plan.Id, "Standard", 3, 2, 2048, 2, 40, 0, 0, 0, 0, 0, 0, "small", 25m,
            baseRatePerHour: "1.50", diskGbHour: "0.25", allowsOverage: false, ct: default);

        var entry = f.Audit.Entries.Should().ContainSingle().Subject;

        // A price change is the most disputable thing an administrator does here, and a line saying
        // only that somebody changed something settles no dispute. The figures are the record.
        entry.Action.Should().Be("billing.plan_rates");
        entry.TargetId.Should().Be(plan.Id.ToString());
        entry.Metadata.Should().Contain("150").And.Contain("25");
    }

    [Fact]
    public async Task Setting_a_sizes_prices_is_written_to_the_audit_trail_with_the_figures()
    {
        var f = Build();
        var size = ExistingSize(f.Db);

        await f.Controller.UpdateSize(size.Id, "Small", 1, 1024, 20, 3,
            runningRate: "3.00", stoppedRate: "0.30", ct: default);

        var entry = f.Audit.Entries.Should().ContainSingle().Subject;

        entry.Action.Should().Be("billing.size_rates");
        entry.TargetId.Should().Be("small");
        entry.Metadata.Should().Contain("300").And.Contain("30");
    }

    [Fact]
    public async Task An_unpriced_rate_reaches_the_audit_trail_as_unset_rather_than_as_zero()
    {
        var f = Build();
        var size = ExistingSize(f.Db);

        await f.Controller.UpdateSize(size.Id, "Small", 1, 1024, 20, 3,
            runningRate: "3.00", stoppedRate: null, ct: default);

        var entry = f.Audit.Entries.Should().ContainSingle().Subject;

        // The same distinction the columns carry has to survive into the record of the change, or
        // the audit trail says an operator priced something at nothing when they priced nothing.
        entry.Metadata.Should().Contain("null");
        entry.Metadata.Should().NotContain(":0");
    }

    [Fact]
    public async Task A_refused_price_is_not_written_to_the_audit_trail_as_though_it_had_been_set()
    {
        var f = Build();
        var size = ExistingSize(f.Db);

        await f.Controller.UpdateSize(size.Id, "Small", 1, 1024, 20, 3,
            runningRate: "-1", stoppedRate: null, ct: default);

        f.Audit.Entries.Should().BeEmpty("nothing was written, so nothing should be recorded as written");
    }

    // ---- what the form renders comes back unchanged ---------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData(0L, "0.00")]
    [InlineData(7L, "0.07")]
    [InlineData(150L, "1.50")]
    [InlineData(123456789L, "1234567.89")]
    public void A_rate_is_rendered_into_a_form_box_without_a_group_separator(long? minor, string expected)
    {
        // A box rendered as "1,234,567.89" is not a round trip: it is what the reader wants and it
        // is also what an <input type="number"> rejects, and a browser that rejects its own initial
        // value posts nothing — which reads back as "unpriced" and wipes a price nobody touched.
        MinorUnits.Box(minor).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(7L)]
    [InlineData(150L)]
    [InlineData(123456789L)]
    public void A_rate_survives_being_rendered_and_posted_back_untouched(long? minor)
    {
        MinorUnits.TryParseRate(MinorUnits.Box(minor), out var round).Should().BeTrue();
        round.Should().Be(minor);
    }

    // ---- the boxes are on the forms that submit them ---------------------------------------------

    private static string Markup =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Plans", "Index.cshtml"));

    [Fact]
    public void Every_price_box_renders_inside_the_form_that_posts_it()
    {
        var markup = Markup;

        // Sliced out of the source rather than searched for across the whole file: a box that is on
        // the page but outside the form is never posted, and the action then reads it as an empty
        // box — which is "unpriced", the exact value that looks like nothing went wrong.
        foreach (var (action, fields) in new (string Action, string[] Fields)[]
                 {
                     ("CreatePlan", ["baseRatePerHour", "diskGbHour", "allowsOverage"]),
                     ("UpdatePlan", ["baseRatePerHour", "diskGbHour", "allowsOverage"]),
                     ("CreateSize", ["runningRate", "stoppedRate"]),
                     ("UpdateSize", ["runningRate", "stoppedRate"]),
                 })
        {
            var start = markup.IndexOf($"asp-action=\"{action}\"", StringComparison.Ordinal);
            start.Should().BeGreaterThan(-1, $"the {action} form must exist");

            var end = markup.IndexOf("</form>", start, StringComparison.Ordinal);
            end.Should().BeGreaterThan(start, $"the {action} form must close");

            var form = markup[start..end];
            foreach (var field in fields)
            {
                form.Should().Contain($"name=\"{field}\"",
                    $"'{field}' has to be posted by {action} or the column it feeds stays unreachable");
            }
        }
    }

    [Fact]
    public void Every_price_box_is_labelled_in_both_languages()
    {
        var markup = Markup;
        var priceNames = new[] { "baseRatePerHour", "diskGbHour", "runningRate", "stoppedRate" };

        // Item 21 of the do-not-change list. A price box labelled only in English on a bilingual
        // panel is a box a Persian-reading operator guesses at, and guessing at a price box is how
        // an hourly rate gets typed into a per-gibibyte one.
        //
        // Walked box by box rather than asserted as "the file contains some Persian somewhere".
        // A mutation run took the Persian off one label and the file-wide search stayed green,
        // which is the failure this whole task is about wearing a different hat: a check that
        // reports success for work it never did.
        var boxes = Regex.Matches(markup, "<input\\s+id=\"(?<id>[^\"]+)\"\\s+name=\"(?<name>[^\"]+)\"")
            .Where(m => priceNames.Contains(m.Groups["name"].Value))
            .Select(m => m.Groups["id"].Value)
            .ToList();

        boxes.Should().HaveCount(8,
            "two price boxes on each of the four forms — a new one must be labelled too, so it has "
            + "to change this number rather than slip past it");

        foreach (var id in boxes)
        {
            var at = markup.IndexOf($"for=\"{id}\"", StringComparison.Ordinal);
            at.Should().BeGreaterThan(-1, $"the box '{id}' must have a label pointing at it");

            var start = markup.LastIndexOf("<label", at, StringComparison.Ordinal);
            var end = markup.IndexOf("</label>", at, StringComparison.Ordinal);
            markup[start..end].Should().Contain("isFa ?", $"the label for '{id}' must read in both languages");
        }
    }
}
