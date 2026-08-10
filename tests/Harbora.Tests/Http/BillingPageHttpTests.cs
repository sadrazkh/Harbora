using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The bill and the credit, as requests rather than as method calls.
///
/// <para>
/// The two things that matter here cannot be observed from a controller test. Who is allowed to
/// credit an account is decided by a capability policy that runs in <c>UseAuthorization</c>, before
/// the action; and whether one confirmation page can move money twice depends on a hidden field
/// surviving a real form POST. Delete the policy attribute or replace the minted id with a fresh one
/// per request and every unit test in this repository still passes. These do not.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class BillingPageHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>
    /// A customer workspace of its own, never the fixture's.
    ///
    /// <para>
    /// The shared one is <c>IsDefault</c> — the provider's — and billing exempts that workspace
    /// everywhere: the gate never refuses it and the suspension never touches it. A credit test run
    /// against it would prove nothing about a tenant.
    /// </para>
    /// </summary>
    private Guid GivenTenant(string slug, long? balanceMinor = null)
    {
        var workspace = new Workspace { Name = slug, Slug = slug };
        Panel.Seed(db =>
        {
            db.Workspaces.Add(workspace);
            if (balanceMinor is { } minor)
                db.Wallets.Add(new Wallet { WorkspaceId = workspace.Id, BalanceMinor = minor });
        });
        return workspace.Id;
    }

    // --- the customer's own bill --------------------------------------------------------------

    [Fact]
    public async Task An_anonymous_visitor_asking_for_a_bill_is_sent_to_the_login_form()
    {
        var response = await Panel.ClientFrom("203.0.113.150").GetAsync("/billing");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/login");
    }

    [Fact]
    public async Task An_ordinary_member_may_read_their_own_workspaces_bill()
    {
        // No capability on this route, deliberately. Everybody in a workspace can see what it is
        // running, so everybody may see what that costs — and this is the page somebody opens when
        // their app has stopped and they want to know why.
        Panel.GivenUser(fixture.WorkspaceId, "bill-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.151", "bill-member@example.com");

        var response = await client.GetAsync("/billing");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_bill_shows_what_each_thing_cost_and_how_long_it_ran()
    {
        // The whole feature, rendered: this app was up for two hours and stopped for three, and here
        // is what each came to.
        var hour = new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero);
        var api = Guid.CreateVersion7();

        Panel.Seed(db =>
        {
            for (var h = 0; h < 2; h++) db.BillingLedger.Add(Line(hour.AddHours(h), -1_000, api, BilledRunState.Running));
            for (var h = 2; h < 5; h++) db.BillingLedger.Add(Line(hour.AddHours(h), -100, api, BilledRunState.Stopped));
        });

        Panel.GivenUser(fixture.WorkspaceId, "bill-reader@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.152", "bill-reader@example.com");

        var page = await (await client.GetAsync("/billing?month=2026-07")).Content.ReadAsStringAsync();

        page.Should().Contain("bill-me-api", "the name is copied onto the line, not joined back to the app");
        page.Should().Contain("2 / 3", "two hours running and three stopped is the question being answered");
        // 2 × 1000 + 3 × 100 minor units is -2300 in the ledger's own convention — negative is money
        // out — and that sign is exactly right for the reconciliation note further down this same
        // page. It is wrong under a column headed "Cost": a customer who asked "this app was on for
        // ten hours, what did it cost me" did not ask for a minus sign in front of the answer. The
        // view flips it at render, nowhere near the arithmetic WalletServiceTests' own reconciliation
        // test still depends on.
        page.Should().Contain("23.00", "2 × 1000 + 3 × 100 minor units, converted once at the view boundary")
            .And.NotContain("-23.00", "a cost column reads as a positive amount, not the ledger's own negative sign");
    }

    [Fact]
    public async Task A_bill_calls_a_databases_disk_a_databases_disk_rather_than_other()
    {
        // The half of the ledger-key decision that only the customer sees. Giving a database's disk
        // its own BilledResourceType is pointless if the page's kind switch drops it into its
        // catch-all arm: the two lines the key was split to distinguish would both read as one word
        // that names neither. This branch has already been bitten once by a `_ =>` default quietly
        // absorbing an appended enum member while reporting success.
        var hour = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
        var database = Guid.CreateVersion7();

        Panel.Seed(db => db.BillingLedger.Add(new BillingLedgerEntry
        {
            WorkspaceId = fixture.WorkspaceId,
            BillingHour = hour,
            Kind = LedgerKind.Charge,
            AmountMinor = -400,
            ResourceType = BilledResourceType.ServiceVolume,
            ResourceId = database,
            ResourceName = "bill-me-db",
            RunState = BilledRunState.NotApplicable,
            Hours = 1,
        }));

        Panel.GivenUser(fixture.WorkspaceId, "bill-disk@example.com", SystemRole.Member);

        var english = await Panel.SignedInAs("203.0.113.171", "bill-disk@example.com");
        english.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("en"));
        var persian = await Panel.SignedInAs("203.0.113.172", "bill-disk@example.com");

        var inEnglish = await (await english.GetAsync("/billing?month=2026-09")).Content.ReadAsStringAsync();
        var inPersian = await (await persian.GetAsync("/billing?month=2026-09")).Content.ReadAsStringAsync();

        inEnglish.Should().Contain("bill-me-db").And.Contain("Database disk")
            .And.NotContain(">Other<", "the catch-all arm names nothing the customer can act on");

        // "دیسک دیتابیس" — the Persian counterpart, as the razor encoder emits it.
        inPersian.Should().Contain(
            "&#x62F;&#x6CC;&#x633;&#x6A9; &#x62F;&#x6CC;&#x62A;&#x627;&#x628;&#x6CC;&#x633;",
            "the panel's default language is Persian and this is the screen the bill is read on");
    }

    [Fact]
    public async Task A_bill_for_a_workspace_nothing_has_ever_charged_does_not_claim_a_balance_of_zero()
    {
        // "Nobody has billed you" and "you have nothing left" are opposite situations, and the panel
        // does not print a nought for a figure nobody has set — the same rule it applies to a disk
        // nobody has measured.
        Panel.GivenUser(fixture.WorkspaceId, "bill-fresh@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.153", "bill-fresh@example.com");

        var page = await (await client.GetAsync("/billing?month=2031-01")).Content.ReadAsStringAsync();

        page.Should().Contain("&#x647;&#x646;&#x648;&#x632; &#x635;&#x648;&#x631;&#x62A;&#x200C;&#x62D;&#x633;&#x627;&#x628;&#x6CC; &#x635;&#x627;&#x62F;&#x631; &#x646;&#x634;&#x62F;&#x647; &#x627;&#x633;&#x62A;.",
            "the panel's default language is Persian, and this is the sentence it says there");
        page.Should().NotContain("0.00", "printing a nought would claim a balance nobody has set");
    }

    [Fact]
    public async Task The_bill_is_written_in_the_language_the_customer_asked_for()
    {
        // The contrast is the assertion, not the English page on its own: this panel defaults to
        // Persian, so a test that only looked at the English request would pass on a page with no
        // translation in it at all. This is the screen the whole feature exists to produce and it is
        // customer-facing, which is exactly where an English-only page is not acceptable.
        Panel.GivenUser(fixture.WorkspaceId, "bill-bilingual@example.com", SystemRole.Member);

        var english = await Panel.SignedInAs("203.0.113.166", "bill-bilingual@example.com");
        english.DefaultRequestHeaders.AcceptLanguage.Add(
            new System.Net.Http.Headers.StringWithQualityHeaderValue("en"));
        var persian = await Panel.SignedInAs("203.0.113.167", "bill-bilingual@example.com");

        var inEnglish = await (await english.GetAsync("/billing")).Content.ReadAsStringAsync();
        var inPersian = await (await persian.GetAsync("/billing")).Content.ReadAsStringAsync();

        inEnglish.Should().Contain("""<html lang="en" dir="ltr""").And.Contain("What each thing cost");
        inPersian.Should().Contain("""<html lang="fa" dir="rtl""")
            .And.Contain("&#x635;&#x648;&#x631;&#x62A;&#x200C;&#x62D;&#x633;&#x627;&#x628;",
                "the heading in Persian, so asking for English is what changed the page");
    }

    // --- balance vouchers --------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_member_redeems_a_voucher_into_their_current_workspace()
    {
        const string code = "ABCDEFGHJKLM";
        var tenant = GivenTenant("voucher-receiver", balanceMinor: -5_000);
        var voucher = new BillingVoucher
        {
            CodeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))),
            CodeHint = code[^4..],
            AmountMinor = 100_000,
            Currency = "IRR",
            Note = "support top-up",
            CreatedByUserId = Guid.CreateVersion7()
        };
        Panel.Seed(db => db.BillingVouchers.Add(voucher));
        Panel.GivenUser(tenant, "voucher-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.174", "voucher-member@example.com");
        var token = await client.AntiforgeryTokenFrom("/billing");

        var response = await client.PostFormAsync("/billing/voucher", token, ("code", "ABCDE-FGHJK-LM"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/billing");
        BalanceOf(tenant).Should().Be(95_000);
        Panel.Read(db => db.BillingVouchers.AsNoTracking().Single(v => v.Id == voucher.Id)
                .RedeemedWorkspaceId)
            .Should().Be(tenant);
        Panel.Read(db => db.BillingLedger.IgnoreQueryFilters().Count(l => l.Id == voucher.Id))
            .Should().Be(1, "a voucher is the idempotency key for its one credit line");
    }

    [Fact]
    public async Task An_ordinary_member_cannot_open_the_provider_voucher_console()
    {
        var tenant = GivenTenant("voucher-console-refused");
        Panel.GivenUser(tenant, "voucher-console-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.175", "voucher-console-member@example.com");

        var response = await client.GetAsync("/vouchers");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    // --- who may credit -----------------------------------------------------------------------

    [Fact]
    public async Task A_member_is_refused_the_credit_form_by_the_policy()
    {
        var tenant = GivenTenant("credit-refused-form");
        Panel.GivenUser(fixture.WorkspaceId, "credit-member@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.154", "credit-member@example.com");

        var response = await client.GetAsync($"/tenants/{tenant}/credit");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
    }

    [Fact]
    public async Task A_member_posting_a_credit_straight_at_the_endpoint_moves_no_money()
    {
        // The form being hidden is not the control. This is the same request an administrator's
        // browser makes, sent by somebody who is not one.
        var tenant = GivenTenant("credit-refused-post", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-sneak@example.com", SystemRole.Member);
        var client = await Panel.SignedInAs("203.0.113.155", "credit-sneak@example.com");
        var token = await client.AntiforgeryTokenFrom("/billing");

        var response = await client.PostFormAsync($"/tenants/{tenant}/credit", token,
            ("creditId", Guid.CreateVersion7().ToString()), ("amount", "500000"), ("note", "helping myself"));

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().Be("/account/denied");
        BalanceOf(tenant).Should().Be(0);
    }

    [Fact]
    public async Task A_credit_with_no_antiforgery_token_is_refused()
    {
        var tenant = GivenTenant("credit-no-token", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-owner-x@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.156", "credit-owner-x@example.com");

        var response = await client.PostFormWithoutTokenAsync($"/tenants/{tenant}/credit",
            ("creditId", Guid.CreateVersion7().ToString()), ("amount", "500000"), ("note", "cross-site"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        BalanceOf(tenant).Should().Be(0);
    }

    // --- one confirmation, one credit ---------------------------------------------------------

    [Fact]
    public async Task An_owner_credits_a_tenant_through_the_form_the_panel_renders()
    {
        var tenant = GivenTenant("credit-happy", balanceMinor: -5_000);
        Panel.GivenUser(fixture.WorkspaceId, "credit-owner@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.157", "credit-owner@example.com");

        var response = await SubmitConfirmationPageAsync(client, tenant, "1000", "card payment");

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        response.RedirectPath().Should().StartWith("/tenants/");
        BalanceOf(tenant).Should().Be(95_000, "1000 major units is 100,000 minor ones, on top of -5,000");
    }

    [Fact]
    public async Task One_confirmation_page_submitted_twice_credits_once()
    {
        // The double-click, end to end. The id lives in a hidden field on the page the administrator
        // confirmed on, so both POSTs carry the same one — and the second writes nothing.
        var tenant = GivenTenant("credit-double", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-double@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.158", "credit-double@example.com");

        var (token, creditId) = await ConfirmationPageAsync(client, tenant);
        await client.PostFormAsync($"/tenants/{tenant}/credit", token,
            ("creditId", creditId), ("amount", "1000"), ("note", "card payment"));
        await client.PostFormAsync($"/tenants/{tenant}/credit", token,
            ("creditId", creditId), ("amount", "1000"), ("note", "card payment"));

        BalanceOf(tenant).Should().Be(100_000, "the same decision arrived twice");
        Panel.Read(db => db.BillingLedger.IgnoreQueryFilters()
            .Count(l => l.WorkspaceId == tenant && l.Kind == LedgerKind.Credit)).Should().Be(1);
    }

    [Fact]
    public async Task Two_confirmation_pages_credit_twice()
    {
        // The other half of the rule. Loading the page again is how an administrator deliberately
        // takes a second payment from the same customer in the same hour, which is an ordinary day.
        var tenant = GivenTenant("credit-twice", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-twice@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.159", "credit-twice@example.com");

        await SubmitConfirmationPageAsync(client, tenant, "1000", "first payment");
        await SubmitConfirmationPageAsync(client, tenant, "1000", "second payment");

        BalanceOf(tenant).Should().Be(200_000);
    }

    [Fact]
    public async Task An_amount_that_is_not_a_number_is_sent_back_to_the_form_rather_than_read_as_nothing()
    {
        // The refusal has to name the mistake. A parser that answered zero for a typo, or a refusal
        // that said "a credit puts money in" about the word "lots", sends an administrator looking
        // for a problem they do not have.
        var tenant = GivenTenant("credit-typo", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-typo@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "credit-typo@example.com");

        var response = await SubmitConfirmationPageAsync(client, tenant, "a lot", "card payment");

        response.RedirectPath().Should().Be($"/tenants/{tenant}/credit",
            "the confirmation page is where the mistake can be corrected");
        (await FollowAsync(client, response)).Should().Contain("Enter the amount in figures");
        BalanceOf(tenant).Should().Be(0);
        Panel.Read(db => db.BillingLedger.IgnoreQueryFilters().Any(l => l.WorkspaceId == tenant))
            .Should().BeFalse();
    }

    [Fact]
    public async Task A_negative_amount_typed_into_the_credit_box_takes_nothing_off()
    {
        // The one screen where money moves without a resource, an hour or a unique index behind it.
        // A charge made through this door would have none of that ceremony, so it is refused.
        //
        // A negative number is still not a credit. The refusal points to the dedicated adjustment
        // workflow so that this narrowly-scoped form cannot silently change meaning.
        var tenant = GivenTenant("credit-negative", balanceMinor: 50_000);
        Panel.GivenUser(fixture.WorkspaceId, "credit-negative@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "credit-negative@example.com");

        var response = await SubmitConfirmationPageAsync(client, tenant, "-100", "taking it back");

        var page = await FollowAsync(client, response);
        page.Should().Contain("use Adjust balance");
        BalanceOf(tenant).Should().Be(50_000);
    }

    [Fact]
    public async Task A_form_that_arrives_without_the_id_its_page_minted_credits_nothing()
    {
        // The hidden field is the whole idempotency story. A POST without it must not be treated as
        // a fresh decision — that is the door through which "one confirmation, one credit" would
        // quietly become "one POST, one credit".
        var tenant = GivenTenant("credit-no-id", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-no-id@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.168", "credit-no-id@example.com");
        var token = await client.AntiforgeryTokenFrom($"/tenants/{tenant}/credit");

        var response = await client.PostFormAsync($"/tenants/{tenant}/credit", token,
            ("amount", "1000"), ("note", "card payment"));

        (await FollowAsync(client, response)).Should().Contain("Open the page again");
        BalanceOf(tenant).Should().Be(0);
    }

    [Fact]
    public async Task A_refused_credit_comes_back_with_what_was_typed_still_in_the_boxes()
    {
        // And not in the URL: an amount and somebody's note about a customer's payment would
        // otherwise sit in browser history and in every access log between here and the panel.
        var tenant = GivenTenant("credit-refill", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-refill@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.169", "credit-refill@example.com");

        var response = await SubmitConfirmationPageAsync(client, tenant, "not a number", "invoice 4471");

        response.Headers.Location!.OriginalString.Should().NotContain("invoice");
        (await FollowAsync(client, response)).Should().Contain("invoice 4471");
    }

    [Fact]
    public async Task A_credit_reaches_a_tenant_that_is_not_the_administrators_own_workspace()
    {
        // The failure this guards is invisible from inside the action: the administrator's session
        // belongs to the provider's workspace, so every read about the customer runs through a
        // tenant filter that matches none of their rows. A credit that opened a second wallet nobody
        // can read would look, from this page, exactly like one that worked.
        var tenant = GivenTenant("credit-cross-tenant", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-cross@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "credit-cross@example.com");

        await SubmitConfirmationPageAsync(client, tenant, "1000", "card payment");

        Panel.Read(db => db.Wallets.IgnoreQueryFilters().Count(w => w.WorkspaceId == tenant))
            .Should().Be(1, "a second wallet would be a second, unreadable balance");
        BalanceOf(tenant).Should().Be(100_000);
    }

    [Fact]
    public async Task Crediting_a_suspended_tenant_lifts_the_suspension_the_balance_caused()
    {
        // What the whole task is for. The suspension is lifted from a request whose session belongs
        // to the provider — the scope under which the platform's own start route used to fail to
        // find the customer's apps at all.
        var tenant = GivenTenant("credit-resumes", balanceMinor: -5_000);
        Panel.Seed(db =>
        {
            var workspace = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == tenant);
            workspace.IsSuspended = true;
            workspace.SuspendedReason = SuspensionReason.NoBalance;
        });

        Panel.GivenUser(fixture.WorkspaceId, "credit-resume@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.163", "credit-resume@example.com");

        await SubmitConfirmationPageAsync(client, tenant, "1000", "card payment");

        var after = Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == tenant));
        after.IsSuspended.Should().BeFalse();
        after.SuspendedReason.Should().Be(SuspensionReason.None);
    }

    [Fact]
    public async Task Crediting_a_tenant_the_provider_suspended_by_hand_leaves_that_suspension_alone()
    {
        // Paying a bill is not a request to undo an operator's decision — and the money still lands.
        var tenant = GivenTenant("credit-manual", balanceMinor: -5_000);
        Panel.Seed(db =>
        {
            var workspace = db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == tenant);
            workspace.IsSuspended = true;
            workspace.SuspendedReason = SuspensionReason.Manual;
        });

        Panel.GivenUser(fixture.WorkspaceId, "credit-manual@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.164", "credit-manual@example.com");

        await SubmitConfirmationPageAsync(client, tenant, "1000", "card payment");

        var after = Panel.Read(db => db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == tenant));
        after.IsSuspended.Should().BeTrue();
        after.SuspendedReason.Should().Be(SuspensionReason.Manual);
        BalanceOf(tenant).Should().Be(95_000);
    }

    [Fact]
    public async Task A_credit_is_written_into_the_audit_trail_with_the_amount_on_it()
    {
        // Money moving is exactly what an audit log is for, and "who tried" is half of it.
        var tenant = GivenTenant("credit-audited", balanceMinor: 0);
        Panel.GivenUser(fixture.WorkspaceId, "credit-audit@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.165", "credit-audit@example.com");

        await SubmitConfirmationPageAsync(client, tenant, "1000", "card payment");

        var entry = Panel.Read(db => db.AuditLogs.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefault(a => a.Action == "billing.credit" && a.TargetId == tenant.ToString()));
        entry.Should().NotBeNull();
        entry!.MetadataJson.Should().Contain("100000");
    }

    // --- the shape of a form submission -------------------------------------------------------

    /// <summary>
    /// Renders the confirmation page and takes both hidden fields off it: the antiforgery token, and
    /// the id this rendering minted. Nothing is invented — a test that made up a credit id would be
    /// asserting on an idempotency key the panel never issues.
    /// </summary>
    private static async Task<(string Token, string CreditId)> ConfirmationPageAsync(
        HttpClient client, Guid tenant)
    {
        var path = $"/tenants/{tenant}/credit";
        var token = await client.AntiforgeryTokenFrom(path);
        var html = await (await client.GetAsync(path)).Content.ReadAsStringAsync();

        var match = System.Text.RegularExpressions.Regex.Match(
            html, """<input[^>]*name="creditId"[^>]*value="(?<id>[^"]+)""");
        match.Success.Should().BeTrue("the confirmation page has to carry the id it minted");

        return (token, match.Groups["id"].Value);
    }

    /// <summary>Renders the page and posts it back, the way a browser would.</summary>
    private static async Task<HttpResponseMessage> SubmitConfirmationPageAsync(
        HttpClient client, Guid tenant, string amount, string note)
    {
        var (token, creditId) = await ConfirmationPageAsync(client, tenant);
        return await client.PostFormAsync($"/tenants/{tenant}/credit", token,
            ("creditId", creditId), ("amount", amount), ("note", note));
    }

    /// <summary>
    /// Follows a redirect and reads the page it lands on — which is where a refusal actually reaches
    /// the person who caused it, TempData being one redirect long.
    /// </summary>
    private static async Task<string> FollowAsync(HttpClient client, HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Found, "a refusal redirects back to the form");
        var landed = await client.GetAsync(response.Headers.Location!.OriginalString);
        landed.StatusCode.Should().Be(HttpStatusCode.OK);
        return await landed.Content.ReadAsStringAsync();
    }

    private long BalanceOf(Guid workspaceId) => Panel.Read(db => db.Wallets.IgnoreQueryFilters()
        .Where(w => w.WorkspaceId == workspaceId).Select(w => (long?)w.BalanceMinor).FirstOrDefault()) ?? 0;

    private BillingLedgerEntry Line(DateTimeOffset hour, long amountMinor, Guid appId, BilledRunState state) =>
        new()
        {
            WorkspaceId = fixture.WorkspaceId,
            BillingHour = hour,
            Kind = LedgerKind.Charge,
            AmountMinor = amountMinor,
            ResourceType = BilledResourceType.App,
            ResourceId = appId,
            ResourceName = "bill-me-api",
            RunState = state,
            Hours = 1
        };
}
