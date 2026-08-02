using FluentAssertions;
using Harbora.Infrastructure.Design;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The palette, checked rather than eyeballed.
///
/// Two values in the first draft of the design failed this test — tertiary text at 2.81 on white,
/// and the dark brand fill at 3.67 with white on it. Both looked fine in a mockup. Contrast is the
/// one part of visual design that is arithmetic, so it belongs in the suite and not in an opinion.
/// </summary>
public class DesignTokenTests
{
    private static readonly string Css =
        File.ReadAllText(Path.Combine(TestPaths.WebRoot, "Scripts", "app.css"));

    private static double Ratio(string theme, string a, string b)
    {
        var tokens = DesignTokens.Parse(Css)[theme];
        return ColorContrast.Ratio(tokens[a], tokens[b]);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Body_text_is_readable_on_every_surface(string theme)
    {
        Ratio(theme, "--text", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
        Ratio(theme, "--text", "--canvas").Should().BeGreaterThanOrEqualTo(4.5);
        Ratio(theme, "--text", "--surface-2").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Secondary_text_is_readable(string theme)
    {
        // Muted text carries most of the panel captions in the mockups. It is normal text, so it
        // gets the normal floor, not the decorative one.
        Ratio(theme, "--text-muted", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Tertiary_text_clears_the_decorative_floor(string theme)
    {
        Ratio(theme, "--text-faint", "--surface").Should().BeGreaterThanOrEqualTo(3.0);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void White_on_the_brand_fill_is_readable(string theme)
    {
        // Every primary button in the mockups is white on violet.
        var tokens = DesignTokens.Parse(Css)[theme];

        ColorContrast.Ratio("255 255 255", tokens["--brand"]).Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Brand_links_are_readable_on_a_surface(string theme)
    {
        // Deliberately a different token from the fill: the colour that works under white text is
        // not the colour that works as text on white.
        Ratio(theme, "--brand-text", "--surface").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Status_text_is_readable_on_its_own_tint(string theme)
    {
        // The pills are a colour on a tint of the same colour, which is the pairing most likely to
        // look fine to whoever picked it and be unreadable to everybody else.
        foreach (var tone in new[] { "ok", "warn", "error", "info", "idle" })
            Ratio(theme, $"--{tone}", $"--{tone}-soft").Should().BeGreaterThanOrEqualTo(4.5, $"the {tone} pill in {theme}");
    }

    [Theory]
    [InlineData("light")]
    [InlineData("dark")]
    public void Code_and_terminal_text_are_readable_on_their_own_surfaces(string theme)
    {
        // A terminal keeps a dark background in both themes, so it cannot borrow the page's text
        // colour — in light mode that is near-black on near-black. The migration produced exactly
        // that before these tokens existed.
        Ratio(theme, "--code-ink", "--code").Should().BeGreaterThanOrEqualTo(4.5);
        Ratio(theme, "--terminal-ink", "--terminal").Should().BeGreaterThanOrEqualTo(4.5);
    }

    [Fact]
    public void Both_themes_define_the_same_tokens()
    {
        // A token defined in one theme and forgotten in the other renders as an inherited value,
        // which is how a dark-mode page ends up with one white card on a black page.
        var parsed = DesignTokens.Parse(Css);

        parsed["dark"].Keys.Should().BeEquivalentTo(parsed["light"].Keys);
    }
}
