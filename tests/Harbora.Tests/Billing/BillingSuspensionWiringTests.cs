using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// That something actually calls the suspension.
///
/// <para>
/// <c>BillingSuspensionTests</c> proves the suspension does the right thing when it is called. None
/// of it proved that anything called it, and for the whole of this branch nothing did: the hourly
/// pass charged a workspace past zero, wrote its low-balance warning, and left every container up.
/// The balance would go negative without bound, the workloads would go on being charged against money
/// that was not there, and every pass would report success.
/// </para>
///
/// <para>
/// The start gate is what made that survivable to look at and impossible to notice. It refuses NEW
/// starts at a balance of nothing, so an operator watching the panel sees refusals and concludes the
/// suspension is working — while the customer's site, which was already running, never stops. These
/// tests are the ones that would have said otherwise.
/// </para>
/// </summary>
public class BillingSuspensionWiringTests
{
    /// <summary>The hour the tick's own tests charge, so the files move together.</summary>
    private static readonly DateTimeOffset Hour = BillingTickTests.Hour;

    [Fact]
    public async Task The_hourly_pass_stops_a_workspace_whose_balance_has_run_out()
    {
        // The whole feature in one assertion: at zero, the customer's workloads stop.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, stoppedRatePerHour: 100);
        var database = Harness.AddDatabase(db, ws, "tenant-db");
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Stopped);
        (await db.ManagedServices.SingleAsync(s => s.Id == database)).Status
            .Should().Be(ServiceStatus.Stopped,
                "the database is charged for its size and its disk, and the customer cannot stop it " +
                "themselves once they are suspended");

