using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Monitoring;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="AlertDedup"/> is the persisted replacement for the in-memory <c>AlertThrottle</c> (N2,
/// 2026-08-16 notification-system spec). The property that matters and that
/// <c>AlertThrottle</c> could never have — a mark written by one process is seen by the next — is the
/// headline of this file: <see cref="A_key_marked_by_one_process_is_seen_by_a_brand_new_one"/> builds
/// two independent <see cref="HarboraDbContext"/> instances against the same store, the same way a
/// restarted panel opens a fresh context against the same database.
/// </summary>
public class AlertDedupTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    private static HarboraDbContext NewDb(string name) => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(name).Options);

    [Fact]
    public async Task The_first_occurrence_of_a_key_fires()
    {
        await using var db = NewDb("dedup-" + Guid.NewGuid());
        var dedup = new AlertDedup(db);

        (await dedup.ShouldFireAsync("ssl:example.com:2026-08-16", Now, default)).Should().BeTrue();
    }

    [Fact]
    public async Task A_repeat_of_the_same_key_is_suppressed()
    {
        await using var db = NewDb("dedup-" + Guid.NewGuid());
        var dedup = new AlertDedup(db);

        await dedup.ShouldFireAsync("ssl:example.com:2026-08-16", Now, default);

        (await dedup.ShouldFireAsync("ssl:example.com:2026-08-16", Now.AddHours(1), default)).Should().BeFalse(
            "the window is baked into the key, and this call asked with the exact same one");
    }

    [Fact]
    public async Task One_subject_does_not_silence_another()
    {
        // The whole point AlertThrottle's own doc comment made about disk applies here too: a full
        // disk on one node, or an expiring certificate on one host, must not hide the next one.
        await using var db = NewDb("dedup-" + Guid.NewGuid());
        var dedup = new AlertDedup(db);

        (await dedup.ShouldFireAsync("disk:node-a:100", Now, default)).Should().BeTrue();
        (await dedup.ShouldFireAsync("disk:node-b:100", Now, default)).Should().BeTrue();
    }

    [Fact]
    public async Task A_new_window_is_simply_a_new_key_and_fires_again()
    {
        await using var db = NewDb("dedup-" + Guid.NewGuid());
        var dedup = new AlertDedup(db);

        await dedup.ShouldFireAsync("ssl:example.com:2026-08-16", Now, default);

        (await dedup.ShouldFireAsync("ssl:example.com:2026-08-17", Now.AddDays(1), default)).Should().BeTrue(
            "the next day's check asks with tomorrow's key, which this table has never seen");
    }

    /// <summary>
    /// The defect this sub-project exists to close. <c>AlertThrottle</c> was a singleton dictionary:
    /// rebuilding the process rebuilt an empty one, and a panel bounced twice in a day sent the SSL
    /// warning twice — doc 09 §6's "at most one per host per day" failing on exactly the case it
    /// names. Two independent <see cref="HarboraDbContext"/> instances against the same in-memory
    /// database name stand in for "before" and "after" a restart: nothing in this test shares an
    /// object between them except the store itself.
    /// </summary>
    [Fact]
    public async Task A_key_marked_by_one_process_is_seen_by_a_brand_new_one()
    {
        var storeName = "dedup-restart-" + Guid.NewGuid();

        await using (var beforeRestart = NewDb(storeName))
        {
            var firstProcess = new AlertDedup(beforeRestart);
            (await firstProcess.ShouldFireAsync("ssl:example.com:2026-08-16", Now, default)).Should().BeTrue();
        }
        // beforeRestart is disposed here — nothing about it survives except what it wrote to the store.

        await using var afterRestart = NewDb(storeName);
        var secondProcess = new AlertDedup(afterRestart);

        (await secondProcess.ShouldFireAsync("ssl:example.com:2026-08-16", Now.AddHours(6), default))
            .Should().BeFalse("the mark from before the restart is still there — that is the whole point");
    }

    /// <summary>
    /// A raced insert — two callers both find no mark and both try to write one — must lose gracefully
    /// rather than throw, and must not disturb whatever else the caller's own context was holding.
    /// <see cref="MetricsCollector"/> and <c>CertificateWatcher</c> both share one context across a
    /// whole tick's worth of other writes, so <c>ShouldFireAsync</c> clearing the entire change
    /// tracker on a lost race would silently drop that other work — this is what pins it not doing so.
    /// </summary>
    [Fact]
    public async Task A_raced_insert_is_suppressed_without_disturbing_other_pending_work()
    {
        await using var db = new RejectingContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dedup-race-" + Guid.NewGuid()).Options);
        var dedup = new AlertDedup(db);

        // Stands in for "whatever else this tick's context was holding" — an unrelated change the
        // dedup check must not know or care about.
        var otherPendingWork = new Server { Name = "node-1" };
        db.Servers.Add(otherPendingWork);

        db.RejectTheNextInsertWith = UniqueViolation();

        (await dedup.ShouldFireAsync("disk:node-1:100", Now, default)).Should().BeFalse(
            "another pass won the race — this one is simply late, not wrong");

        db.Entry(otherPendingWork).State.Should().Be(EntityState.Added,
            "the caller's own unsaved work must survive a lost dedup race");

        // The caller's own SaveChangesAsync, later in the same tick — must succeed, and must not
        // retry the losing insert (which would fail identically forever).
        (await db.SaveChangesAsync(default)).Should().Be(1);
        db.AlertDedupMarks.Local.Should().BeEmpty(
            "the detached mark must not have been re-added to the next save");
    }

    private static DbUpdateException UniqueViolation() =>
        new("An error occurred while saving the entity changes.",
            new PostgresException(
                "duplicate key value violates unique constraint \"IX_AlertDedupMarks_Key\"",
                "ERROR", "ERROR", PostgresErrorCodes.UniqueViolation));

    /// <summary>Rejects exactly the next <c>SaveChangesAsync</c> call — the in-memory provider cannot
    /// enforce a unique index itself, so this stands in for the database refusing the insert.</summary>
    private sealed class RejectingContext(DbContextOptions<HarboraDbContext> options) : HarboraDbContext(options)
    {
        public DbUpdateException? RejectTheNextInsertWith { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (RejectTheNextInsertWith is not { } refusal) return base.SaveChangesAsync(cancellationToken);

            RejectTheNextInsertWith = null;
            throw refusal;
        }
    }
}

