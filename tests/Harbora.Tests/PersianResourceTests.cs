using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Every phrase a view asks the localiser for has a Persian entry.
///
/// A localiser returns the key when it has no entry, and the keys in this codebase are English
/// sentences. So a missing translation is not a blank or a marker anybody would notice in review —
/// it is an English paragraph in the middle of a Persian page, and it looks deliberate.
///
/// About a hundred had accumulated: the whole landing page, every error page, the audit log and the
/// rollback confirmation. Two causes, both invisible from the view itself:
///
/// * Six views injected <c>IViewLocalizer</c>, which shadows the shared localiser that
///   _ViewImports injects for everyone else. It looks for Resources/Views/&lt;Controller&gt;/&lt;
///   View&gt;.fa.resx — files that were never created — so every string in those views fell back to
///   English while the rest of the panel was translated.
/// * Other views used the shared localiser correctly with keys nobody had ever added.
/// </summary>
public class PersianResourceTests
{
    private static readonly string ViewsRoot = Path.Combine(TestPaths.WebRoot, "Views");
    private static readonly string Resource =
        Path.Combine(TestPaths.WebRoot, "Resources", "SharedResource.fa.resx");

    private static readonly Regex Key = new(@"T\[""([^""]*)""\]", RegexOptions.Compiled);

    private static IEnumerable<string> Views() =>
        Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories);

    /// <summary>
    /// Read as XML rather than scanned for a pattern: a key containing an ampersand is stored
    /// escaped, so a regex over the raw file reports "Automatic SSL &amp;amp; backups" as a name
    /// nothing uses and the real key as one nothing translates.
    /// </summary>
    private static List<string> ResourceNames() =>
        System.Xml.Linq.XDocument.Load(Resource).Root!
            .Elements("data")
            .Select(e => (string?)e.Attribute("name") ?? string.Empty)
            .ToList();

    [Fact]
    public void The_scan_finds_the_views_and_the_resource()
    {
        // Guards everything below: an empty scan or a missing file makes them all pass.
        Views().Should().HaveCountGreaterThan(30);
        File.Exists(Resource).Should().BeTrue();
    }

    [Fact]
    public void Every_phrase_a_view_asks_for_has_a_persian_translation()
    {
        // Case-sensitive, because that is how the lookup works, and the two halves of this rule pull
        // in opposite directions: the *build* treats "Disk" and "disk" as one entry and drops the
        // second with a warning, while the *lookup* treats them as two names and finds neither for
        // the spelling that was dropped. Comparing case-insensitively here made the first version of
        // this test green while four keys on the audit and landing pages still rendered in English.
        var translated = ResourceNames().ToHashSet(StringComparer.Ordinal);

        var missing = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var view in Views())
            foreach (Match match in Key.Matches(File.ReadAllText(view)))
                if (!translated.Contains(match.Groups[1].Value))
                    missing.Add($"{Path.GetFileName(view)}: {match.Groups[1].Value}");

        missing.Should().BeEmpty(
            "a key with no entry renders as its own English text on a Persian page — "
            + "add it to Resources/SharedResource.fa.resx");
    }

    [Fact]
    public void No_view_shadows_the_shared_localiser()
    {
        // _ViewImports injects IHtmlLocalizer<SharedResource> as T for every view. A view that
        // injects IViewLocalizer under the same name silently swaps the resource file underneath
        // itself for one that does not exist.
        foreach (var view in Views())
            File.ReadAllText(view).Should().NotContain("IViewLocalizer",
                $"{Path.GetFileName(view)} would stop reading SharedResource.fa.resx");
    }

    [Fact]
    public void No_resource_name_is_a_duplicate_of_another_by_case_alone()
    {
        // The build only warns, and the second entry is dropped — so the translation somebody wrote
        // most recently is the one that silently does nothing.
        var names = ResourceNames();

        names.Should().OnlyHaveUniqueItems();
        names.Select(n => n.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }
}
