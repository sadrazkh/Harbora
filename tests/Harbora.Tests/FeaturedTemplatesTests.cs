using FluentAssertions;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which ready-made apps a person sees first.
///
/// The dashboard took the first eight templates alphabetically. That is not a choice anybody made —
/// it is what happens when nobody is asked — so the apps an operator most wants installed sat
/// wherever their name put them, and a template added later could push one off the page without
/// anybody deciding it should go.
/// </summary>
public class FeaturedTemplatesTests
{
    private static readonly string[] Available = ["gitea", "grafana", "metabase", "minio", "n8n", "sentry"];

    [Fact]
    public void With_nothing_chosen_the_previous_order_is_kept()
    {
        // An operator who has never opened the setting must not have their dashboard rearranged by
        // the arrival of the setting itself.
        FeaturedTemplates.Resolve([], Available).Should().Equal(Available);
    }

    [Fact]
    public void The_chosen_order_is_the_order_shown()
    {
        // Not sorted afterwards. The whole point is that the operator decides what comes first.
        FeaturedTemplates.Resolve(["n8n", "gitea"], Available).Should().Equal("n8n", "gitea");
    }

    [Fact]
    public void Choosing_three_shows_three()
    {
        // Padding the rest from the alphabet is the behaviour this replaces: it would put back on
        // the page exactly the tiles the operator left off it.
        FeaturedTemplates.Resolve(["n8n", "gitea", "minio"], Available).Should().HaveCount(3);
    }

    [Fact]
    public void A_withdrawn_template_is_skipped_rather_than_leaving_a_hole()
    {
        // A template can be disabled after being chosen. A tile that leads nowhere is worse than
        // one fewer tile.
        FeaturedTemplates.Resolve(["n8n", "removed-app", "gitea"], Available)
            .Should().Equal("n8n", "gitea");
    }

    [Fact]
    public void A_choice_of_only_withdrawn_templates_falls_back_rather_than_showing_nothing()
    {
        // An empty row of ready apps reads as "this platform has none".
        FeaturedTemplates.Resolve(["gone", "also-gone"], Available).Should().Equal(Available);
    }

    [Fact]
    public void More_than_the_slots_are_trimmed()
    {
        var many = Enumerable.Range(0, 20).Select(i => $"app-{i}").ToList();

        FeaturedTemplates.Resolve(many, many).Should().HaveCount(FeaturedTemplates.Slots);
    }

    [Fact]
    public void No_slots_means_nothing_rather_than_everything()
    {
        FeaturedTemplates.Resolve(["n8n"], Available, slots: 0).Should().BeEmpty();
    }

    [Fact]
    public void A_key_chosen_twice_appears_once()
    {
        FeaturedTemplates.Parse("n8n,gitea,n8n").Should().Equal("n8n", "gitea");
    }

    [Fact]
    public void What_is_stored_reads_back_as_what_was_chosen()
    {
        var chosen = new[] { "N8N", " gitea ", "minio" };

        FeaturedTemplates.Parse(FeaturedTemplates.Format(chosen))
            .Should().Equal("n8n", "gitea", "minio");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , , ")]
    public void An_empty_setting_is_no_choice_rather_than_a_broken_one(string? stored)
    {
        FeaturedTemplates.Parse(stored).Should().BeEmpty();
    }
}