/// <summary>
/// <see cref="AlertDedupWindow.Bucket"/> is pure — the piece of the mechanism that turns "how often"
/// into "which key" — and is exercised on its own rather than only through <see cref="AlertDedup"/>.
/// </summary>
public class AlertDedupWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Two_instants_in_the_same_window_share_a_bucket()
        => AlertDedupWindow.Bucket(Now, TimeSpan.FromHours(1))
            .Should().Be(AlertDedupWindow.Bucket(Now.AddMinutes(59), TimeSpan.FromHours(1)));

    [Fact]
    public void Two_instants_a_full_window_apart_land_in_different_buckets()
        => AlertDedupWindow.Bucket(Now, TimeSpan.FromHours(1))
            .Should().NotBe(AlertDedupWindow.Bucket(Now.AddHours(1), TimeSpan.FromHours(1)));

    [Fact]
    public void The_bucket_depends_only_on_the_instant_and_the_window_not_on_call_order()
    {
        // The property AlertDedup depends on: two independent processes asking about the same instant
        // must compute the identical bucket without ever having talked to each other.
        var a = AlertDedupWindow.Bucket(Now, TimeSpan.FromMinutes(5));
        var b = AlertDedupWindow.Bucket(Now, TimeSpan.FromMinutes(5));

        a.Should().Be(b);
    }

    [Fact]
    public void A_shorter_configured_window_buckets_more_finely()
    {
        // Ten minutes apart: the same bucket at the shipped one-hour default, different buckets once
        // the interval is configured down to five minutes — MonitoringOptions.DiskAlertIntervalHours
        // stays a real knob under the new mechanism, not merely a number nobody reads any more.
        var hourly = TimeSpan.FromHours(1);
        var fiveMinutes = TimeSpan.FromMinutes(5);

        AlertDedupWindow.Bucket(Now, hourly).Should().Be(AlertDedupWindow.Bucket(Now.AddMinutes(10), hourly));
        AlertDedupWindow.Bucket(Now, fiveMinutes).Should().NotBe(AlertDedupWindow.Bucket(Now.AddMinutes(10), fiveMinutes));
    }

    [Fact]
    public void A_zero_or_negative_window_has_no_bucket()
    {
        var zero = () => AlertDedupWindow.Bucket(Now, TimeSpan.Zero);
        var negative = () => AlertDedupWindow.Bucket(Now, TimeSpan.FromMinutes(-1));

        zero.Should().Throw<ArgumentOutOfRangeException>();
        negative.Should().Throw<ArgumentOutOfRangeException>();
    }
}
