using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Conventions the shell depends on, checked mechanically because they are the kind of thing that
/// decays one view at a time and is never noticed until somebody opens the panel in Persian.
/// </summary>
public class LayoutConventionTests
{
    private static readonly string DesignDirectory =
        Path.Combine(TestPaths.WebRoot, "Views", "Shared", "Design");

    private static IEnumerable<string> DesignViews() =>
        Directory.EnumerateFiles(DesignDirectory, "*.cshtml");

    [Fact]
    public void The_design_partials_exist()
    {
        // Guards every other test in this class: an empty directory makes them all pass.
        DesignViews().Should().HaveCountGreaterThanOrEqualTo(8);
    }

    [Fact]
    public void No_design_partial_uses_a_physical_direction_class()
    {
        // ml-/mr-/pl-/pr-/left-/right- do not mirror in RTL. Persian is the default culture here,
        // so a physical class is a layout that is wrong for most of the people using it.
        var physical = new Regex(
            @"(?<![\w-])(ml|mr|pl|pr|border-l|border-r|rounded-l|rounded-r)-\w",
            RegexOptions.Compiled);

        foreach (var file in DesignViews())
        {
            var offending = physical.Match(File.ReadAllText(file));
            offending.Success.Should().BeFalse(
                $"{Path.GetFileName(file)} uses '{offending.Value}' — use the logical equivalent (ms/me/ps/pe/border-s/border-e)");
        }
    }

    [Fact]
    public void Every_design_partial_that_shows_words_shows_them_in_both_languages()
    {
        // A partial with user-visible English and no Persian branch ships an untranslated screen.
        //
        // Asserted on the presence of Persian characters rather than on one syntax: the first
        // version of this test looked for `isFa ? "x" : "y"` and failed the sidebar, which is fully
        // translated via a tuple switch. Testing the shape of the code instead of the outcome finds
        // the wrong files.
        var persian = new Regex(@"[؀-ۿ]", RegexOptions.Compiled);

        foreach (var file in DesignViews())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("isFa")) continue;

            persian.IsMatch(text).Should().BeTrue(
                $"{Path.GetFileName(file)} branches on culture but contains no Persian text");
        }
    }

    [Fact]
    public void Only_the_metric_partials_print_a_measured_value()
    {
        // The honesty gate only works if nothing routes around it. A panel that reaches into
        // MetricView.Text itself can print whatever it likes, including a zero it invented.
        foreach (var file in DesignViews())
        {
            var name = Path.GetFileName(file);
            if (name is "_Metric.cshtml" or "_Sparkline.cshtml") continue;

            File.ReadAllText(file).Should().NotContain("View.Text",
                $"{name} should render Design/_Metric instead of printing the value itself");
        }
    }

    [Fact]
    public void The_layout_renders_the_shell_partials()
    {
        // The partials are worthless if the layout still carries a hand-written copy of the nav.
        var layout = File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Shared", "_Layout.cshtml"));

        layout.Should().Contain("Design/_Sidebar");
        layout.Should().Contain("Design/_Topbar");
    }

    [Fact]
    public void The_right_rail_is_rendered_exactly_once()
    {
        // Razor throws if a section is rendered twice. The rail is placed by CSS for that reason,
        // and a second RenderSectionAsync would only be discovered by loading a page that uses it.
        var layout = File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Views", "Shared", "_Layout.cshtml"));

        Regex.Matches(layout, @"RenderSectionAsync\(""RightRail""").Count.Should().Be(1);
    }
}
