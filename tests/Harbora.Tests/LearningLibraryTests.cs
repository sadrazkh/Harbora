using FluentAssertions;
using Harbora.Infrastructure.Learning;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The library that reads the nine tutorial chapters off disk and decides which images alongside
/// them may be served.
///
/// <para>
/// Nothing here keeps its own list of chapters or images — <c>Chapters()</c> reads the directory and
/// sorts on the numeric prefix, and the image census below reads <c>docs/tutorial/img</c> directly.
/// A hand-kept list here would be the thing this suite exists to catch going stale.
/// </para>
/// </summary>
public class LearningLibraryTests
{
    private static LearningLibrary Library() => new(TestPaths.DocsRoot);

    [Fact]
    public void Every_chapter_on_disk_is_offered()
    {
        var chapters = Library().Chapters();

        // Deliberately not asserting the count as a literal in prose beyond what the fixture already
        // is: nine chapter files sit in docs/tutorial today, numbered 01 through 09, and each is read
        // rather than assumed.
        var expectedFileNames = Directory.EnumerateFiles(TestPaths.DocsRoot, "*.md")
            .Select(Path.GetFileName)
            .Where(name => name != "README.md")
            .ToList();

        expectedFileNames.Should().NotBeEmpty("a fixture with no chapter files would make every assertion below vacuous");

        chapters.Select(c => c.FileName).Should().BeEquivalentTo(expectedFileNames,
            "every markdown file in docs/tutorial other than the README is a chapter");

        chapters.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Slug), "every chapter needs a route segment");
        chapters.Should().OnlyContain(c => !string.IsNullOrWhiteSpace(c.Title), "every chapter needs a title to show");

        // Numbered and sorted on that number, not on the order the filesystem happens to return.
        chapters.Select(c => c.Number).Should().BeInAscendingOrder();
        chapters.Select(c => c.Number).Should().OnlyHaveUniqueItems();

        // The title is read from the file's own first heading, not kept here — spot-checked against
        // one real chapter so a change to how the title is parsed cannot drift from what the file says
        // without this test noticing.
        var firstSteps = chapters.Single(c => c.Number == 1);
        var headingLine = File.ReadLines(Path.Combine(TestPaths.DocsRoot, firstSteps.FileName)).First();
        headingLine.Should().Be("# " + firstSteps.Title, "the chapter's title is its own first heading, read fresh");
    }

    [Fact]
    public void The_index_readme_is_not_offered_as_a_chapter()
    {
        Library().Chapters().Should().NotContain(c => c.FileName == "README.md",
            "README.md is the index for the docs site, not a chapter, and carries no numeric prefix");
    }

    [Fact]
    public void An_annotated_capture_may_be_served()
    {
        Library().MayServeImage("01-dashboard.annotated.png").Should().BeTrue();
    }

    [Fact]
    public void A_raw_capture_may_not_be_served_even_though_git_already_ignores_it()
    {
        // .gitignore:42 keeps a raw capture out of the repository. This is a different question: a
        // developer's working directory can hold one anyway, and the render path must refuse it on
        // its own, without relying on git having done anything.
        Library().MayServeImage("01-dashboard.png").Should().BeFalse();
    }

    [Fact]
    public void A_name_that_climbs_out_of_the_directory_is_refused()
    {
        var library = Library();

        library.MayServeImage("../../appsettings.json").Should().BeFalse();

        // Ends with the right suffix so this is actually exercising the traversal guard, not the
        // suffix check above catching it for an unrelated reason.
        library.MayServeImage("../secrets.annotated.png").Should().BeFalse();

        // Rooted rather than relative — refused even though the leaf name alone would pass.
        library.MayServeImage("/secrets.annotated.png").Should().BeFalse();
    }

    [Fact]
    public async Task A_chapter_that_does_not_exist_reads_as_null_rather_than_throwing()
    {
        var reading = async () => await Library().ReadAsync("no-such-chapter");

        await reading.Should().NotThrowAsync();
        (await reading()).Should().BeNull();
    }

    [Fact]
    public async Task A_chapter_reads_as_html_containing_its_own_heading_text()
    {
        var library = Library();
        var firstSteps = library.Chapters().Single(c => c.Number == 1);

        var html = await library.ReadAsync(firstSteps.Slug);

        html.Should().NotBeNull();
        html!.Should().Contain(firstSteps.Title, "the rendered chapter still carries its own heading text");
    }

    /// <summary>
    /// The test the spec calls the one that matters most: every image actually sitting in
    /// <c>docs/tutorial/img</c> today is one the guard would serve. All 30 are annotated, so this
    /// passes today — but it passes because the guard function says so for each real file name, not
    /// because nothing was asked. Drop a raw capture into that directory and this is what fails.
    /// </summary>
    [Fact]
    public void Every_image_actually_in_the_directory_is_one_the_guard_would_serve()
    {
        var library = Library();
        var imgDir = Path.Combine(TestPaths.DocsRoot, "img");

        var files = Directory.EnumerateFiles(imgDir).Select(Path.GetFileName).ToList();

        files.Should().NotBeEmpty("a fixture with no images would make this pass for the wrong reason");

        files.Should().OnlyContain(f => library.MayServeImage(f!),
            "every file the panel could serve from docs/tutorial/img must be an annotated capture");
    }
}
