using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Infrastructure.Navigation;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Modules.Sync.Infrastructure;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every icon a view can put on screen must exist in <c>main.ts</c>'s import list, or lucide's
/// <c>createIcons()</c> silently renders nothing for it — no exception, no console error, just an
/// empty space where a glyph should be. Sixteen of these went missing at once: <c>main.ts</c> imports
/// lucide icons one at a time, deliberately, to keep the bundle at roughly 138 kB instead of the
/// 821 kB the whole icon set costs — and a view that references a new icon has nothing forcing its
/// author to pair that with an import.
///
/// General over every view rather than a fixed list of the icons that were missing today, so this
/// class of rot cannot come back by way of the seventeenth icon nobody thought to check.
/// </summary>
public class IconCoverageTests
{
    private static string ViewsRoot => Path.Combine(TestPaths.WebRoot, "Views");

    private static string MainTs =>
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Scripts", "main.ts"));

    /// <summary>
    /// The exact identifiers <c>main.ts</c> imports from 'lucide' — the only names
    /// <c>createIcons()</c> can ever resolve, since it is handed this object and nothing else.
    /// </summary>
    private static HashSet<string> ImportedIconNames()
    {
        var source = MainTs;
        var start = source.IndexOf("import {", StringComparison.Ordinal);
        start.Should().BeGreaterThan(-1, "main.ts must import icons from lucide");
        var end = source.IndexOf("} from 'lucide';", start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, "the lucide import must close");

        var block = source[(start + "import {".Length)..end];
        return block.Split(',')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0 && s != "createIcons")
            .ToHashSet();
    }

    /// <summary>
    /// lucide's own kebab-case-to-component-name rule (<c>replaceElement.mjs</c>, via
    /// <c>toPascalCase</c>/<c>toCamelCase</c>), applied the same way <c>createIcons()</c> applies it
    /// at runtime — so <c>"table-2"</c> is checked as <c>Table2</c>, the literal export name lucide
    /// actually looks up, not some other guess at the mapping.
    /// </summary>
    private static string PascalCase(string kebab) =>
        string.Concat(kebab.Split('-').Select(part =>
            part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));

    private const string IconLiteral = @"[a-z][a-z0-9]*(?:-[a-z0-9]+)*";

    // A plain literal: `data-lucide="table-2"`. Anchored to the closing quote immediately after the
    // token, so a later attribute on the same tag (`aria-hidden="true"`) can never be mistaken for
    // the icon name.
    private static readonly Regex DirectIcon = new($@"data-lucide=""({IconLiteral})""", RegexOptions.Compiled);

    // A ternary: `data-lucide="@(x ? "a" : "b")"` is real markup in this codebase (the platform
    // health dot, the Simple/Advanced toggle). The Razor expression's own quotes would break a naive
    // "up to the next quote" attribute match, so this captures only what is between the matching
    // `@(` and `)"` and looks for icon literals inside that span, nowhere wider.
    private static readonly Regex TernaryIcon =
        new(@"data-lucide=""@\((.*?)\)""", RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex QuotedToken = new($@"""({IconLiteral})""", RegexOptions.Compiled);

    private static readonly Regex ModelIcon = new(
        $@"(?:StatCardModel|EmptyStateModel)\(\s*(?:Icon:\s*)?""({IconLiteral})""", RegexOptions.Compiled);

    /// <summary>
    /// Every icon name a view spells out as a literal: plain <c>data-lucide="..."</c> attributes,
    /// the ternary form above, and the icon argument to <c>StatCardModel</c>/<c>EmptyStateModel</c>,
    /// since <c>_StatCard</c>/<c>_EmptyState</c> render it as <c>data-lucide="@Model.Icon"</c>
    /// themselves.
    ///
    /// An icon chosen by C# logic that is not a string literal in the view — a per-app-type switch
    /// expression, for instance — is outside what a static scan of Razor source can verify. None of
    /// the defects this test exists to catch were of that shape; the sidebar's own icons, which are a
    /// variable one layer removed the same way, get their own test below instead of being folded in
    /// here.
    /// </summary>
    private static IEnumerable<string> IconNamesReferencedIn(string markup)
    {
        foreach (Match m in DirectIcon.Matches(markup))
            yield return m.Groups[1].Value;

        foreach (Match span in TernaryIcon.Matches(markup))
            foreach (Match token in QuotedToken.Matches(span.Groups[1].Value))
                yield return token.Groups[1].Value;

        foreach (Match m in ModelIcon.Matches(markup))
            yield return m.Groups[1].Value;
    }

    [Fact]
    public void The_scan_actually_finds_icons()
    {
        // Guards the test below: a regex that stopped matching would make it pass by finding no
        // offenders, having checked nothing.
        var total = Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories)
            .SelectMany(p => IconNamesReferencedIn(File.ReadAllText(p)))
            .Count();

        total.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Every_icon_referenced_in_a_view_is_importable_by_name()
    {
        var imported = ImportedIconNames();
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories))
        {
            var markup = File.ReadAllText(path);
            foreach (var icon in IconNamesReferencedIn(markup).Distinct())
            {
                var componentName = PascalCase(icon);
                if (!imported.Contains(componentName))
                    offenders.Add(
                        $"{Path.GetRelativePath(ViewsRoot, path)}: data-lucide=\"{icon}\" needs " +
                        $"`{componentName}` imported in main.ts");
            }
        }

        offenders.Should().BeEmpty(
            "every icon a view can render must be importable by lucide's createIcons(), or it renders nothing");
    }

    [Fact]
    public void Every_sidebar_navigation_icon_is_importable_by_name()
    {
        // The sidebar renders `data-lucide="@item.Icon"` — a variable, not a literal, so the scan
        // above cannot see it. The icon names themselves are still literals, just one layer away in
        // NavigationMap and the feature modules that augment it; two of the sixteen original
        // defects — the Nodes and Sync sidebar entries — lived exactly here.
        var imported = ImportedIconNames();

        var icons = NavigationMap.All
            .SelectMany(g => g.Items).Select(i => i.Icon)
            .Concat(SyncNavigation.Augment(NavigationMap.All, syncEnabled: true)
                .SelectMany(g => g.Items)
                .Where(i => i.Key == SyncNavigation.ItemKey)
                .Select(i => i.Icon))
            .Concat(BackupNavigation.Augment(NavigationMap.All, backupEnabled: true)
                .SelectMany(g => g.Items)
                .Where(i => i.Key == BackupNavigation.ItemKey)
                .Select(i => i.Icon))
            .Distinct()
            .ToList();

        icons.Should().NotBeEmpty();

        var offenders = icons.Where(icon => !imported.Contains(PascalCase(icon))).ToList();

        offenders.Should().BeEmpty(
            "every sidebar destination's icon must be importable by lucide's createIcons()");
    }
}
