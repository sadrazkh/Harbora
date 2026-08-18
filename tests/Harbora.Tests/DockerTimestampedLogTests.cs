using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Parsing the per-line stamp Docker attaches when a log snapshot is requested with
/// <c>Timestamps: true</c> — the thing a time-window search needs and nothing before it ever asked
/// Docker for. <see cref="LogFilterTests"/> covers the untimed reader; this covers the timed one.
/// </summary>
public class DockerTimestampedLogTests
{
    [Fact]
    public void A_stamped_line_splits_into_its_moment_and_its_text()
    {
        var lines = DockerTimestampedLog.Parse("2026-08-18T10:23:01.123456789Z Starting up");

        lines.Should().ContainSingle();
        lines[0].Text.Should().Be("Starting up");
        lines[0].Timestamp.Should().Be(DateTimeOffset.Parse("2026-08-18T10:23:01.123456789Z"));
    }

    [Fact]
    public void Every_line_of_a_multi_line_snapshot_keeps_its_own_moment()
    {
        var raw = "2026-08-18T10:00:00.000000000Z first\n2026-08-18T10:00:05.000000000Z second";

        var lines = DockerTimestampedLog.Parse(raw);

        lines.Should().HaveCount(2);
        lines[0].Text.Should().Be("first");
        lines[1].Text.Should().Be("second");
        lines[1].Timestamp.Should().BeAfter(lines[0].Timestamp);
    }

    [Fact]
    public void A_line_with_no_stamp_of_its_own_stays_attached_to_the_line_before_it()
    {
        // A stack trace's continuation lines are still separately timestamped by Docker in practice,
        // but the parser must not lose output on a line that somehow was not — it belongs with
        // whatever came just before it, not dropped and not given a false moment of its own.
        var raw = "2026-08-18T10:00:00.000000000Z ERROR boom\n    at Shop.Data.Connect()";

        var lines = DockerTimestampedLog.Parse(raw);

        lines.Should().ContainSingle();
        lines[0].Text.Should().Be("ERROR boom\n    at Shop.Data.Connect()");
    }

    [Fact]
    public void A_leading_line_with_no_stamp_is_dropped_rather_than_given_a_false_moment()
    {
        var lines = DockerTimestampedLog.Parse("not a timestamp at all, no colon");

        lines.Should().BeEmpty();
    }

    [Fact]
    public void Blank_lines_are_skipped()
    {
        var raw = "2026-08-18T10:00:00.000000000Z first\n\n2026-08-18T10:00:01.000000000Z second";

        DockerTimestampedLog.Parse(raw).Should().HaveCount(2);
    }

    [Fact]
    public void Nothing_in_gives_nothing_out()
    {
        DockerTimestampedLog.Parse(null).Should().BeEmpty();
        DockerTimestampedLog.Parse("").Should().BeEmpty();
    }

    [Fact]
    public void Windows_style_line_endings_are_handled_like_unix_ones()
    {
        var raw = "2026-08-18T10:00:00.000000000Z first\r\n2026-08-18T10:00:01.000000000Z second";

        DockerTimestampedLog.Parse(raw).Should().HaveCount(2);
    }
}
