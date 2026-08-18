using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

/// <summary>
/// The warning that goes out before the lights do.
///
/// <para>
/// Almost every test here is about <b>not</b> sending one. That is deliberate: a low-balance warning
/// is trivial to raise and worth nothing unless the customer still reads it, and the way it becomes
/// worth nothing is twenty copies of it — one an hour, or twenty-four in one backfill — teaching them
/// to skip past the twenty-first, which is the one that mattered. So the count is asserted, not the
/// existence, and the two things that legitimately make a warning news again — money arriving, and a
/// balance that climbed clear of the window and fell back into it — each have a test of their own.
/// </para>
/// </summary>
public class LowBalanceAlertTests
{
    /// <summary>The hour every test charges, borrowed so both files move together if it ever changes.</summary>
    private static readonly DateTimeOffset Hour = new(2026, 8, 9, 14, 0, 0, TimeSpan.Zero);

    /// <summary>What one running app costs an hour in every fixture below.</summary>
    private const long RatePerHour = 500;

    /// <summary>The window in every fixture below, so the threshold is a number a reader can hold.</summary>
    private const int WarningHours = 24;

    /// <summary>24 hours at 500 an hour. Below this the workspace is inside its warning window.</summary>
    private const long Threshold = WarningHours * RatePerHour;

    /// <summary>
    /// A workspace with one running app, a chosen balance and a chosen warning window.
    ///
    /// <para>
    /// The balance is written straight onto the wallet rather than credited through
    /// <c>WalletService</c>. A credit is a ledger line and a resume attempt, neither of which this
    /// file is about, and the two would have to be kept in step with the tick's own arithmetic for no
    /// gain — what these tests need is a workspace that starts the hour at a known number.
    /// </para>
    /// </summary>
    private static Guid SeedTenant(
        BillingContext db,
        string name,
        long balanceMinor,
        int lowBalanceHours = WarningHours,
        long ratePerHour = RatePerHour,
        AppStatus status = AppStatus.Running)
    {
        var workspaceId = Harness.SeedWorkspaceWithOneRunningApp(db, name, ratePerHour, status: status);
        db.SaveChanges();

        SetBalance(db, workspaceId, balanceMinor);

        var wallet = db.Wallets.Single(w => w.WorkspaceId == workspaceId);
        wallet.LowBalanceHours = lowBalanceHours;
        db.SaveChanges();

        return workspaceId;
    }

    /// <summary>Puts the balance where a test needs it, standing in for hours nobody wants to run.</summary>
    private static void SetBalance(BillingContext db, Guid workspaceId, long balanceMinor)
    {
        var wallet = db.Wallets.Single(w => w.WorkspaceId == workspaceId);
        wallet.BalanceMinor = balanceMinor;
        db.SaveChanges();
    }

    private static Wallet WalletOf(BillingContext db, Guid workspaceId)
    {
        db.ChangeTracker.Clear();
        return db.Wallets.Single(w => w.WorkspaceId == workspaceId);
    }

    private static List<RecordingNotificationService.Sent> LowBalance(RecordingNotificationService told) =>
        told.Notifications.Where(n => n.Event == AlertEvent.LowBalance).ToList();

    // --- once, not once an hour -------------------------------------------------------------

    [Fact]
    public async Task A_workspace_inside_its_warning_window_is_warned()
    {
        // The whole point of the feature: the customer hears about it while they can still act.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().ContainSingle().Which.Workspace.Should().Be(ws);
    }

