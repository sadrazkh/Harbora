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
    public void No_view_uses_a_physical_direction_class()
    {
        // ml-/mr-/pl-/pr-/left-/right- do not mirror in RTL. Persian is the default culture here,
        // so a physical class is a layout that is wrong for most of the people using it.
        //
        // Originally only the Design partials were held to this; widening the net found two
        // hand-branched insets on the landing page (`isFa ? "right-6" : "left-6"`) doing by hand
        // what `start-6` does by itself. Every view is covered now. A view with a genuine need for
        // a physical class — inside a dir="ltr" block, say — goes in the allowlist with its reason.
        var physical = new Regex(
            @"(?<![\w-])(ml|mr|pl|pr|border-l|border-r|rounded-l|rounded-r|text-left|text-right|left|right)-[\w.\[]",
            RegexOptions.Compiled);

        string[] allowed = [];

        var views = Directory.EnumerateFiles(
            Path.Combine(TestPaths.WebRoot, "Views"), "*.cshtml", SearchOption.AllDirectories).ToList();
        views.Should().HaveCountGreaterThan(30);

        foreach (var file in views)
        {
            if (allowed.Contains(Path.GetFileName(file))) continue;

            var offending = physical.Match(File.ReadAllText(file));
            offending.Success.Should().BeFalse(
                $"{Path.GetFileName(file)} uses '{offending.Value}' — use the logical equivalent (ms/me/ps/pe/start/end)");
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
    public void A_control_that_shows_only_a_glyph_still_has_a_name()
    {
        // The delete buttons are a lone ✕ and several links are a lone icon. A sighted person
        // infers the meaning from position; a screen reader announces "button" and nothing else,
        // which on a row of five identical ✕ buttons is five identical mysteries. Anything whose
        // visible content has no letters must carry an aria-label or a title.
        var control = new Regex(@"<(button|a)\b((?:[^<>""]|""[^""]*"")*)>(.*?)</\1>",
            RegexOptions.Compiled | RegexOptions.Singleline);
        var tags = new Regex("<[^>]+>", RegexOptions.Compiled);
        var razor = new Regex(@"@[\w.\[\]()""'?: ]+", RegexOptions.Compiled);
        var letters = new Regex(@"[A-Za-z؀-ۿ]", RegexOptions.Compiled);

        var unnamed = new List<string>();

        foreach (var file in Directory.EnumerateFiles(
                     Path.Combine(TestPaths.WebRoot, "Views"), "*.cshtml", SearchOption.AllDirectories))
            foreach (Match match in control.Matches(File.ReadAllText(file)))
            {
                var attributes = match.Groups[2].Value;
                if (attributes.Contains("aria-label") || attributes.Contains("title=")) continue;

                var inner = match.Groups[3].Value;
                // A nested element may carry the label (aria-hidden icon + labelled span).
                if (inner.Contains("aria-label")) continue;

                var visible = razor.Replace(tags.Replace(inner, ""), "");
                if (letters.IsMatch(visible)) continue;
                // Razor expressions that render text — @T[…], @name, @(isFa ? … : …) — count as
                // visible words.
                if (inner.Contains("@T[") || Regex.IsMatch(inner, @"@[a-zA-Z(]")) continue;

                if (inner.Contains("data-lucide") || visible.Trim().Length is > 0 and <= 3)
                    unnamed.Add($"{Path.GetFileName(file)}: {inner.Trim()[..Math.Min(40, inner.Trim().Length)]}");
            }

        unnamed.Should().BeEmpty("a glyph-only control announces nothing to a screen reader");
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
