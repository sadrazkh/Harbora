using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Finding the one line that explains an outage.
///
/// The logs screen showed a tail and nothing else. That is fine until something goes wrong, and then
/// the line that matters is somewhere in four hundred — and the browser's own find only searches
/// what happens to be rendered.
/// </summary>
public class LogFilterTests
{
    private const string Sample = """
        2026-07-31 10:00:01 Starting up
        2026-07-31 10:00:02 Listening on 8080
        2026-07-31 10:00:09 ERROR could not connect to database
            at Shop.Data.Connect()
            at Shop.Program.Main()
        2026-07-31 10:00:10 Retrying
        2026-07-31 10:00:12 WARN slow query 2400ms
        """;

    [Fact]
    public void A_search_keeps_only_the_lines_that_contain_it()
    {
        var lines = LogFilter.Apply(Sample, "retrying", onlyProblems: false);

        lines.Should().ContainSingle().Which.Should().Contain("Retrying");
    }

    [Fact]
    public void Searching_ignores_case_because_nobody_knows_how_the_app_writes_it()
    {
        LogFilter.Apply(Sample, "ERROR", onlyProblems: false).Should().NotBeEmpty();
        LogFilter.Apply(Sample, "error", onlyProblems: false).Should().NotBeEmpty();
    }

    [Fact]
    public void A_stack_trace_stays_with_the_line_that_introduced_it()
    {
        // A message without its trace explains nothing, and the trace on its own does not match any
        // search someone would think to type.
        var lines = LogFilter.Apply(Sample, "could not connect", onlyProblems: false);

        lines.Should().HaveCount(3);
        lines[0].Should().Contain("ERROR");
        lines[1].Should().Contain("Shop.Data.Connect");
        lines[2].Should().Contain("Shop.Program.Main");
    }

    [Fact]
    public void Only_problems_finds_the_usual_words_whatever_the_app_calls_itself()
    {
        var lines = LogFilter.Apply(Sample, search: null, onlyProblems: true);

        lines.Should().Contain(l => l.Contains("ERROR"));
        lines.Should().Contain(l => l.Contains("WARN"));
        lines.Should().NotContain(l => l.Contains("Listening on"));
    }

    [Fact]
    public void Only_problems_keeps_the_trace_under_a_problem_line()
    {
        var lines = LogFilter.Apply(Sample, search: null, onlyProblems: true);

        lines.Should().Contain(l => l.Contains("Shop.Data.Connect"));
    }

    [Fact]
    public void A_search_and_the_problems_filter_apply_together()
    {
        // Both narrow: neither one is allowed to widen the result.
        var lines = LogFilter.Apply(Sample, "database", onlyProblems: true);

        lines.Should().Contain(l => l.Contains("could not connect to database"));
        lines.Should().NotContain(l => l.Contains("slow query"));
    }

    [Fact]
    public void No_filter_at_all_returns_every_line_it_was_given()
    {
        var lines = LogFilter.Apply(Sample, search: null, onlyProblems: false);

        lines.Should().HaveCount(7);
    }

    [Fact]
    public void A_search_that_matches_nothing_returns_nothing_rather_than_everything()
    {
        // The failure mode that makes a filter untrustworthy: quietly falling back to no filter.
        LogFilter.Apply(Sample, "zzzz-not-here", onlyProblems: false).Should().BeEmpty();
    }

    [Theory]
    [InlineData("2026-07-31 ERROR boom", true)]
    [InlineData("FATAL: out of memory", true)]
    [InlineData("Unhandled exception", true)]
    [InlineData("panic: runtime error", true)]
    [InlineData("GET /health 200", false)]
    [InlineData("terror management is fine", false)]
    public void The_words_that_mark_a_problem_do_not_catch_ordinary_lines(string line, bool expected)
    {
        // "terror" contains "error"; a filter that fires on it teaches people to stop using it.
        LogFilter.IsProblem(line).Should().Be(expected);
    }

    [Fact]
    public void Blank_lines_do_not_drag_unrelated_output_along()
    {
        // A blank line ends a trace. Without that, everything after one matching line would follow it.
        var text = "ERROR first\n    at frame\n\n    stray indented line\nplain line";

        var lines = LogFilter.Apply(text, search: null, onlyProblems: true);

        lines.Should().NotContain(l => l.Contains("stray indented"));
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        LogFilter.Apply(null, "x", false).Should().BeEmpty();
        LogFilter.Apply("", "x", false).Should().BeEmpty();
    }

    [Fact]
    public void A_downloaded_file_is_named_for_its_app_and_moment()
    {
        // The second thing anyone does with a log file is send it to someone.
        var name = LogFilter.FileName("shop", new DateTimeOffset(2026, 7, 31, 14, 5, 9, TimeSpan.Zero));

        name.Should().Be("shop-logs-20260731-140509.txt");
    }

    // ---- ApplyTimed: the same rule, for lines that already carry their own moment ----

    private const string StampedSample = """
        2026-07-31T10:00:01.000000000Z Starting up
        2026-07-31T10:00:02.000000000Z Listening on 8080
        2026-07-31T10:00:09.000000000Z ERROR could not connect to database
        2026-07-31T10:00:09.100000000Z     at Shop.Data.Connect()
        2026-07-31T10:00:09.200000000Z     at Shop.Program.Main()
        2026-07-31T10:00:10.000000000Z Retrying
        2026-07-31T10:00:12.000000000Z WARN slow query 2400ms
        """;

    [Fact]
    public void A_timed_search_keeps_only_the_lines_that_contain_it_with_their_moments_intact()
    {
        var stamped = DockerTimestampedLog.Parse(StampedSample);

        var kept = LogFilter.ApplyTimed(stamped, "retrying", onlyProblems: false);

        kept.Should().ContainSingle();
        kept[0].Text.Should().Be("Retrying");
        kept[0].Timestamp.Should().Be(DateTimeOffset.Parse("2026-07-31T10:00:10.000000000Z"));
    }

    [Fact]
    public void A_timed_stack_trace_stays_with_the_line_that_introduced_it()
    {
        var stamped = DockerTimestampedLog.Parse(StampedSample);

        var kept = LogFilter.ApplyTimed(stamped, "could not connect", onlyProblems: false);

        kept.Should().HaveCount(3);
        kept[0].Text.Should().Contain("ERROR");
        kept[1].Text.Should().Contain("Shop.Data.Connect");
        kept[2].Text.Should().Contain("Shop.Program.Main");
    }

    [Fact]
    public void An_empty_timed_list_gives_nothing_out()
    {
        LogFilter.ApplyTimed([], "x", false).Should().BeEmpty();
    }

    [Fact]
    public void The_stamp_is_the_same_date_whatever_calendar_the_panel_is_running_in()
    {
        // Found on the live server, not here: the panel runs in Persian, so the ambient calendar
        // wrote a Jalali year — "14050509" for what everything else calls 2026-07-31. The test
        // runner's culture is invariant, which is exactly why the first version looked fine.
        var original = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fa-IR");

            LogFilter.FileName("shop", new DateTimeOffset(2026, 7, 31, 14, 5, 9, TimeSpan.Zero))
                .Should().Be("shop-logs-20260731-140509.txt");
        }
        finally
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
