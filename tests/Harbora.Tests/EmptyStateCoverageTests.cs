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
    [InlineData("Monitoring", "Index.cshtml")]
    public void The_page_renders_the_shared_empty_state_partial(string folder, string file)
    {
        var markup = View(folder, file);

        markup.Should().Contain("Design/_EmptyState",
            $"{folder}/{file} must show the shared empty state instead of a blank list or a bare dash");
    }
}
