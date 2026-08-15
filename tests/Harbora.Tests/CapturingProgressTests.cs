using FluentAssertions;
using Harbora.Infrastructure.Docker;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="CapturingProgress"/> is what the agent's <c>/agent/oneoff</c> endpoint uses to
/// collect a one-off's output for the JSON response instead of discarding it. These tests cover
/// what a real agent round trip cannot: the exact text it accumulates and what happens once that
/// text would grow past the bound.
/// </summary>
public class CapturingProgressTests
{
    [Fact]
    public void Reported_lines_come_back_newline_joined_in_the_order_they_arrived()
    {
        var capture = new CapturingProgress(CapturingProgress.DefaultMaxChars);

        capture.Report("first");
        capture.Report("second");
        capture.Report("third");

        capture.Text.Should().Be("first\nsecond\nthird\n");
    }

    [Fact]
    public void An_untouched_capture_has_empty_text()
    {
        var capture = new CapturingProgress(CapturingProgress.DefaultMaxChars);

        capture.Text.Should().Be(string.Empty);
    }

    [Fact]
    public void Output_that_would_exceed_the_bound_is_cut_and_says_so_instead_of_stopping_silently()
    {
        var capture = new CapturingProgress(maxChars: 20);

        capture.Report("0123456789"); // 11 chars with the newline, under the bound
        capture.Report("this line pushes the total past the bound");

        var text = capture.Text;
        text.Should().StartWith("0123456789\n");
        text.Should().Contain("truncated");
        // The line that would have overflowed the bound never made it in.
        text.Should().NotContain("this line pushes");
    }

    [Fact]
    public void Once_truncated_further_lines_are_dropped_rather_than_growing_the_marker_forever()
    {
        var capture = new CapturingProgress(maxChars: 10);

        capture.Report("a line long enough to trip the bound on its own");
        var afterFirstOverflow = capture.Text;

        capture.Report("another line reported after truncation already happened");
        var afterSecondOverflow = capture.Text;

        afterSecondOverflow.Should().Be(afterFirstOverflow, "one marker is enough; the capture should not keep growing");
    }
}