    [Fact]
    public async Task A_workspace_inside_its_warning_window_gets_one_warning_not_one_an_hour()
    {
        // A customer warned twenty times stops reading the warnings, and the twenty-first is the one
        // that says their site is about to stop.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);
        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour.AddHours(1), default);
        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour.AddHours(2), default);

        LowBalance(told).Should().ContainSingle(
            "three hours inside the window is one piece of news, not three");
    }

    [Fact]
    public async Task A_backfill_of_a_whole_day_still_sends_one_warning()
    {
        // The de-duplication has to be persisted, not held in the pass: a catch-up opens a fresh
        // scope per hour, so anything remembered in memory for the length of one hour remembers
        // nothing at all. Twenty-four hours of backfill is where a per-hour warning would show.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).CatchUpAsync(Hour.AddHours(-1), default);

        LowBalance(told).Should().ContainSingle();
    }

    [Fact]
    public async Task A_retried_tick_does_not_warn_a_second_time()
    {
        // The durable queue delivers the same hour twice. It must not deliver the same warning twice.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);
        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().ContainSingle();

        // And the retry must not quietly re-arm it either. The second pass writes no ledger line,
        // so a burn rate read off what THIS pass wrote is zero — which reads as "nothing is running
        // the balance down", forgets the warning, and hands the next hour a clean slate to warn
        // again from. The rate has to come from what the hour cost, not from who wrote it.
        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().Be(Threshold - 1_000);
    }

    [Fact]
    public async Task A_warning_window_too_large_to_multiply_out_still_warns()
    {
        // Both numbers here belong to somebody else — the operator sets the rate, the customer sets
        // the window — and their product is what the balance is compared against. Multiplied
        // unchecked, this pair wraps past long.MinValue and the comparison answers "not low", so the
        // customer with the largest bill on the install is the one who is never warned. Silent, and
        // only in production.
        //
        // 2^62 an hour and a two-hour window: the product is exactly 2^63, which is long.MinValue.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "vast", balanceMinor: 0, lowBalanceHours: 2, ratePerHour: 1L << 62);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().ContainSingle();
    }

    // --- what re-arms it --------------------------------------------------------------------

    [Fact]
    public async Task Money_arriving_re_arms_the_warning_even_though_it_did_not_clear_the_window()
    {
        // The half a hysteresis on the window alone would miss. A customer who tops up too little is
        // still heading for zero, and they have just proved they are reading and acting — telling
        // them the top-up was not enough is the most useful message this feature ever sends.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        // Still well inside the window: 24 hours at 500 is 12,000 and this leaves 10,600.
        SetBalance(db, ws, WalletOf(db, ws).BalanceMinor + 1_100);

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour.AddHours(1), default);

        LowBalance(told).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_balance_that_climbs_clear_of_the_window_and_falls_back_in_is_warned_again()
    {
        // Otherwise a customer is warned once, ever, and the second time they run down — months
        // later, on a bill they have long since stopped worrying about — they get nothing.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);
        LowBalance(told).Should().ContainSingle("the fixture has to start from a warned workspace");

        // A real top-up, clear of the window, and one charged hour for the pass to notice it in.
        SetBalance(db, ws, Threshold * 10);
        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour.AddHours(1), default);
        LowBalance(told).Should().ContainSingle("a workspace with ten days of balance is not warned");
        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().BeNull(
            "climbing clear of the window is what makes the next fall back into it news again");

        // Months of ordinary spending, compressed. The window is re-entered from above.
        SetBalance(db, ws, Threshold + 200);
        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour.AddHours(2), default);

        LowBalance(told).Should().HaveCount(2);
    }

    // --- the incident it opens (2026-08-16 monitoring-alerting spec §M4) --------------------

    [Fact]
    public async Task A_low_balance_warning_opens_an_incident_scoped_to_the_workspace_itself()
    {
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var incident = db.AlertIncidents.AsNoTracking().Should().ContainSingle().Subject;
        incident.WorkspaceId.Should().Be(ws);
        incident.Condition.Should().Be(AlertEvent.LowBalance);
        incident.SubjectRef.Should().BeNull("the workspace itself is the whole subject of a low-balance incident");
        incident.ClosedAt.Should().BeNull("nothing re-evaluates a low-balance incident; only a person or the expiry backstop closes it");
    }

    [Fact]
    public async Task A_second_warning_for_the_same_still_low_workspace_refreshes_the_open_incident_rather_than_opening_another()
    {
        // Money arriving re-arms the WARNING (see the test above this section), but the balance never
        // left the window here — so this is the ordinary "still low, told again" case, and it must
        // stay one row, not accumulate a fresh incident on every re-arm.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        SetBalance(db, ws, Threshold * 10); // clear of the window: re-arms
        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(1), default);
        SetBalance(db, ws, Threshold - 200); // back inside it: warns again
        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(2), default);

        db.AlertIncidents.AsNoTracking().Count(i => i.WorkspaceId == ws).Should().Be(1);
    }

    [Fact]
    public async Task The_balance_the_customer_was_warned_at_is_what_gets_written_down()
    {
        // The record is a balance rather than a timestamp precisely so that nothing has to remember
        // to clear it. If this stored the wrong number, "money has arrived since" would be answered
        // against a figure that was never the balance, and a warning would repeat or vanish.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var wallet = WalletOf(db, ws);
        wallet.BalanceMinor.Should().Be(Threshold - 1_000);
        wallet.LowBalanceWarnedAtBalanceMinor.Should().Be(Threshold - 1_000);
    }

    // --- who is not warned ------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_with_more_than_its_window_of_balance_is_not_warned()
    {
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold * 100);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().BeEmpty();
        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().BeNull();
    }

    [Fact]
    public async Task A_window_of_zero_hours_turns_the_warning_off()
    {
        // Zero disables a limit everywhere else in this platform, and a zero-hour window that warned
        // on every balance would be the loudest possible reading of "off".
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: 10, lowBalanceHours: 0);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_the_warning_off_does_not_erase_the_one_already_outstanding()
    {
        // Otherwise turning it off and on again sends a second copy of a warning the customer has
        // already read, which is the same noise this whole file exists to prevent.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);

        var wallet = WalletOf(db, ws);
        wallet.LowBalanceHours = 0;
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(1), default);

        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().Be(Threshold - 1_000);
    }

    [Fact]
    public async Task A_workspace_that_is_running_nothing_is_not_warned()
    {
        // Nothing is being charged, so the balance is worth an unbounded number of hours and no
        // moment is approaching to warn about. A warning here would be the platform telling a
        // customer with an idle account that their site is about to stop.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "idle", balanceMinor: 10, status: AppStatus.Created);
        var told = new RecordingNotificationService();

        var result = await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().BeEmpty();

        // And quietly, without a reported failure. An hour of zero divides into a balance exactly
        // as badly as it sounds, so a rule that called this workspace "low" would reach the message
        // and throw on the way to writing "0 hours left" — which this class catches and files as a
        // warning that could not be sent. The customer still hears nothing either way, and the
        // difference between "correctly silent" and "broken, loudly" is only visible here.
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_workspace_that_stops_everything_is_re_armed()
    {
        // The other half of the rule above, and the reason it is "not low" rather than "say nothing":
        // a customer who was warned, switched everything off, and later switched it back on is
        // running a new risk, and the warning they read weeks ago was about a different set of apps.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db).ChargeHourAsync(Hour, default);
        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().NotBeNull();

        db.ChangeTracker.Clear();
        var app = await db.Apps.SingleAsync(a => a.WorkspaceId == ws);

        // Back to a status that reserves nothing. The size is left alone on purpose: clearing it
        // would make the app an unpriced resource the pass reports, which is a different fact.
        app.Status = AppStatus.Created;
        await db.SaveChangesAsync();

        await Harness.Tick(db).ChargeHourAsync(Hour.AddHours(1), default);

        WalletOf(db, ws).LowBalanceWarnedAtBalanceMinor.Should().BeNull();
    }

    [Fact]
    public async Task Billing_that_is_switched_off_warns_nobody()
    {
        // The switch guards the money and the uptime everywhere else in this feature. It guards this
        // too: an install that upgraded into billing unasked must not start telling tenants their
        // balance is running out on an account nobody ever told them existed.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, enabled: false, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().BeEmpty();
    }

    [Fact]
    public async Task Only_the_workspace_that_is_running_low_is_told()
    {
        // Two tenants, one in trouble. A warning raised against the wrong workspace reaches the wrong
        // customer's channel, and the count alone would not notice.
        await using var db = Harness.SystemContext();
        var poor = SeedTenant(db, "poor", balanceMinor: Threshold - 500);
        SeedTenant(db, "rich", balanceMinor: Threshold * 100);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        LowBalance(told).Should().ContainSingle().Which.Workspace.Should().Be(poor);
    }

    // --- what the warning says --------------------------------------------------------------

    [Fact]
    public async Task The_warning_is_raised_as_the_event_and_severity_a_channel_can_match_on()
    {
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        var sent = told.Notifications.Should().ContainSingle().Subject;
        sent.Event.Should().Be(AlertEvent.LowBalance);
        sent.Severity.Should().Be(AlertSeverity.Warning,
            "Critical is what a workspace that has already stopped gets; this one can still be fixed");
    }

    [Fact]
    public async Task The_warning_names_the_workspace_and_how_many_hours_are_left()
    {
        // "Your balance is low" is not actionable. The number of hours is, and it is the same unit
        // the customer set the window in.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        // N4 (2026-08-16 notification-system spec): NotifyAsync's own input is the workspace name and
        // the hours left as data, not a rendered sentence in either language — see
        // NotificationTemplateCatalogTests for what a template actually does with them, and the test
        // below this one for why "both languages in the same string" is no longer how this raise site
        // answers "which language".
        var sent = told.Notifications.Should().ContainSingle().Subject;
        sent.Data.Get("WorkspaceName").Should().Be("tenant");

        // 11,000 left at 500 an hour is 22 whole hours.
        sent.Data.Get("Hours").Should().Be("22");
    }

    [Fact]
    public async Task The_warning_also_carries_the_same_runway_said_as_a_date()
    {
        // Hours and date are one runway, computed once (BurnRate.RunwayDate) and handed to both the
        // incident and the notification data — a customer must never see them disagree.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        // 11,000 left at 500 an hour is 22 whole hours from the clock the tick reads "now" off —
        // Harness.Now, not the billing hour being charged.
        var expected = Harness.Now.AddHours(22).ToString("yyyy-MM-dd");
        told.Notifications.Should().ContainSingle().Subject.Data.Get("RunsOutOn").Should().Be(expected);

        var incident = db.AlertIncidents.AsNoTracking().Should().ContainSingle().Subject;
        incident.Body.Should().Contain(expected);
    }

    [Fact]
    public async Task The_hours_left_are_floored_rather_than_rounded_up()
    {
        // 11,999 at 500 an hour is 23.998 hours. Rounding that to 24 tells a customer they have a
        // day when the last hour of it does not exist.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 1);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        // 11,499 / 500 = 22.998.
        told.Notifications.Should().ContainSingle().Subject.Data.Get("Hours").Should().Be("22");
    }

    [Fact]
    public async Task The_warning_hands_over_facts_not_prose_so_the_recipient_decides_the_language()
    {
        // Item 21 of the do-not-change list, arriving at a surface with no request behind it — but
        // N4 (2026-08-16 notification-system spec) changes what "no request behind it" means for this
        // raise site specifically: BillingTick.LowBalanceMessage still says both languages at once for
        // the incident, because an incident has no reader to pick one for. NotifyAsync's own input no
        // longer says either language: it is the workspace name and the hours left, and
        // NotificationService picks the language per actual recipient (a member's own
        // PreferredCulture, or the platform default for a channel with no person attached) — proved
        // here by rendering the same facts both ways and getting two different sentences.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        var told = new RecordingNotificationService();

        await Harness.Tick(db, notifications: told).ChargeHourAsync(Hour, default);

        var data = told.Notifications.Should().ContainSingle().Subject.Data;
        var catalog = new NotificationTemplateCatalog();
        var fa = catalog.Render(data, "fa");
        var en = catalog.Render(data, "en");

        fa.Subject.Should().Contain("اعتبار");
        en.TextBody.Should().Contain("balance");
        fa.Subject.Should().NotBe(en.Subject, "the same facts must read differently depending who is asking");
    }

    // --- when the warning itself goes wrong -------------------------------------------------

    /// <summary>A channel layer that is down. The real one swallows delivery failures; this is the
    /// case where the failure escapes anyway — a disposed context, a broken rule query.</summary>
    private sealed class BrokenNotifications : INotificationService
    {
        public Task<int> NotifyAsync(Guid workspaceId, Harbora.Domain.Notifications.NotificationEventData evt,
            AlertSeverity severity, CancellationToken ct) =>
            throw new InvalidOperationException("the alert rules could not be read");

        public Task<NotificationResult> NotifyRuleAsync(Guid alertId, Harbora.Domain.Notifications.NotificationEventData evt,
            AlertSeverity severity, CancellationToken ct) => throw new NotSupportedException();

        public Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task A_warning_that_could_not_be_sent_does_not_cost_the_customer_their_charge()
    {
        // The money is committed before the warning goes out, and it stays committed. An hour that
        // rolled back because a Telegram bot was unreachable would be free hosting bought with a
        // notification.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        var result = await Harness.Tick(db, notifications: new BrokenNotifications())
            .ChargeHourAsync(Hour, default);

        WalletOf(db, ws).BalanceMinor.Should().Be(Threshold - 1_000);
        result.WorkspacesCharged.Should().Be(1);
    }

    [Fact]
    public async Task A_warning_that_could_not_be_sent_says_so()
    {
        // Nothing throws, the ledger adds up and the pass reports success — which is exactly the
        // shape in which a customer is never told anything and nobody finds out.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        var result = await Harness.Tick(db, notifications: new BrokenNotifications())
            .ChargeHourAsync(Hour, default);

        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("tenant").And.Contain("the alert rules could not be read");
    }

    [Fact]
    public async Task A_warning_that_could_not_be_sent_is_not_repeated_every_hour_afterwards()
    {
        // A retry that runs once an hour for ever against a channel that is down is the flood this
        // file exists to prevent, arriving through the failure path instead of the happy one. The
        // undelivered attempt is recorded on the alert rule, which is where a broken channel is
        // meant to be read.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        await Harness.Tick(db, notifications: new BrokenNotifications()).ChargeHourAsync(Hour, default);
        var second = await Harness.Tick(db, notifications: new BrokenNotifications())
            .ChargeHourAsync(Hour.AddHours(1), default);

        second.Failures.Should().BeEmpty();
    }

    // --- and that a rule actually receives it -----------------------------------------------

    private sealed class Responder : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") });
        }
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>
    /// The real notification service, over a context that can actually see the workspace's rules.
    ///
    /// <para>
    /// The scope is the whole reason this exists rather than <c>Harness.TickContext</c>, and getting
    /// it wrong would make the two tests below pass for the wrong reason. <c>Alert</c> carries a
    /// tenant filter; under a context scoped to a tenant that owns nothing, the rules read comes back
    /// empty whether or not the workspace has any — so "nobody could receive it" would be a fact
    /// about the filter, and it would go on being reported after somebody added the rule. A system
    /// scope is also what production hands this: background work has no <c>HttpContext</c>, which is
    /// exactly when <c>HttpWorkspaceScope.IsUnscoped</c> is true and the filters are inert.
    /// </para>
    /// </summary>
    private static (NotificationService Service, NotificationQueueScope Scope) RealNotifications(
        BillingContext db, HttpMessageHandler handler)
    {
        var own = Harness.SystemContext(db.Store);
        var scope = new NotificationQueueScope(db.Store, Harness.Clock);

        var service = new NotificationService(
            own,
            new PassthroughProtector(),
            new SingleHandlerFactory(handler),
            new PlatformMailer(own, new PassthroughProtector(), NullLogger<PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory,
            Harness.Clock,
            Options.Create(new NotificationOptions { DeliveryTimeoutSeconds = 10 }),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);
        return (service, scope);
    }

    /// <summary>Runs every job the tick's own notify calls queued (N1) — standing in for the job
    /// worker so a test can watch a low-balance warning reach the channel it was queued for.</summary>
    private static async Task RunQueuedDeliveriesAsync(NotificationService service, BillingContext db)
    {
        var pending = await db.NotificationDeliveries
            .Where(d => d.Status == Harbora.Domain.Common.NotificationDeliveryStatus.Pending)
            .OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync();
        foreach (var id in pending) await service.ExecuteQueuedDeliveryAsync(id, default);
    }

    /// <summary>An enabled webhook rule that takes anything the platform sends it.</summary>
    private static void SeedAlertRule(BillingContext db, Guid workspaceId)
    {
        db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId,
            Name = "ops",
            Channel = AlertChannel.Webhook,
            MinSeverity = AlertSeverity.Info,
            EncryptedTarget = """{"url":"https://hooks.example.com/abc"}""",
            IsEnabled = true,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task A_warning_no_rule_could_receive_is_not_left_recorded_as_sent()
    {
        // Nothing seeds an alert rule — AlertsController is the only thing in the product that
        // creates one — so a fresh tenant has none at all. With no rules the dispatch loop runs zero
        // times: nothing throws, nothing is delivered, and the balance the customer was "warned at"
        // has already been written down, so the warning is never sent again while the balance keeps
        // falling. Their first notice would be their site stopping.
        await using var db = Harness.SystemContext();
        SeedTenant(db, "tenant", balanceMinor: Threshold - 500);

        var handler = new Responder();
        var (notifications, scope) = RealNotifications(db, handler);
        using var _ = scope;
        var result = await Harness.Tick(db, notifications: notifications).ChargeHourAsync(Hour, default);

        handler.Calls.Should().Be(0, "the fixture is only worth anything if there really is no rule");
        result.Failures.Should().ContainSingle()
            .Which.Should().Contain("tenant").And.Contain("no alert rule");
    }

    [Fact]
    public async Task A_warning_a_rule_did_receive_is_not_reported_as_unheard()
    {
        // The other direction, and what keeps the count above honest. A rules read that found none
        // because the tenant filter hid them would report every workspace on the install as
        // unreachable — the right number for the wrong reason, and one nobody could act on.
        await using var db = Harness.SystemContext();
        var ws = SeedTenant(db, "tenant", balanceMinor: Threshold - 500);
        SeedAlertRule(db, ws);

        var handler = new Responder();
        var (notifications, scope) = RealNotifications(db, handler);
        using var _ = scope;
        var result = await Harness.Tick(db, notifications: notifications).ChargeHourAsync(Hour, default);
        await RunQueuedDeliveriesAsync(notifications, db);

        handler.Calls.Should().Be(1);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task A_low_balance_warning_reaches_an_enabled_alert_rule()
    {
        // The hole this closes: NotificationService.Matches answers false for any event it has not
        // been taught, so an appended AlertEvent delivers to nobody while the tick that raised it
        // records a warning sent. Nothing throws and no count changes. Proved against the real
        // service and a real rule rather than the recording fake, because the fake records the call
        // this test is asking about the far side of.
        var workspaceId = Guid.CreateVersion7();
        var store = "low-balance-delivery-" + Guid.NewGuid();
        await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);

        db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId,
            Name = "ops",
            Channel = AlertChannel.Webhook,
            MinSeverity = AlertSeverity.Info,
            EncryptedTarget = """{"url":"https://hooks.example.com/abc"}""",
            IsEnabled = true,
        });
        await db.SaveChangesAsync();

        var handler = new Responder();
        using var scope = new NotificationQueueScope(store);
        var service = new NotificationService(
            db,
            new PassthroughProtector(),
            new SingleHandlerFactory(handler),
            new PlatformMailer(db, new PassthroughProtector(), NullLogger<PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory,
            new FixedClock(),
            Options.Create(new NotificationOptions { DeliveryTimeoutSeconds = 10 }),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);

        await service.NotifyAsync(workspaceId,
            NotificationEventData.Create(AlertEvent.LowBalance, ("WorkspaceName", "tenant"), ("Hours", "22")),
            AlertSeverity.Warning, default);
        await RunQueuedDeliveriesAsync(service, db);

        handler.Calls.Should().Be(1);
    }

    /// <summary>Drives every job a single test's own service queued — the overload for tests that
    /// build a bare HarboraDbContext rather than a BillingContext.</summary>
    private static async Task RunQueuedDeliveriesAsync(NotificationService service, HarboraDbContext db)
    {
        var pending = await db.NotificationDeliveries
            .Where(d => d.Status == Harbora.Domain.Common.NotificationDeliveryStatus.Pending)
            .OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync();
        foreach (var id in pending) await service.ExecuteQueuedDeliveryAsync(id, default);
    }

    [Fact]
    public async Task A_low_balance_warning_respects_a_rule_that_only_wants_critical_events()
    {
        // The customer's own out. There is no per-rule opt-in flag for this event — see
        // NotificationService.Matches for why — so severity and IsEnabled are the whole of the
        // control a customer has, and they have to actually work.
        var workspaceId = Guid.CreateVersion7();
        var store = "low-balance-severity-" + Guid.NewGuid();
        await using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase(store).Options);

        db.Alerts.Add(new Alert
        {
            WorkspaceId = workspaceId,
            Name = "critical-only",
            Channel = AlertChannel.Webhook,
            MinSeverity = AlertSeverity.Critical,
            EncryptedTarget = """{"url":"https://hooks.example.com/abc"}""",
            IsEnabled = true,
        });
        await db.SaveChangesAsync();

        var handler = new Responder();
        using var scope = new NotificationQueueScope(store);
        var service = new NotificationService(
            db,
            new PassthroughProtector(),
            new SingleHandlerFactory(handler),
            new PlatformMailer(db, new PassthroughProtector(), NullLogger<PlatformMailer>.Instance),
            Harbora.Infrastructure.Functions.NullFunctionEventBus.Instance,
            scope.Factory,
            new FixedClock(),
            Options.Create(new NotificationOptions { DeliveryTimeoutSeconds = 10 }),
            new NotificationTemplateCatalog(),
            NullLogger<NotificationService>.Instance);

        await service.NotifyAsync(workspaceId,
            NotificationEventData.Create(AlertEvent.LowBalance, ("WorkspaceName", "tenant"), ("Hours", "22")),
            AlertSeverity.Warning, default);

        handler.Calls.Should().Be(0);
    }
}