        var workspace = await db.Workspaces.SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeTrue();
        workspace.SuspendedReason.Should().Be(SuspensionReason.NoBalance,
            "a top-up has to be able to lift what the balance caused");
        result.WorkspacesSuspended.Should().Be(1);
    }

    [Fact]
    public async Task A_workspace_that_still_has_money_after_the_hour_is_left_running()
    {
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();
        SetBalance(db, ws, 5_000);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        result.WorkspacesSuspended.Should().Be(0);
    }

    [Fact]
    public async Task A_balance_the_hour_lands_exactly_on_zero_has_run_out()
    {
        // The boundary, and it is the gate's boundary rather than a second opinion about it.
        // BillingGate opens on `balance > 0`, so a workspace sitting at exactly nothing is already
        // refused every new start. A stricter test here would leave it running everything it happened
        // to have already while unable to start a thing, which is neither of the two states this
        // feature has words for.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();
        SetBalance(db, ws, 500);

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(0,
            "the fixture is only worth anything if the hour lands exactly on nothing");
        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Stopped);
        result.WorkspacesSuspended.Should().Be(1);
    }

    [Fact]
    public async Task A_workspace_nothing_has_ever_charged_is_not_stopped_for_having_no_wallet()
    {
        // No wallet is not a balance of zero — the row is created by the first hour that costs
        // something, so its absence is a workspace nothing has ever billed. Reading it as an empty
        // balance would stop every tenant on the install the moment billing was switched on, before
        // a single hour had been charged to anybody.
        await using var db = Harness.SystemContext();
        var plan = new Plan { Name = "free-plan", BaseRatePerHourMinor = 0 };
        db.Plans.Add(plan);
        var workspace = new Workspace { Name = "newcomer", Slug = "newcomer", PlanId = plan.Id };
        db.Workspaces.Add(workspace);
        db.Apps.Add(new App
        {
            WorkspaceId = workspace.Id,
            Name = "api",
            Slug = "api",
            Status = AppStatus.Running,
        });
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Wallets.AnyAsync(w => w.WorkspaceId == workspace.Id)).Should().BeFalse(
            "an app on no instance size is charged nothing, so no wallet is created");
        (await db.Apps.SingleAsync(a => a.WorkspaceId == workspace.Id)).Status
            .Should().Be(AppStatus.Running);
        (await db.Workspaces.SingleAsync(w => w.Id == workspace.Id)).IsSuspended.Should().BeFalse();
        result.WorkspacesSuspended.Should().Be(0);
    }

    [Fact]
    public async Task The_providers_own_workspace_is_not_stopped_by_the_hourly_pass_either()
    {
        // The pass charges the platform's own workspace like everybody else, so its balance goes
        // negative on the first hour and stays there for the life of the install. Suspending it would
        // take the panel down to collect a debt the platform owes itself — and reporting the refusal
        // would put an unactionable line in every pass for ever, which is how the channel that also
        // carries the real faults stops being read.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "provider", ratePerHour: 500);
        await db.SaveChangesAsync();

        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsDefault = true;
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        result.WorkspacesSuspended.Should().Be(0);
        result.Failures.Should().BeEmpty("a refusal that is true every hour for ever is not news");
    }

    [Fact]
    public async Task Billing_that_is_switched_off_stops_nobody_from_the_hourly_pass_either()
    {
        // Off is the shipped default, and the switch guards the act that costs somebody their uptime
        // at both ends: the pass never reaches the suspension, and the suspension would refuse it.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db, enabled: false).ChargeHourAsync(Hour, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).IsSuspended.Should().BeFalse();
        result.WorkspacesSuspended.Should().Be(0);
        result.Failures.Should().BeEmpty(
            "a pass that offered every workspace to a suspension that then refused them all would " +
            "leave exactly this state behind, and a refusal per tenant per hour on an install that " +
            "never turned billing on is the loudest possible way of doing nothing");
    }

    [Fact]
    public async Task A_workspace_already_stopped_for_an_empty_balance_is_not_stopped_again_next_hour()
    {
        // A suspended workspace goes on being charged for what it is still holding, so it stays at or
        // below nothing for ever and the pass reaches it every single hour afterwards. It has to
        // arrive as a retry rather than as a fresh suspension: no second stop for a container that is
        // already down, no record of what was running rebuilt from a table the first pass has already
        // emptied, and nothing reported, because nothing is wrong.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, stoppedRatePerHour: 100);
        await db.SaveChangesAsync();
        var app = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == ws);

        var ops = Harness.Operations(db);
        await Harness.Tick(db, operations: ops).ChargeHourAsync(Hour, default);
        var second = await Harness.Tick(db, operations: ops).ChargeHourAsync(Hour.AddHours(1), default);

        ops.Stopped.Should()
            .ContainSingle("it was already down, and a second stop is an outage nobody asked for")
            .Which.Should().Be(app.Id);
        second.WorkspacesSuspended.Should().Be(0, "a retry is not news, and counting it as news is");
        second.Failures.Should().BeEmpty();
        (await db.Apps.SingleAsync(a => a.Id == app.Id)).WasRunningAtSuspension.Should().BeTrue(
            "the retry adds to the record of what was running; it does not rebuild it");
    }

    [Fact]
    public async Task A_workspace_an_operator_suspended_is_named_rather_than_taken_over_by_the_pass()
    {
        // The call site may not go round Task 5's whitelist. Billing stops only what lifting a
        // billing suspension would start again, and lifting this one is not billing's to do — so the
        // apps stay up, the reason stays Manual, and the cost of that is stated rather than hidden.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "tenant", ratePerHour: 500);
        await db.SaveChangesAsync();

        var manual = await db.Workspaces.SingleAsync(w => w.Id == ws);
        manual.IsSuspended = true;
        manual.SuspendedReason = SuspensionReason.Manual;
        await db.SaveChangesAsync();

        var result = await Harness.Tick(db).ChargeHourAsync(Hour, default);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Running);
        (await db.Workspaces.SingleAsync(w => w.Id == ws)).SuspendedReason
            .Should().Be(SuspensionReason.Manual);
        result.WorkspacesSuspended.Should().Be(0);
        result.Failures.Should().ContainSingle(f => f.Contains("operator"));
    }

    [Fact]
    public async Task A_stop_the_node_refused_is_named_and_the_next_workspace_is_still_stopped()
    {
        // One tenant's unreachable node must not leave the next tenant's containers up on a balance
        // of nothing. Both are out of money; only the first one's node is gone.
        await using var db = Harness.SystemContext();
        var unreachable = Harness.SeedWorkspaceWithOneRunningApp(db, "unreachable", ratePerHour: 500);
        var reachable = Harness.SeedWorkspaceWithOneRunningApp(db, "reachable", ratePerHour: 700);
        await db.SaveChangesAsync();
        var stranded = await db.Apps.AsNoTracking().SingleAsync(a => a.WorkspaceId == unreachable);

        var ops = Harness.Operations(db);
        ops.Refuses[stranded.Id] = "the node is unreachable";

        var result = await Harness.Tick(db, operations: ops).ChargeHourAsync(Hour, default);

        result.WorkspacesCharged.Should().Be(2, "the money was settled before anything was stopped");
        result.Failures.Should().ContainSingle(f => f.Contains("unreachable"));
        (await db.Apps.SingleAsync(a => a.WorkspaceId == reachable)).Status
            .Should().Be(AppStatus.Stopped);
        (await db.Apps.SingleAsync(a => a.Id == stranded.Id)).WasRunningAtSuspension.Should().BeTrue(
            "the stop failed, and what was running is exactly what the next pass needs to know");
    }

    [Fact]
    public async Task A_suspension_that_threw_is_named_and_costs_nobody_else_their_hour()
    {
        // The fault the test above cannot produce: not a stop that was refused and reported, but a
        // suspension that fell over part-written. The money is committed by the time this runs and
        // must stay committed — an hour rolled back because a container would not stop is free
        // hosting bought with an outage — and the workspace it happened to must not take the next
        // one's suspension down with it, nor leave half its own writes in a context the next one
        // saves under its name.
        await using var db = Harness.SystemContext();
        Harness.SeedWorkspaceWithOneRunningApp(db, "first", ratePerHour: 500);
        Harness.SeedWorkspaceWithOneRunningApp(db, "second", ratePerHour: 700);
        await db.SaveChangesAsync();

        var ops = Harness.Operations(db);
        var databases = Harness.Databases(db);
        var built = 0;

        var result = await Harness
            .Tick(db, suspension: () =>
            {
                var hostile = Harness.TickContext(db);

                // Exactly one of the two, whichever the sweep reaches first, so what this proves is
                // "the other one was still stopped" rather than "they happen to come in this order".
                if (built++ == 0)
                    hostile.FailTheNextSaveWith = new InvalidOperationException("the database went away");

                return new BillingSuspension(
                    hostile, ops, databases,
                    Options.Create(new BillingOptions { Enabled = true }),
                    NullLogger<BillingSuspension>.Instance);
            })
            .ChargeHourAsync(Hour, default);

        result.WorkspacesCharged.Should().Be(2, "the hour they were charged for stands");
        result.Failures.Should().ContainSingle(f => f.Contains("the database went away"));
        (await db.Workspaces.CountAsync(w => w.IsSuspended)).Should().Be(1);
        result.WorkspacesSuspended.Should().Be(1);
    }

    [Fact]
    public async Task A_workspace_deleted_while_the_pass_runs_is_not_counted_as_one_it_stopped()
    {
        // The read that picks the candidates and the stop itself are minutes apart on a real
        // install, and deleting a tenant is an ordinary thing for an operator to do in between.
        // Nothing was stopped, because by then there is nothing to stop — and a pass that counted it
        // anyway would be reporting an outage it never caused, which is this branch's own recurring
        // defect wearing the new code's clothes.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(db, "vanishing", ratePerHour: 500);
        await db.SaveChangesAsync();

        var result = await Harness
            .Tick(db, suspension: () =>
            {
                // The factory runs inside the sweep, in the moment between choosing this workspace
                // and stopping it.
                using var elsewhere = Harness.SystemContext(db.Store);
                elsewhere.Workspaces.Remove(elsewhere.Workspaces.Single(w => w.Id == ws));
                elsewhere.SaveChanges();

                return new BillingSuspension(
                    Harness.TickContext(db), Harness.Operations(db), Harness.Databases(db),
                    Options.Create(new BillingOptions { Enabled = true }),
                    NullLogger<BillingSuspension>.Instance);
            })
            .ChargeHourAsync(Hour, default);

        result.WorkspacesSuspended.Should().Be(0,
            "nothing was stopped, so nothing may be counted as stopped");
        result.Failures.Should().ContainSingle(f => f.Contains("No workspace with id"));
    }

    [Fact]
    public async Task A_backfill_charges_every_hour_for_what_was_running_before_it_stops_anything()
    {
        // Why the suspension is a pass of its own rather than a step inside the charge loop, and the
        // reason is money. Each hour of a catch-up is priced from the status the workload is in NOW,
        // so a suspension taken halfway through would stop the apps and leave every remaining hour of
        // a period the customer spent running to be billed at the stopped rate — the pass corrupting
        // its own input, quietly, on a bill that says "Stopped" about an app that was up. Settle the
        // whole arrears first; then stop whoever it left at nothing.
        await using var db = Harness.SystemContext();
        var ws = Harness.SeedWorkspaceWithOneRunningApp(
            db, "tenant", ratePerHour: 500, stoppedRatePerHour: 100);
        await db.SaveChangesAsync();

        // 17:00, 18:00 and 19:00, all three over by Harness.Now.
        var result = await Harness.Tick(db).CatchUpAsync(Hour.AddHours(2), default);

        result.HoursBackfilled.Should().Be(3);
        (await db.Wallets.SingleAsync(w => w.WorkspaceId == ws)).BalanceMinor.Should().Be(-1_500,
            "three hours of a running app at 500, not one at 500 and two at the stopped rate");
        (await db.BillingLedger.Where(l => l.WorkspaceId == ws).ToListAsync())
            .Should().OnlyContain(l => l.RunState == BilledRunState.Running);

        (await db.Apps.SingleAsync(a => a.WorkspaceId == ws)).Status.Should().Be(AppStatus.Stopped);
        result.WorkspacesSuspended.Should().Be(1);
    }

    /// <summary>Puts a balance on a workspace, standing in for hours nobody wants to run.</summary>
    private static void SetBalance(BillingContext db, Guid workspaceId, long balanceMinor)
    {
        var wallet = db.Wallets.Single(w => w.WorkspaceId == workspaceId);
        wallet.BalanceMinor = balanceMinor;
        db.SaveChanges();
    }
}
