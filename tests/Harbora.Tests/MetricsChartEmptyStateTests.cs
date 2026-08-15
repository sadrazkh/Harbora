using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <c>MetricsChart.vue</c> is a client-only Vue island — nothing in this suite ever renders it, the
/// same way nothing in this suite executes any other <c>.vue</c> file, and there is no JS test runner
/// in this repository to do so instead. What is checked here is what a Razor scan already checks for
/// <c>main.ts</c> in <c>IconCoverageTests</c> and for the design tokens in <c>UiBaselineTests</c>: the
/// source itself, so the property this class exists to protect cannot regress silently.
///
/// <para>
/// The property: "no data for this period" and a drawn zero-value line are opposite claims about an
/// app (do-not-change — the usage-range design spec), so they must come from two branches that can
/// never both fire for the same points array, gated on point count rather than folded into one
/// "insufficient data" bucket the way they were before this class existed.
/// </para>
/// </summary>
public class MetricsChartEmptyStateTests
{
    private static string ComponentSource => File.ReadAllText(
        Path.Combine(TestPaths.WebRoot, "Scripts", "islands", "MetricsChart.vue"));

    [Fact]
    public void The_no_data_branch_fires_only_when_the_window_returned_zero_points()
    {
        // Not "< 2", which would also catch the single-point case and describe it with the same
        // words as never having been measured at all.
        ComponentSource.Should().MatchRegex(
            @"v-if=""loaded\s*&&\s*points\.length\s*===\s*0""",
            "the empty state must be its own branch, gated on exactly zero points");
    }

    [Fact]
    public void The_no_data_branch_is_not_the_branch_that_draws_the_series()
    {
        // The svg is only reached once loaded, single-point and zero-point are all ruled out by the
        // v-else chain above it — so two or more points, including two or more points that are all
        // zero, draw a real (if flat) line rather than the no-data text.
        var svgOpensWithElse = Regex.IsMatch(ComponentSource, @"<svg\s+v-else\b");
        svgOpensWithElse.Should().BeTrue(
            "a flat zero-value series must fall through to the drawn <svg>, not to the no-data text");
    }

    [Fact]
    public void The_no_data_state_carries_its_own_bilingual_copy_distinct_from_the_ellipsis_placeholder()
    {
        var source = ComponentSource;

        // "no data for this period" is the design's own phrase for the empty state — asserted as a
        // literal because this is the one string in the component whose exact wording the design
        // calls out, the same way IconCoverageTests asserts on the exact lucide export name rather
        // than a looser pattern.
        source.Should().Contain("no data for this period",
            "the English copy for a genuinely empty window");
        source.Should().Contain("داده‌ای برای این بازه ثبت نشده است",
            "the Persian copy for the same state — the panel renders Persian by default");

        // Distinct markup from the ellipsis placeholder used for "loading" and "one sample so far",
        // so a test (or a person reading the DOM) can tell the two apart.
        source.Should().Contain("data-empty=\"true\"",
            "the no-data branch needs a marker the ellipsis placeholder does not carry");
    }

    [Fact]
    public void A_page_with_its_own_range_control_can_seed_the_chart_with_the_chosen_window()
    {
        // The prop the usage tabs' islands set via `data-minutes` — without it every chart would keep
        // asking for its own default window regardless of what the page's control says is selected.
        ComponentSource.Should().MatchRegex(@"minutes\?\s*:\s*number",
            "the component must accept an initial window from its caller");
    }
}
