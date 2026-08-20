using System.Net;
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The operator's "what is the platform earning" page, as a real request.
///
/// <para>
/// The two things worth proving here cannot be seen from a unit test against
/// <see cref="Harbora.Infrastructure.Billing.RevenueReport"/> alone. Whether a workspace owner can
/// reach the page at all is decided by a capability policy that runs in <c>UseAuthorization</c>,
/// before the action — and whether the report actually reads across tenants, rather than only
/// happening to see its own seed data, depends on the signed-in administrator's own session
/// belonging to neither of the workspaces being asked about. <see cref="BillingPageHttpTests"/>
/// makes the same argument about the credit form; this page gets it made about a read instead of a
/// write.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RevenuePageHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// A tenant workspace of its own — never the fixture's, which is <c>IsDefault</c> (the provider's
    /// own workspace, exempt from billing everywhere) and never the administrator's signed-in one,
    /// so a page that only worked by accidentally sharing a session's scope would fail here.
    /// </summary>
    private Guid GivenTenant(string slug, long balanceMinor = 100_000)
    {
        var workspace = new Workspace { Name = slug, Slug = slug };
        Panel.Seed(db =>
        {
            db.Workspaces.Add(workspace);
            db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id, BalanceMinor = balanceMinor });
        });
        return workspace.Id;
    }

    private static DateTimeOffset ThisHour()
    {
        var utc = DateTimeOffset.UtcNow;
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static BillingLedgerEntry Charge(Guid workspaceId, DateTimeOffset hour, long amountMinor) => new()
    {
        WorkspaceId = workspaceId,
        BillingHour = hour,
        Kind = LedgerKind.Charge,
        AmountMinor = amountMinor,
        ResourceType = BilledResourceType.App,
        ResourceId = Guid.CreateVersion7(),
        ResourceName = "revenue-app",
        RunState = BilledRunState.Running,
        Hours = 1
    };

    // --- who may open the page -----------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_visitor_is_sent_to_the_login_form()
    {
        var response = await Panel.ClientFrom("203.0.113.190").GetAsync("/revenue");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task A_workspace_owner_cannot_reach_the_page_at_all()
    {
        // WorkspaceRole.Admin — the highest role a tenant can hold inside its own workspace, i.e.
        // exactly what the product calls "the workspace owner" — on an ordinary SystemRole.Member
        // account, which is what every real customer's account is. TenantsManage is a platform
        // capability: WorkspaceRolePermissions grants it to no workspace role at all, however senior.
        var tenant = GivenTenant("revenue-owner-refused");
        var owner = Panel.GivenUser(tenant, "revenue-owner@example.com", SystemRole.Member);
        Panel.Seed(db =>
        {
            var membership = db.WorkspaceMembers.IgnoreQueryFilters()
                .Single(m => m.WorkspaceId == tenant && m.UserId == owner.Id);
            membership.Role = WorkspaceRole.Admin;
        });
        var client = await Panel.SignedInAs("203.0.113.191", "revenue-owner@example.com");

        var response = await client.GetAsync("/revenue");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task An_ordinary_member_cannot_reach_the_page_either()
    {
        var tenant = GivenTenant("revenue-member-refused");
        Panel.GivenUser(tenant, "revenue-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.192", "revenue-member@example.com");

        var response = await client.GetAsync("/revenue");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_platform_owner_opens_the_page()
    {
        Panel.GivenUser(fixture.WorkspaceId, "revenue-platform-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.193", "revenue-platform-owner@example.com");

        var response = await client.GetAsync("/revenue");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // --- the tenant-filter trap, proven in the direction that matters ------------------------

    [Fact]
    public async Task The_page_sees_charges_from_two_tenants_though_the_administrators_own_session_belongs_to_neither()
    {
        var a = GivenTenant("revenue-cross-a");
        var b = GivenTenant("revenue-cross-b");
        var hour = ThisHour();
        Panel.Seed(db =>
        {
            db.BillingLedger.Add(Charge(a, hour, -12_00));
            db.BillingLedger.Add(Charge(b, hour, -34_00));
        });

        // The signed-in session belongs to fixture.WorkspaceId — the provider's own, IsDefault — not
        // to either tenant above. A report that quietly read through the tenant filter would see the
        // provider's own (empty) ledger and report a platform that earned nothing.
        Panel.GivenUser(fixture.WorkspaceId, "revenue-cross-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.194", "revenue-cross-admin@example.com");
        client.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("en"));

        var page = await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync();

        // Asserted per workspace, on ids nothing else in this shared fixture could ever write to —
        // not on the platform-wide monthly total, which every other HTTP test sharing this fixture
        // also contributes real charges into. Each figure landing under exactly the tenant it was
        // seeded for, though the reading session's own workspace is a third one entirely, is the
        // proof that matters: IgnoreQueryFilters() reached both tenants, not merely one it happened
        // to already have rows for.
        page.Should().Contain($@"data-burn-workspace-id=""{a}""")
            .And.Contain($@"data-burn-workspace-id=""{b}""");
        var byWorkspace = System.Text.RegularExpressions.Regex.Matches(
                page, "data-burn-workspace-id=\"(?<id>[^\"]+)\"[^>]*data-burn-minor=\"(?<burn>\\d+)\"",
                System.Text.RegularExpressions.RegexOptions.Singleline)
            .ToDictionary(m => m.Groups["id"].Value, m => long.Parse(m.Groups["burn"].Value));
        byWorkspace[a.ToString()].Should().Be(1_200);
        byWorkspace[b.ToString()].Should().Be(3_400);
    }

    [Fact]
    public async Task A_freshly_seeded_charge_moves_the_current_months_total_by_exactly_its_own_amount()
    {
        // The reconciliation acceptance test, written as a delta rather than an absolute figure: this
        // fixture is shared by every HTTP test in the collection, and several of them legitimately
        // write real charges into the current month before this one ever runs. Asserting an exact
        // total would be asserting on however much of that happened to land first. Asserting the
        // CHANGE one more seeded charge produces is exactly as strong a reconciliation proof and does
        // not care what came before it.
        Panel.GivenUser(fixture.WorkspaceId, "revenue-delta-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.199", "revenue-delta-admin@example.com");

        var currentMonth = DateTimeOffset.UtcNow.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        long CurrentMonthTotal(string page)
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                page,
                $"data-revenue-month=\"{currentMonth}\"[^>]*data-revenue-charged-minor=\"(?<total>-?\\d+)\"",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            match.Success.Should().BeTrue("the current month's row always renders, seeded or not");
            return long.Parse(match.Groups["total"].Value);
        }

        var before = CurrentMonthTotal(await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync());

        var tenant = GivenTenant("revenue-reconcile");
        Panel.Seed(db => db.BillingLedger.Add(Charge(tenant, ThisHour(), -7_531)));

        var after = CurrentMonthTotal(await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync());

        (after - before).Should().Be(7_531, "exactly what was just seeded, on top of whatever was already there");
    }

    [Fact]
    public async Task A_workspace_with_only_a_handful_of_billed_hours_shows_the_same_not_enough_history_honesty_the_bill_itself_shows()
    {
        var tenant = GivenTenant("revenue-thin-history");
        var hour = ThisHour();
        Panel.Seed(db =>
        {
            // Three distinct hours — well under WalletService.MinimumHistoryHours (24).
            for (var h = 0; h < 3; h++)
                db.BillingLedger.Add(Charge(tenant, hour.AddHours(-h), -500));
        });
        Panel.GivenUser(fixture.WorkspaceId, "revenue-thin-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.195", "revenue-thin-admin@example.com");

        var page = await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync();

        page.Should().Contain($@"data-burn-workspace-id=""{tenant}""")
            .And.Contain(@"data-forecast-state=""insufficient-history""")
            .And.Contain(@"data-forecast-history-hours=""3""")
            .And.Contain(@"data-forecast-minimum-history-hours=""24""");
    }

    [Fact]
    public async Task A_workspace_that_has_burned_nothing_in_thirty_days_is_not_listed_as_a_top_burner()
    {
        var quiet = GivenTenant("revenue-quiet");
        Panel.GivenUser(fixture.WorkspaceId, "revenue-quiet-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.196", "revenue-quiet-admin@example.com");

        var page = await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync();

        page.Should().NotContain($@"data-burn-workspace-id=""{quiet}""");
    }

    [Fact]
    public async Task A_suspended_workspaces_row_says_so()
    {
        var tenant = GivenTenant("revenue-suspended");
        var hour = ThisHour();
        Panel.Seed(db =>
        {
            db.BillingLedger.Add(Charge(tenant, hour, -900));
            var workspace = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == tenant);
            workspace.IsSuspended = true;
            workspace.SuspendedReason = SuspensionReason.NoBalance;
        });
        Panel.GivenUser(fixture.WorkspaceId, "revenue-suspended-admin@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.197", "revenue-suspended-admin@example.com");
        client.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("en"));

        var page = await (await client.GetAsync("/revenue")).Content.ReadAsStringAsync();

        // The em dash is written out by Razor's encoder as a numeric character reference even in
        // English text — see BillingPageHttpTests' own note on this — so the assertion matches that
        // rather than the literal character the view's source carries.
        RowFor(page, tenant).Should().Contain(@"data-burn-suspended=""true""")
            .And.Contain("suspended &#x2014; no balance");
    }

    /// <summary>
    /// The one workspace's row, as its own substring — never a bare <c>page.Should().Contain(...)</c>
    /// on a value like <c>"true"</c> that another test's row, seeded into this same shared fixture,
    /// could just as easily have produced.
    /// </summary>
    private static string RowFor(string page, Guid workspaceId)
    {
        var start = page.IndexOf($@"data-burn-workspace-id=""{workspaceId}""", StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the row for {workspaceId} must be on the page");
        var end = page.IndexOf("</tr>", start, StringComparison.Ordinal);
        return page[start..end];
    }
}
