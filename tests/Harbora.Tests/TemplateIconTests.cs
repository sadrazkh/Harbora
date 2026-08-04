using FluentAssertions;
using Harbora.Infrastructure.Templates;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What a catalogue card draws for an app.
///
/// The letters were the whole icon before — "W" for WordPress — which reads as a missing image on an
/// app that is fully supported. They are now the exception, and this pins that round.
/// </summary>
public class TemplateIconTests
{
    private static Func<string, bool> Has(params string[] paths) => p => paths.Contains(p);
    private static readonly Func<string, bool> Nothing = _ => false;

    [Fact]
    public void A_template_with_a_logo_draws_the_logo()
    {
        var icon = TemplateIcon.For("wordpress", "WordPress", Has("/img/apps/wordpress.svg"));

        icon.HasImage.Should().BeTrue();
        icon.ImagePath.Should().Be("/img/apps/wordpress.svg");
    }

    [Fact]
    public void A_template_without_a_logo_falls_back_to_letters()
    {
        var icon = TemplateIcon.For("something-new", "Something New", Nothing);

        icon.HasImage.Should().BeFalse();
        icon.Initials.Should().Be("SN");
    }

    [Fact]
    public void Initials_come_from_the_display_name_not_the_slug()
    {
        // "uptime-kuma" would give UP; the name gives UK, which is what a person recognises.
        TemplateIcon.Initials("Uptime Kuma", "uptime-kuma").Should().Be("UK");
    }

    [Fact]
    public void A_one_word_name_uses_two_letters()
    {
        TemplateIcon.Initials("Sentry").Should().Be("SE");
    }

    [Fact]
    public void A_single_character_name_does_not_crash()
    {
        TemplateIcon.Initials("n").Should().Be("N");
    }

    [Fact]
    public void A_dotted_name_splits_on_the_dot()
    {
        TemplateIcon.Initials("Rocket.Chat").Should().Be("RC");
    }

    [Fact]
    public void An_empty_name_still_produces_something_drawable()
    {
        // A card with a blank square reads as broken; a question mark reads as unknown.
        TemplateIcon.Initials("", "").Should().Be("?");
    }

    [Fact]
    public void A_missing_key_does_not_look_up_an_asset()
    {
        TemplateIcon.For(null, "Some App", _ => true).HasImage.Should().BeFalse();
    }

    [Fact]
    public void The_lookup_is_case_insensitive_on_the_key()
    {
        TemplateIcon.For("PostgreSQL", "PostgreSQL", Has("/img/apps/postgresql.svg"))
            .HasImage.Should().BeTrue();
    }

    [Fact]
    public void Initials_are_still_carried_alongside_an_image()
    {
        // The card needs something to show while the image loads, and something for a screen reader
        // if the asset 404s in production.
        TemplateIcon.For("wordpress", "WordPress", Has("/img/apps/wordpress.svg"))
            .Initials.Should().Be("WO");
    }
}
