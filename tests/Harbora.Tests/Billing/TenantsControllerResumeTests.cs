using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Tests.Fakes;
using Harbora.Web.Controllers;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The provider console's own resume button, pressed on a suspension billing made.
///
/// <para>
/// Un-suspending was two field writes: <c>IsSuspended</c> false, <c>SuspendedReason</c> None. On a
/// suspension an operator made, that is the whole of it. On one <b>billing</b> made it was a trap.
/// The workloads it stopped still carry <c>WasRunningAtSuspension</c>, and
/// <c>BillingSuspension.ResumeAsync</c> — the only thing that reads those markers — does nothing at
/// all unless the reason is <see cref="SuspensionReason.NoBalance"/>. Clearing the reason therefore
/// removed the last route by which the customer's apps and databases could ever be started again:
/// containers down, markers saying somebody owes them a start, and nobody left who does. That is the
/// exact stranding <c>BillingSuspension</c> refuses to cause from the other direction, arriving
/// through the door beside it.
/// </para>
///
/// <para>
/// <b>The button routes a NoBalance resume through <c>ResumeAsync</c> rather than refusing it.</b>
/// Refusing — telling the operator that only a top-up lifts this — reads as the safer answer and is
/// not: it leaves no way at all to lift a billing suspension whose money has already arrived by some
/// other door (a credit whose resume half failed on an unreachable node, a transfer reconciled by
/// hand, billing switched off after the fact), and those are exactly the workspaces that are stuck.
/// Routing through <c>ResumeAsync</c> still gives the operator the refusal when refusal is the true
/// answer: every start goes through the billing gate, an empty balance refuses it, nothing is
/// stranded and the reason lands in front of them. One button, and the platform works out which of
/// the two answers is honest today instead of the operator guessing.
/// </para>
/// </summary>
public class TenantsControllerResumeTests
{
    [Fact]
    public async Task Lifting_a_billing_suspension_starts_back_the_database_it_stopped()
    {
        // The money arrived and the resume that should have followed it did not — the node was down,
        // or the panel was restarted mid-pass. The workspace is in credit and still suspended, and
        // this button is the only thing left that can finish the job.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: 95_000, suspended: true, reason: SuspensionReason.NoBalance);
        var database = WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var console = await Fixture(db);

        await console.Controller.Suspend(ws, suspended: false, default);

        console.Docker.Calls.Should().Contain(
            c => c.Operation == "RestartContainerAsync" && c.Target == "harbora-svc-orders-db",
            "the workloads the suspension stopped are what lifting it is for");

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeFalse();
        workspace.SuspendedReason.Should().Be(SuspensionReason.None);

        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == database))
            .WasRunningAtSuspension.Should().BeFalse(
                "a marker left set would start this database again after the next suspension is lifted");
    }

    [Fact]
    public async Task Lifting_a_billing_suspension_on_an_empty_balance_leaves_it_suspended_and_says_why()
    {
        // The honest half of the same button. The gate refuses every start, so nothing comes back —
        // and clearing the flags anyway is precisely the stranding this exists to prevent, because
        // the marker is the only surviving record that the database was ever running.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: 0, suspended: true, reason: SuspensionReason.NoBalance);
        var database = WalletHarness.SeedStoppedDatabaseOwedAStart(db, ws, "orders-db");
        await db.SaveChangesAsync();

        var console = await Fixture(db);

        await console.Controller.Suspend(ws, suspended: false, default);

        console.Docker.Calls.Should().NotContain(
            c => c.Operation == "RestartContainerAsync",
            "the gate refuses an empty balance before a node is reached");

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeTrue();
        workspace.SuspendedReason.Should().Be(SuspensionReason.NoBalance,
            "the reason is what a later top-up reads to know this suspension is its to lift");

        (await db.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync(s => s.Id == database))
            .WasRunningAtSuspension.Should().BeTrue();

        console.Error.Should().NotBeNullOrWhiteSpace()
            .And.Contain("orders-db", "the operator is told which workload did not come back");
        console.Message.Should().BeNull("a workspace that is still suspended was not resumed");
    }

    [Fact]
    public async Task Lifting_an_operators_own_suspension_is_still_two_field_writes()
    {
        // Unchanged, and it has to be: a suspension a person made stopped nothing, wrote no markers,
        // and has nothing for ResumeAsync to read. Sending it there would be a call that does nothing
        // and a reason nobody could explain.
        await using var db = WalletHarness.SystemContext();
        var ws = WalletHarness.SeedWorkspace(
            db, balanceMinor: 0, suspended: true, reason: SuspensionReason.Manual);
        await db.SaveChangesAsync();

        var console = await Fixture(db);

        await console.Controller.Suspend(ws, suspended: false, default);

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking().SingleAsync(w => w.Id == ws);
        workspace.IsSuspended.Should().BeFalse();
        workspace.SuspendedReason.Should().Be(SuspensionReason.None);
        console.Error.Should().BeNull();
    }

    // --- and the operator can see which of the two they are about to press --------------------

    [Fact]
    public async Task The_tenants_list_says_which_kind_of_suspension_each_one_is()
    {
        await using var db = WalletHarness.SystemContext();
        var billing = WalletHarness.SeedWorkspace(
            db, balanceMinor: 0, suspended: true, reason: SuspensionReason.NoBalance);
        var byHand = WalletHarness.SeedWorkspace(
            db, balanceMinor: 0, suspended: true, reason: SuspensionReason.Manual);
        await db.SaveChangesAsync();

        var view = await (await Fixture(db)).Controller.Index(default) as ViewResult;

        var rows = view!.Model.Should().BeOfType<TenantsPageViewModel>().Subject.Tenants;
        rows.Single(r => r.WorkspaceId == billing).SuspendedForNoBalance.Should().BeTrue();
        rows.Single(r => r.WorkspaceId == byHand).SuspendedForNoBalance.Should().BeFalse(
            "an operator's own suspension is lifted by pressing this button; a billing one may not be");
    }

    [Fact]
    public void The_badge_that_says_suspended_says_which_of_the_two_kinds_it_is()
    {
        // Sliced out of the markup rather than searched for across the whole file, for the reason
        // RateAdminTests slices its own forms — and this test earned that the hard way. Written as
        // "the file mentions SuspendedForNoBalance somewhere" it stayed green while the badge was
        // reduced back to the bare word "suspended", because the flag was still being read by the
        // button's tooltip a few lines below. A value that reaches the view model and is not
        // rendered where the operator is looking is a value nobody can act on.
        var badge = Slice(Markup, "@if (t.Suspended)", "@if (!t.IsDefault)");

        badge.Should().Contain("t.SuspendedForNoBalance")
            .And.Contain("suspended — no balance")
            .And.Contain("suspended — by an operator",
                "one badge for both kinds makes the two lifts look identical, and they are not");
    }

    [Fact]
    public void The_resume_button_says_that_a_billing_suspension_needs_the_balance_behind_it()
    {
        // The badge names the kind; this names the consequence. Pressing resume on a billing
        // suspension with nothing in the account is refused per workload by the gate — correct, and
        // indistinguishable from a broken button unless the screen said so beforehand.
        var form = Slice(Markup, "@if (!t.IsDefault)", "</form>");

        // The attribute is named, not just the sentence. Text that is present in the markup and
        // rendered into an attribute no browser shows is text the operator never reads — the same
        // failure as the badge one test above, one element along.
        form.Should().Contain("title=\"@(t.SuspendedForNoBalance")
            .And.Contain("refused while the account has no balance");
    }

    private static string Markup =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Tenants", "Index.cshtml"));

    private static string Slice(string markup, string from, string to)
    {
        var start = markup.IndexOf(from, StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, $"the markup must still contain {from}");

        var end = markup.IndexOf(to, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"{to} must still follow {from}");

        return markup[start..end];
    }

    // --- fixture ------------------------------------------------------------------------------

    private sealed record Panel(TenantsController Controller, FakeDockerEngine Docker)
    {
        public string? Error => Controller.TempData["Error"] as string;
        public string? Message => Controller.TempData["Message"] as string;
    }

    /// <summary>
    /// The provider console as it is wired in production: one request-scoped context belonging to
    /// the administrator's own workspace, shared by the controller, the suspension and the engine
    /// underneath it. The engine is the real one — this button's whole job is to reach it.
    ///
    /// <para>
    /// The daemon is holding the customer's database container from the outset, labelled the way
    /// <c>ManagedServiceEngine.ProvisionAsync</c> labels one. Without it a start would find nothing
    /// to restart and still write <c>Running</c>, which is a pass that proves the opposite of what
    /// it claims.
    /// </para>
    /// </summary>
    private static async Task<Panel> Fixture(BillingContext db)
    {
        db.ChangeTracker.Clear();

        var context = WalletHarness.ProviderContext(db);
        var docker = new FakeDockerEngine();
        await HoldTheContainer(docker, "orders-db");

        var engine = new Harbora.Infrastructure.Services.ManagedServiceEngine(
            context, new SingleEngineFactory(docker), new PassthroughProtector(), new NoopJobQueue(),
            new BillingGate(context, Options.Create(new BillingOptions { Enabled = true })),
            Options.Create(new HarboraRuntimeOptions()), WalletHarness.Clock,
            NullLogger<Harbora.Infrastructure.Services.ManagedServiceEngine>.Instance);

        var suspension = new BillingSuspension(
            context,
            new FakeAppOperations(WalletHarness.ProviderContext(db)),
            engine,
            Options.Create(new BillingOptions { Enabled = true }),
            NullLogger<BillingSuspension>.Instance);

        var controller = new TenantsController(
            context, null!, null!, null!, null!, null!, suspension, null!, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        controller.TempData = new TempDataDictionary(controller.HttpContext, new Nowhere());

        return new Panel(controller, docker);
    }

    private static async Task HoldTheContainer(FakeDockerEngine docker, string name)
    {
        await docker.RunContainerAsync(new DockerRunRequest(
            "postgres:16", $"harbora-svc-{name}", "harbora",
            new Dictionary<string, string>(),
            new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = name },
            [], 5432, 0, 0, null), default);
    }

    private sealed class Nowhere : ITempDataProvider
    {
        public IDictionary<string, object?> LoadTempData(HttpContext context) => new Dictionary<string, object?>();
        public void SaveTempData(HttpContext context, IDictionary<string, object?> values) { }
    }
}
