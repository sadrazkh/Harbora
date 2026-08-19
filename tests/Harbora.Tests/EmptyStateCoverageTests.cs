using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Four lists that used to render nothing, or a lone "—", when they had no rows.
///
/// A header with no body reads as broken, not empty — the same defect
/// <c>Design/_EmptyState</c> already exists to fix everywhere else in the panel. These four pages
/// were the gap: three tables with no zero-row branch at all, and one list that fell back to a bare
/// dash instead of the shared partial the rest of the app uses.
/// </summary>
public class EmptyStateCoverageTests
{
    private static string View(params string[] parts) =>
        File.ReadAllText(Path.Combine([TestPaths.WebRoot, "Views", .. parts]));

    [Theory]
    [InlineData("Servers", "Index.cshtml")]
    [InlineData("Tenants", "Index.cshtml")]
    [InlineData("Users", "Index.cshtml")]
    public void The_page_renders_the_shared_empty_state_partial(string folder, string file)
    {
        var markup = View(folder, file);

        markup.Should().Contain("Design/_EmptyState",
            $"{folder}/{file} must show the shared empty state instead of a blank list or a bare dash");
    }

    /// <summary>
    /// Monitoring/Index.cshtml was split into per-section partials (2026-08-19 monitoring redesign) —
    /// 520 lines in one file was part of what made the page hard to reorder. The guarantee this test
    /// exists for — every list shows the shared empty state rather than a blank body or a bare dash —
    /// now spans the whole file set the page is assembled from, not the top-level view alone.
    /// </summary>
    [Fact]
    public void The_monitoring_page_and_its_partials_render_the_shared_empty_state_partial()
    {
        var folder = Path.Combine(TestPaths.WebRoot, "Views", "Monitoring");
        var markup = string.Concat(Directory.GetFiles(folder, "*.cshtml").Select(File.ReadAllText));

        markup.Should().Contain("Design/_EmptyState",
            "Monitoring/Index.cshtml and its partials must show the shared empty state instead of a blank list or a bare dash");
    }
}
