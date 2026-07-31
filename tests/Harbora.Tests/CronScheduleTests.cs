using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// When a scheduled job actually runs.
///
/// A scheduler that drifts, double-fires, or never fires is the kind of bug nobody notices until a
/// nightly backup has been dead for a month. So the next-occurrence calculation is pinned exactly,
/// including the two rules people get wrong: "strictly after" (or a job due at 03:00 fires repeatedly
/// through that minute) and cron's or-rule when both day fields are restricted.
/// </summary>
public class CronScheduleTests
{
    private static CronSchedule Parse(string expression)
    {
        CronSchedule.TryParse(expression, out var schedule, out var error).Should().BeTrue(error);
        return schedule!;
    }

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    [Fact]
    public void Every_day_at_three_finds_the_next_three()
    {
        var next = Parse("0 3 * * *").NextOccurrence(At(2026, 7, 31, 10, 0));

        next.Should().Be(At(2026, 8, 1, 3, 0));
    }

    [Fact]
    public void The_next_occurrence_is_strictly_after_the_moment_asked_about()
    {
        // The rule that stops a job firing again and again for the whole minute it is due.
        var next = Parse("0 3 * * *").NextOccurrence(At(2026, 7, 31, 3, 0));

        next.Should().Be(At(2026, 8, 1, 3, 0));
    }

    [Fact]
    public void A_step_expression_lands_on_the_step_boundaries()
    {
        var schedule = Parse("*/15 * * * *");

        schedule.NextOccurrence(At(2026, 7, 31, 10, 0)).Should().Be(At(2026, 7, 31, 10, 15));
        schedule.NextOccurrence(At(2026, 7, 31, 10, 16)).Should().Be(At(2026, 7, 31, 10, 30));
        schedule.NextOccurrence(At(2026, 7, 31, 10, 59)).Should().Be(At(2026, 7, 31, 11, 0));
    }

    [Fact]
    public void A_list_and_a_range_both_work()
    {
        Parse("0 9,17 * * *").NextOccurrence(At(2026, 7, 31, 10, 0)).Should().Be(At(2026, 7, 31, 17, 0));
        Parse("30 8 * * 1-5").NextOccurrence(At(2026, 8, 1, 0, 0)).Should().Be(At(2026, 8, 3, 8, 30));
    }

    [Fact]
    public void Sunday_may_be_written_as_seven()
    {
        // Common in copied expressions. Rejecting it, or treating 7 as out of range, would produce a
        // schedule that silently never runs.
        var next = Parse("0 4 * * 7").NextOccurrence(At(2026, 7, 31, 0, 0));

        next!.Value.DayOfWeek.Should().Be(DayOfWeek.Sunday);
        next.Value.Hour.Should().Be(4);
    }

    [Fact]
    public void When_both_day_fields_are_restricted_either_one_matching_is_enough()
    {
        // Standard cron's odd rule, kept deliberately: quietly differing would make expressions
        // copied from elsewhere run on the wrong days. 1 August 2026 is a Saturday.
        var schedule = Parse("0 0 1 * 3");   // the 1st, OR any Wednesday

        var next = schedule.NextOccurrence(At(2026, 7, 30, 12, 0));

        next.Should().Be(At(2026, 8, 1, 0, 0), "the 1st matches even though it is not a Wednesday");
    }

    [Fact]
    public void When_only_one_day_field_is_restricted_it_alone_decides()
    {
        var schedule = Parse("0 0 15 * *");

        schedule.NextOccurrence(At(2026, 7, 31, 0, 0)).Should().Be(At(2026, 8, 15, 0, 0));
    }

    [Fact]
    public void A_month_restriction_skips_whole_months()
    {
        Parse("0 0 1 1 *").NextOccurrence(At(2026, 7, 31, 0, 0)).Should().Be(At(2027, 1, 1, 0, 0));
    }

    [Fact]
    public void The_29th_of_february_finds_the_next_leap_year()
    {
        Parse("0 0 29 2 *").NextOccurrence(At(2026, 7, 31, 0, 0)).Should().Be(At(2028, 2, 29, 0, 0));
    }

    [Fact]
    public void An_impossible_schedule_answers_never_and_answers_quickly()
    {
        // 31 February. Answering "never" is the honest result; spinning is not — and the scheduler
        // asks this question for every cron service on every tick, so how long it takes is part of
        // the behaviour, not an implementation detail.
        var started = System.Diagnostics.Stopwatch.StartNew();
        var next = Parse("0 0 31 2 *").NextOccurrence(At(2026, 7, 31, 0, 0));
        started.Stop();

        next.Should().BeNull();
        started.ElapsedMilliseconds.Should().BeLessThan(250, "the search is bounded to a few years");
    }

    [Theory]
    [InlineData("", "Enter a schedule")]
    [InlineData("0 3 * *", "five fields")]
    [InlineData("0 3 * * * *", "five fields")]
    [InlineData("60 3 * * *", "0–59")]
    [InlineData("0 24 * * *", "0–23")]
    [InlineData("0 3 32 * *", "1–31")]
    [InlineData("0 3 * 13 *", "1–12")]
    [InlineData("@daily", "five fields")]
    [InlineData("0 3 * * abc", "not a valid")]
    [InlineData("*/0 * * * *", "not a valid step")]
    public void A_schedule_that_cannot_be_understood_is_refused_with_a_reason(string expression, string expected)
    {
        // Refused where someone types it, rather than interpreted into something they did not mean.
        CronSchedule.TryParse(expression, out var schedule, out var error).Should().BeFalse();
        schedule.Should().BeNull();
        error.Should().Contain(expected);
    }

    [Fact]
    public void A_valid_schedule_reports_no_error()
    {
        CronSchedule.TryParse("*/5 * * * *", out var schedule, out var error).Should().BeTrue();
        schedule.Should().NotBeNull();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("*/10 * * * *", "Every 10 minutes")]
    [InlineData("0 3 * * *", "03:00")]
    [InlineData("15 * * * *", "15 minutes past")]
    public void The_expression_is_read_back_in_plain_language(string expression, string expected)
    {
        // So someone can check it means what they meant before a job runs at the wrong time for a week.
        CronSchedule.Describe(expression).Should().Contain(expected);
    }
}
