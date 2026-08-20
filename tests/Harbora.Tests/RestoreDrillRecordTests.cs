using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.DisasterRecovery;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The pure persistence half of sub-project 12's drill record — no Docker, no PostgreSQL, no
/// shelling out to <c>deploy/restore-drill.sh</c> (that side is
/// <see cref="RestoreDrillScriptTests"/>). This is what <c>harbora record-drill-result</c> calls,
/// and what <c>AdminSettingsController</c> reads for the "last drill" panel.
/// </summary>
public class RestoreDrillRecordTests
{
    private static HarboraDbContext NewDb() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("restore-drill-" + Guid.NewGuid()).Options);

    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    // ---- writing ----

    [Fact]
    public async Task A_pass_verdict_is_written_and_reads_back()
    {
        await using var db = NewDb();

        var result = await RestoreDrillRecord.WriteAsync(db, "pass", "5 migrations, 3 workspaces", Now);

        result.Success.Should().BeTrue();
        result.Verdict.Should().Be("pass");

        var status = await RestoreDrillRecord.ReadAsync(db, Now);
        status.HasRun.Should().BeTrue();
        status.Verdict.Should().Be("pass");
        status.At.Should().Be(Now);
        status.Detail.Should().Be("5 migrations, 3 workspaces");
        status.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task A_fail_verdict_is_written_and_reads_back()
    {
        await using var db = NewDb();

        await RestoreDrillRecord.WriteAsync(db, "fail", "no backup found", Now);

        var status = await RestoreDrillRecord.ReadAsync(db, Now);
        status.Verdict.Should().Be("fail");
        status.Detail.Should().Be("no backup found");
    }

    [Theory]
    [InlineData("PASS", "pass")]
    [InlineData("Pass", "pass")]
    [InlineData("  pass  ", "pass")]
    [InlineData("FAIL", "fail")]
    public async Task The_verdict_is_normalized_case_and_whitespace_insensitively(string input, string expected)
    {
        await using var db = NewDb();

        var result = await RestoreDrillRecord.WriteAsync(db, input, null, Now);

        result.Success.Should().BeTrue();
        result.Verdict.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("maybe")]
    [InlineData("passed")]
    [InlineData("0")]
    public async Task An_unrecognised_verdict_is_rejected_and_nothing_is_written(string? bogus)
    {
        // A typo in the --verdict argument must be loud on the command line — the whole point of a
        // closed vocabulary here is that "whatever the script happened to pass" can never become a
        // silently-accepted third state next to pass and fail.
        await using var db = NewDb();

        var result = await RestoreDrillRecord.WriteAsync(db, bogus, "irrelevant", Now);

        result.Success.Should().BeFalse();
        result.Error.Should().NotBeNullOrWhiteSpace();
        (await db.Settings.IgnoreQueryFilters().AnyAsync(s => s.Key == SettingKeys.DrLastDrillAt)).Should().BeFalse();

        var status = await RestoreDrillRecord.ReadAsync(db, Now);
        status.HasRun.Should().BeFalse("a rejected write must not fabricate a recorded drill");
    }

    [Fact]
    public async Task A_second_drill_overwrites_the_first_rather_than_accumulating_rows()
    {
        await using var db = NewDb();

        await RestoreDrillRecord.WriteAsync(db, "fail", "first attempt failed", Now);
        await RestoreDrillRecord.WriteAsync(db, "pass", "second attempt passed", Now.AddDays(1));

        var status = await RestoreDrillRecord.ReadAsync(db, Now.AddDays(1));
        status.Verdict.Should().Be("pass");
        status.Detail.Should().Be("second attempt passed");
        status.At.Should().Be(Now.AddDays(1));

        (await db.Settings.IgnoreQueryFilters().CountAsync(s => s.Key == SettingKeys.DrLastDrillVerdict))
            .Should().Be(1, "the second drill's result replaces the first, it does not sit beside it");
    }

    [Fact]
    public async Task An_overlong_detail_is_capped_rather_than_rejected()
    {
        await using var db = NewDb();
        var huge = new string('x', RestoreDrillRecord.MaxDetailLength + 500);

        var result = await RestoreDrillRecord.WriteAsync(db, "fail", huge, Now);

        result.Success.Should().BeTrue();
        var status = await RestoreDrillRecord.ReadAsync(db, Now);
        status.Detail!.Length.Should().Be(RestoreDrillRecord.MaxDetailLength);
    }

    [Fact]
    public async Task A_null_detail_is_stored_as_no_detail_rather_than_the_literal_word_null()
    {
        await using var db = NewDb();

        await RestoreDrillRecord.WriteAsync(db, "pass", null, Now);

        var status = await RestoreDrillRecord.ReadAsync(db, Now);
        status.Detail.Should().BeNull();
    }

    // ---- reading: the honest "never run" state ----

    [Fact]
    public async Task An_empty_database_reads_as_never_run_rather_than_throwing_or_faking_a_verdict()
    {
        await using var db = NewDb();

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.Should().Be(RestoreDrillStatus.NeverRun);
        status.HasRun.Should().BeFalse();
        status.Verdict.Should().BeNull();
        status.At.Should().BeNull();
        status.IsStale.Should().BeFalse("staleness is meaningless with nothing to be stale");
    }

    [Fact]
    public async Task A_corrupted_verdict_setting_reads_as_never_run_rather_than_crashing_or_showing_garbage()
    {
        // Not reachable through WriteAsync's own validation, but Settings is a bare key/value table
        // an operator could still hand-edit — the reader must not trust its own writer blindly.
        await using var db = NewDb();
        db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillAt, Value = Now.ToString("O") });
        db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillVerdict, Value = "banana" });
        await db.SaveChangesAsync();

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.HasRun.Should().BeFalse();
    }

    [Fact]
    public async Task An_unparseable_timestamp_reads_as_never_run()
    {
        await using var db = NewDb();
        db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillAt, Value = "not-a-date" });
        db.Settings.Add(new Setting { Key = SettingKeys.DrLastDrillVerdict, Value = "pass" });
        await db.SaveChangesAsync();

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.HasRun.Should().BeFalse();
    }

    // ---- staleness ----

    [Fact]
    public async Task A_drill_from_29_days_ago_is_not_stale()
    {
        await using var db = NewDb();
        await RestoreDrillRecord.WriteAsync(db, "pass", null, Now.AddDays(-29));

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.IsStale.Should().BeFalse();
    }

    [Fact]
    public async Task A_drill_from_31_days_ago_is_stale()
    {
        await using var db = NewDb();
        await RestoreDrillRecord.WriteAsync(db, "pass", null, Now.AddDays(-31));

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task A_failing_drill_can_still_be_stale_independently_of_its_verdict()
    {
        // Staleness answers "when did we last check", not "did the last check go well" — a FAIL from
        // 40 days ago is both a failed drill and a stale one, and the page must say both.
        await using var db = NewDb();
        await RestoreDrillRecord.WriteAsync(db, "fail", "old failure", Now.AddDays(-40));

        var status = await RestoreDrillRecord.ReadAsync(db, Now);

        status.Verdict.Should().Be("fail");
        status.IsStale.Should().BeTrue();
    }
}
