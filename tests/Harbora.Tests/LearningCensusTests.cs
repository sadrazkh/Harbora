using System.Linq;
using FluentAssertions;
using Harbora.Infrastructure.Learning;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The Help control is only as honest as the table behind it. A slug <see cref="HelpMap"/> hands out
/// that does not exist on disk turns "click Help" into "click 404" — worse than the honest "no chapter
/// for this screen" the control already falls back to, because a 404 costs a click to discover it was
/// wrong instead of saying so up front.
///
/// <para>
/// Follows <c>DetailTabCensusTests</c> and <c>AppAddressCensusTests</c> for exactly the reason they
/// give: reads <see cref="LearningLibrary"/> — the real chapters on disk — rather than a list of slugs
/// written here, which would be the thing this suite exists to protect from going stale the day a
/// chapter is renamed or removed.
/// </para>
/// </summary>
public class LearningCensusTests
{
    private static LearningLibrary Library() => new(TestPaths.DocsRoot);

    [Fact]
    public void The_help_map_offers_at_least_one_route()
    {
        HelpMap.Routes.Should().NotBeEmpty(
            "a Help control backed by nothing is the filing-cabinet link this sub-project exists to replace");
    }

    [Fact]
    public void Every_chapter_the_help_map_can_send_someone_to_exists_on_disk()
    {
        var onDisk = Library().Chapters().Select(c => c.Slug).ToHashSet();
        onDisk.Should().NotBeEmpty("the census only means something if there are real chapters to check against");

        var mappedSlugs = HelpMap.Routes.Select(r => r.Slug).Distinct().ToList();

        mappedSlugs.Should().OnlyContain(slug => onDisk.Contains(slug),
            "a chapter slug the Help control can point at must be one LearningLibrary actually finds on " +
            "disk — otherwise the click that was supposed to help lands on a 404");
    }

    /// <summary>
    /// The mechanism the whole sub-project is built on: a longer, more specific route entry
    /// (<c>/apps/{id}/volumes</c>) must outrank a shorter one that also matches (the bare
    /// <c>/apps</c>), or the volumes tab could never answer with anything other than whatever the
    /// rest of the app page answers with.
    /// </summary>
    [Fact]
    public void A_more_specific_route_wins_over_a_shorter_one_that_also_matches()
    {
        var onDisk = Library().Chapters().Select(c => c.Slug).ToHashSet();
        var appId = Guid.NewGuid();

        var appsChapter = HelpMap.ChapterFor("/apps");
        var volumesChapter = HelpMap.ChapterFor($"/apps/{appId}/volumes");

        appsChapter.Should().NotBeNull().And.Match(slug => onDisk.Contains(slug!));
        volumesChapter.Should().NotBeNull().And.Match(slug => onDisk.Contains(slug!));
        volumesChapter.Should().NotBe(appsChapter,
            "the longest matching entry must win, or /apps/{id}/volumes could never resolve to " +
            "anything other than what the bare /apps prefix resolves to");
    }

    [Fact]
    public void A_screen_with_no_entry_answers_null_rather_than_a_guess()
    {
        // /workspaces has no dedicated section in any of the nine chapters (chapter 1 only mentions
        // switching workspace from the sidebar card in passing) — an honest gap, not an oversight.
        HelpMap.ChapterFor("/workspaces").Should().BeNull(
            "a wrong guess costs a click to discover it was wrong; null is the honest answer until this " +
            "screen gets its own chapter");
    }
}
