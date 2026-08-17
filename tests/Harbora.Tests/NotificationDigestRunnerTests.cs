using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// N5 (2026-08-16 notification-system spec, "noise control") — the job that turns waiting digest
/// entries and opted-in weekly summaries into durable deliveries.
///
/// <para>
/// The behaviour named as mattering most: a digest window that passes without this running does not
/// lose what it was going to say (<see cref="A_digest_window_that_passed_without_the_job_running_still_says_everything_once_it_runs"/>)
/// — there is no age check anywhere in <c>RunDigestAsync</c>, so "the job did not run for a week" and
/// "the job ran on schedule" produce the same eventual outcome: everything still pending gets folded
/// in, just later.
/// </para>
/// </summary>
public class NotificationDigestRunnerTests
{
    private static (NotificationDigestRunner Runner, HarboraDbContext Db) Build(ISystemClock? clock = null)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("digest-" + Guid.NewGuid()).Options);
        var jobQueue = new DatabaseJobQueue(
            db, clock ?? new FixedClock(), new JobCancellationRegistry(), new JobSignal());
        var runner = new NotificationDigestRunner(
            db, new PassthroughProtector(), jobQueue, clock ?? new FixedClock(), new NotificationTemplateCatalog());
        return (runner, db);
    }

    private static User AddUser(HarboraDbContext db, string culture = "fa", bool weeklyOptIn = false, DateTimeOffset? lastReport = null)
    {
        var user = new User
        {
            Email = $"{Guid.NewGuid()}@example.com", DisplayName = "member", IsActive = true,
            PreferredCulture = culture, WeeklyReportOptIn = weeklyOptIn, LastWeeklyReportAt = lastReport
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static NotificationDigestEntry Entry(Guid userId, Guid workspaceId, string title, DateTimeOffset? createdAt = null) =>
        new()
        {
            UserId = userId, WorkspaceId = workspaceId, EventType = AlertEvent.ThresholdBreached,
            Severity = AlertSeverity.Warning, Title = title, Body = title + " body",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };

    // ---- the digest ------------------------------------------------------------------------

    [Fact]
    public async Task Several_pending_entries_for_one_user_fold_into_one_delivery()
    {
        var (runner, db) = Build();
        var user = AddUser(db);
        var ws = Guid.NewGuid();
        db.NotificationDigestEntries.AddRange(
            Entry(user.Id, ws, "first"), Entry(user.Id, ws, "second"), Entry(user.Id, ws, "third"));
        await db.SaveChangesAsync();

        await runner.RunDigestAsync(default);

        db.NotificationDeliveries.Should().ContainSingle().Which.Purpose.Should().Be(NotificationDeliveryPurpose.PersonalDigest);
        db.NotificationDigestEntries.Should().AllSatisfy(e => e.DeliveryId.Should().NotBeNull());
        db.NotificationDigestEntries.Select(e => e.DeliveryId).Distinct().Should().ContainSingle(
            "all three entries were folded into the same one delivery");
        db.Jobs.Should().ContainSingle().Which.Kind.Should().Be(JobKind.NotificationDelivery);
    }

    [Fact]
    public async Task Two_different_users_get_two_separate_deliveries()
    {
        var (runner, db) = Build();
        var alice = AddUser(db);
        var bob = AddUser(db);
        var ws = Guid.NewGuid();
        db.NotificationDigestEntries.AddRange(Entry(alice.Id, ws, "for alice"), Entry(bob.Id, ws, "for bob"));
        await db.SaveChangesAsync();

        await runner.RunDigestAsync(default);

        db.NotificationDeliveries.Should().HaveCount(2);
        db.NotificationDeliveries.Select(d => d.RecipientAddress).Should().BeEquivalentTo([alice.Email, bob.Email]);
    }

    [Fact]
    public async Task A_digest_window_that_passed_without_the_job_running_still_says_everything_once_it_runs()
    {
        var (runner, db) = Build();
        var user = AddUser(db);
        var ws = Guid.NewGuid();
        // Three entries that would have belonged to three separate hourly windows if the job had run
        // on schedule — none of them ever got flushed, so all three are still here, however old.
        db.NotificationDigestEntries.AddRange(
            Entry(user.Id, ws, "hour one", DateTimeOffset.UtcNow.AddDays(-3)),
            Entry(user.Id, ws, "hour two", DateTimeOffset.UtcNow.AddDays(-2)),
            Entry(user.Id, ws, "hour three", DateTimeOffset.UtcNow.AddHours(-1)));
        await db.SaveChangesAsync();

        await runner.RunDigestAsync(default);

        db.NotificationDigestEntries.Should().AllSatisfy(e => e.DeliveryId.Should().NotBeNull(
            "nothing about how late this ran means an entry is dropped rather than folded in"));
        var delivery = db.NotificationDeliveries.Should().ContainSingle().Which;
        var text = DecodeText(delivery);
        text.Should().Contain("hour one").And.Contain("hour two").And.Contain("hour three");
    }

    [Fact]
    public async Task An_already_flushed_entry_is_never_folded_into_a_second_delivery()
    {
        var (runner, db) = Build();
        var user = AddUser(db);
        var ws = Guid.NewGuid();
        var alreadyFlushed = Entry(user.Id, ws, "already sent");
        alreadyFlushed.DeliveryId = Guid.NewGuid();
        db.NotificationDigestEntries.Add(alreadyFlushed);
        await db.SaveChangesAsync();

        await runner.RunDigestAsync(default);

        db.NotificationDeliveries.Should().BeEmpty("the one entry present was already folded into a delivery");
    }

    [Fact]
    public async Task Nothing_pending_is_a_quiet_no_op()
    {
        var (runner, db) = Build();

        var act = async () => await runner.RunDigestAsync(default);

        await act.Should().NotThrowAsync();
        db.NotificationDeliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_entry_for_a_deleted_user_is_left_pending_rather_than_crashing_the_pass()
    {
        var (runner, db) = Build();
        var ws = Guid.NewGuid();
        db.NotificationDigestEntries.Add(Entry(Guid.NewGuid() /* no such user */, ws, "orphaned"));
        await db.SaveChangesAsync();

        var act = async () => await runner.RunDigestAsync(default);

        await act.Should().NotThrowAsync();
        db.NotificationDigestEntries.Should().ContainSingle().Which.DeliveryId.Should().BeNull();
    }

    // ---- the weekly report ------------------------------------------------------------------

    [Fact]
    public async Task An_opted_in_user_who_has_never_had_a_report_gets_one()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (runner, db) = Build(clock);
        var user = AddUser(db, weeklyOptIn: true, lastReport: null);

        await runner.RunWeeklyReportAsync(default);

        db.NotificationDeliveries.Should().ContainSingle().Which.Purpose.Should().Be(NotificationDeliveryPurpose.WeeklyReport);
        db.Users.Single(u => u.Id == user.Id).LastWeeklyReportAt.Should().Be(clock.UtcNow);
    }

    [Fact]
    public async Task A_user_who_never_opted_in_gets_no_report()
    {
        var (runner, db) = Build();
        AddUser(db, weeklyOptIn: false);

        await runner.RunWeeklyReportAsync(default);

        db.NotificationDeliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_opted_in_user_reported_less_than_a_week_ago_waits()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (runner, db) = Build(clock);
        AddUser(db, weeklyOptIn: true, lastReport: clock.UtcNow.AddDays(-3));

        await runner.RunWeeklyReportAsync(default);

        db.NotificationDeliveries.Should().BeEmpty();
    }

    [Fact]
    public async Task An_opted_in_user_reported_exactly_a_week_ago_is_due_again()
    {
        // The boundary itself: LastWeeklyReportAt sits exactly seven days back, not a day further —
        // "at least a week" has to include the instant the week completes, not only the day after.
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (runner, db) = Build(clock);
        AddUser(db, weeklyOptIn: true, lastReport: clock.UtcNow.AddDays(-7));

        await runner.RunWeeklyReportAsync(default);

        db.NotificationDeliveries.Should().ContainSingle();
    }

    [Fact]
    public async Task The_report_counts_this_persons_own_notifications_by_severity_over_the_period()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (runner, db) = Build(clock);
        var user = AddUser(db, weeklyOptIn: true, lastReport: clock.UtcNow.AddDays(-7));
        var ws = Guid.NewGuid();

        db.UserNotifications.AddRange(
            new UserNotification { UserId = user.Id, WorkspaceId = ws, Severity = AlertSeverity.Critical, Title = "c", Body = "c", CreatedAt = clock.UtcNow.AddDays(-2) },
            new UserNotification { UserId = user.Id, WorkspaceId = ws, Severity = AlertSeverity.Warning, Title = "w1", Body = "w", CreatedAt = clock.UtcNow.AddDays(-3) },
            new UserNotification { UserId = user.Id, WorkspaceId = ws, Severity = AlertSeverity.Warning, Title = "w2", Body = "w", CreatedAt = clock.UtcNow.AddDays(-1) },
            new UserNotification { UserId = user.Id, WorkspaceId = ws, Severity = AlertSeverity.Info, Title = "i", Body = "i", CreatedAt = clock.UtcNow.AddDays(-4) },
            // Outside the window (older than the last report) and belonging to somebody else — neither
            // should be counted.
            new UserNotification { UserId = user.Id, WorkspaceId = ws, Severity = AlertSeverity.Critical, Title = "too old", Body = "x", CreatedAt = clock.UtcNow.AddDays(-30) },
            new UserNotification { UserId = Guid.NewGuid(), WorkspaceId = ws, Severity = AlertSeverity.Critical, Title = "not mine", Body = "x", CreatedAt = clock.UtcNow.AddDays(-1) });
        await db.SaveChangesAsync();

        await runner.RunWeeklyReportAsync(default);

        var delivery = db.NotificationDeliveries.Should().ContainSingle().Which;
        var text = DecodeText(delivery);
        text.Should().Contain("1").And.Contain("2"); // 1 critical, 2 warning, 1 info within the window
    }

    /// <summary>Undoes <see cref="PassthroughProtector"/>'s own nonce suffix and N4's ChannelBody
    /// envelope, so a test can read the words a delivery actually carries.</summary>
    private static string DecodeText(NotificationDelivery delivery) =>
        ChannelBody.Decode(new PassthroughProtector().Unprotect(delivery.EncryptedBody)).Text;

    [Fact]
    public async Task A_quiet_week_still_gets_a_report_saying_so()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        var (runner, db) = Build(clock);
        AddUser(db, weeklyOptIn: true, lastReport: clock.UtcNow.AddDays(-8));

        await runner.RunWeeklyReportAsync(default);

        db.NotificationDeliveries.Should().ContainSingle(
            "silence is itself the report — an opted-in user hears from this every seven days, unconditionally");
    }
}
