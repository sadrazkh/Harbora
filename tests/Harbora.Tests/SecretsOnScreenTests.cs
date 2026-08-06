using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A secret is never on screen unless somebody asked for it.
///
/// The storage page had this right from the start: keys are hidden and revealed one at a time,
/// because a page that prints every secret it holds is a page nobody can screen-share. The Git page
/// and the application page printed the webhook secret of every repository, always — and those are
/// exactly the pages an operator has open while walking somebody through wiring a repository up.
///
/// The secret is not decoration there: it is what proves a push notification came from the
/// provider, so reading one off a screen is enough to forge a deployment.
///
/// Checked against the views because that is where the decision is made, and because the next page
/// to print one will be a page nobody thought to look at.
/// </summary>
public class SecretsOnScreenTests
{
    private static readonly string ViewsRoot = Path.Combine(TestPaths.WebRoot, "Views");

    /// <summary>
    /// Properties that hold a live secret. AccessKey is deliberately absent: it names the caller
    /// rather than authenticating them, and the storage page shows it beside the hidden secret on
    /// purpose so somebody can tell two buckets apart.
    /// </summary>
    private static readonly Regex Secret = new(
        @"@[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\.(WebhookSecret|SecretKey|ClientSecret|ApiKey|PlainSecret)\b",
        RegexOptions.Compiled);

    private static IEnumerable<string> Views() =>
        Directory.EnumerateFiles(ViewsRoot, "*.cshtml", SearchOption.AllDirectories);

    [Fact]
    public void The_scan_finds_the_views()
    {
        Views().Should().HaveCountGreaterThan(30);
    }

    [Fact]
    public void A_view_that_prints_a_secret_also_asks_before_printing_it()
    {
        // "Reveal" is the panel's one word for this. Requiring it in the same file is coarse, but it
        // fails the moment somebody prints a secret in a view that has no reveal at all — which is
        // exactly how both of these shipped.
        foreach (var view in Views())
        {
            var text = File.ReadAllText(view);
            if (!Secret.IsMatch(text)) continue;

            text.Should().Contain("Reveal",
                $"{Path.GetFileName(view)} prints {Secret.Match(text).Value} — hide it behind a reveal");
        }
    }

    [Fact]
    public void The_pages_that_hold_a_webhook_secret_are_still_the_two_we_know_about()
    {
        // If a third appears, this fails and somebody reads the test above rather than discovering
        // the rule from scratch.
        var printers = Views()
            .Where(v => Secret.IsMatch(File.ReadAllText(v)))
            .Select(Path.GetFileName)
            .OrderBy(n => n)
            .ToList();

        printers.Should().BeEquivalentTo(["Details.cshtml", "Index.cshtml"]);
    }
}
