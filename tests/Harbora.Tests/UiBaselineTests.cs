using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A guard on the interface that already exists.
///
/// The panel's look is signed off and must not drift while features are added underneath it. A
/// screenshot comparison is the usual tool and is not available in this environment, so this locks
/// the two things that actually broke before:
///
/// <list type="bullet">
/// <item>the design tokens — everything is drawn from them, so if the palette or the surface ramp
/// is edited, every page moves at once;</item>
/// <item>tag balance in the views — an extra <c>&lt;/div&gt;</c> compiles, renders, and silently
/// closes the page's main element early, which is how the monitoring page ended up with its content
/// outside the layout and squeezed into 48 pixels.</item>
/// </list>
/// </summary>
public class UiBaselineTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harbora.slnx")))
            dir = dir.Parent;

        dir.Should().NotBeNull("the tests must be able to find the repository root");
        return dir!.FullName;
    }

    private static string ViewsRoot() => Path.Combine(RepoRoot(), "src", "Harbora.Web", "Views");

    /// <summary>
    /// The semantic tokens every component is built on. Adding to this list is fine; removing or
    /// renaming one is a redesign, and this test is where that conversation starts.
    /// </summary>
    private static readonly string[] RequiredTokens =
    [
        "--canvas", "--surface", "--surface-2", "--border", "--border-strong",
        "--text", "--text-muted", "--text-faint",
        "--brand", "--brand-hover", "--brand-text", "--brand-soft",
        "--ok", "--warn", "--error", "--info", "--idle"
    ];

    [Fact]
    public void The_design_tokens_are_all_still_defined()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Harbora.Web", "Scripts", "app.css"));

        foreach (var token in RequiredTokens)
            css.Should().Contain(token + ":", $"{token} is part of the approved design system");
    }

    [Fact]
    public void Both_themes_define_every_token()
    {
        // A token defined only in light leaves dark mode falling back to whatever it inherits, which
        // is how a panel ends up with unreadable text in one theme and nobody notices for a week.
        var css = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Harbora.Web", "Scripts", "app.css"));
        var darkAt = css.IndexOf("html.dark", StringComparison.Ordinal);
        darkAt.Should().BeGreaterThan(0, "the dark theme block must exist");

        var light = css[..darkAt];
        var dark = css[darkAt..];

        foreach (var token in RequiredTokens)
        {
            light.Should().Contain(token + ":", $"{token} must be defined for the light theme");
            dark.Should().Contain(token + ":", $"{token} must be defined for the dark theme");
        }
    }

    [Fact]
    public void Every_view_closes_the_tags_it_opens()
    {
        // The failure this catches does not throw and does not fail to compile. It renders a page
        // whose content has escaped its own layout.
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories))
        {
            // The panel partials are a deliberate pair: one opens the section, the other closes it.
            var name = Path.GetFileName(path);
            if (name is "_PanelStart.cshtml" or "_PanelEnd.cshtml") continue;

            var markup = Regex.Replace(File.ReadAllText(path), @"@\*.*?\*@", "", RegexOptions.Singleline);
            markup = Regex.Replace(markup, "<!--.*?-->", "", RegexOptions.Singleline);

            foreach (var tag in new[] { "div", "section", "aside", "main", "table", "form" })
            {
                var opened = Regex.Matches(markup, $@"<{tag}[\s>]", RegexOptions.IgnoreCase).Count;
                var closed = Regex.Matches(markup, $@"</{tag}>", RegexOptions.IgnoreCase).Count;

                if (opened != closed)
                    offenders.Add($"{Path.GetRelativePath(ViewsRoot(), path)}: <{tag}> opened {opened}, closed {closed}");
            }
        }

        offenders.Should().BeEmpty("unbalanced markup silently breaks the page layout");
    }

    [Fact]
    public void No_view_reintroduces_the_retired_colour_ramp()
    {
        // The pre-redesign indigo ramp. Anything still using it renders a different purple from the
        // rest of the panel, which is the exact drift this suite exists to prevent.
        var offenders = Directory.EnumerateFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories)
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"\bbrand-[0-9]{3}\b"))
            .Select(p => Path.GetRelativePath(ViewsRoot(), p))
            .ToList();

        offenders.Should().BeEmpty("the accent token replaced the brand-NNN ramp");
    }

    [Fact]
    public void A_filled_accent_control_always_carries_its_text_colour()
    {
        // bg-accent with no text colour inherits the page ink: near-white on dark, near-black on
        // light. It looked correct for as long as the panel defaulted to dark.
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ViewsRoot(), "*.cshtml", SearchOption.AllDirectories))
        {
            // Only <a> and <button>: those carry a label. The same fill on a progress bar or a dot
            // is a shape with no text in it, and flagging those would only teach people to add a
            // meaningless class to silence the test.
            foreach (Match m in Regex.Matches(
                         File.ReadAllText(path),
                         @"<(a|button)\b[^>]*class=""([^""]*\bbg-accent\b[^""]*)""",
                         RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                var classes = m.Groups[2].Value;
                if (!classes.Contains("text-white") && !Regex.IsMatch(classes, @"\btext-(ink|accent)"))
                    offenders.Add($"{Path.GetRelativePath(ViewsRoot(), path)}: {classes.Trim()}");
            }
        }

        offenders.Should().BeEmpty("a filled accent control needs an explicit text colour");
    }
}
