using FluentAssertions;
using Harbora.Cli;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which app a deploy is for, decided before anything is asked or uploaded.
///
/// Reported from a real session: `harbora deploy Kousar-kolie` against an app whose slug is
/// `kousar-kolie` packed 311 files, uploaded 3.1 MB, and failed with "Error while copying content to
/// a stream." The CLI matched the name case-insensitively and then sent what the user typed; the
/// server compares ordinally, answered 404 before reading the body, and tore the upload down. So the
/// rule pinned here is that resolving a name yields the *server's* spelling of it, never the
/// caller's — and that a name nobody recognises is never quietly resolved to something else.
/// </summary>
public class DeployAppChoiceTests
{
    private static RemoteApp App(string slug) => new(slug, slug, "Running", "Upload", false);

    private static readonly IReadOnlyList<RemoteApp> Two = [App("kousar-kolie"), App("subscriptionlink")];

    [Fact]
    public void A_name_resolves_to_the_servers_spelling_of_it()
    {
        var choice = AppChoice.Resolve("Kousar-kolie", Two, interactive: false, yes: true);

        choice.Current!.Slug.Should().Be("kousar-kolie");
        choice.Problem.Should().BeNull();
    }

    [Fact]
    public void A_terminal_is_offered_the_list_even_when_the_name_is_known()
    {
        // The point of the picker: `harbora deploy` shows what you could deploy to, the way CapRover
        // does, rather than silently acting on a name written into a file months ago.
        var choice = AppChoice.Resolve("kousar-kolie", Two, interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeTrue();
        choice.Current!.Slug.Should().Be("kousar-kolie", "the current app is what the list preselects");
    }

    [Fact]
    public void Yes_asks_nothing()
    {
        AppChoice.Resolve("kousar-kolie", Two, interactive: true, yes: true)
            .NeedsPrompt.Should().BeFalse();
    }

    [Fact]
    public void No_terminal_asks_nothing()
    {
        // CI must fail with an explanation rather than block on input nobody can give.
        AppChoice.Resolve("kousar-kolie", Two, interactive: false, yes: false)
            .NeedsPrompt.Should().BeFalse();
    }

    [Fact]
    public void An_unknown_name_is_never_resolved_to_something_else()
    {
        var choice = AppChoice.Resolve("typo", Two, interactive: false, yes: true);

        choice.Current.Should().BeNull();
        choice.NeedsPrompt.Should().BeFalse();
        choice.Problem.Should().Contain("typo");
    }

    [Fact]
    public void An_unknown_name_in_a_terminal_says_so_and_still_offers_the_list()
    {
        var choice = AppChoice.Resolve("typo", Two, interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeTrue();
        choice.Current.Should().BeNull("nothing should be preselected when the name matched nothing");
        choice.Problem.Should().Contain("typo");
    }

    [Fact]
    public void No_name_and_no_terminal_explains_itself()
    {
        var choice = AppChoice.Resolve(null, Two, interactive: false, yes: false);

        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("No app specified");
    }

    [Fact]
    public void A_single_app_is_not_a_question()
    {
        // A one-item menu asks a question with no answer.
        var choice = AppChoice.Resolve(null, [App("only-one")], interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeFalse();
        choice.Current!.Slug.Should().Be("only-one");
    }

    [Fact]
    public void A_single_app_does_not_absorb_a_name_that_is_not_it()
    {
        // Deploying to the only app because the name given was wrong is the one outcome worse than
        // failing: it deploys, and to the wrong place.
        var choice = AppChoice.Resolve("something-else", [App("only-one")], interactive: false, yes: true);

        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("something-else");
    }

    [Fact]
    public void An_account_with_no_apps_is_told_where_to_make_one()
    {
        var choice = AppChoice.Resolve(null, [], interactive: true, yes: false);

        choice.NeedsPrompt.Should().BeFalse();
        choice.Current.Should().BeNull();
        choice.Problem.Should().Contain("panel");
    }

    [Fact]
    public void The_current_app_is_offered_first()
    {
        var ordered = AppChoice.Order(Two, Two[1]);

        ordered.Select(a => a.Slug).Should().Equal("subscriptionlink", "kousar-kolie");
    }

    [Fact]
    public void Without_a_current_app_the_order_is_left_alone()
    {
        AppChoice.Order(Two, null).Should().Equal(Two);
    }
}
