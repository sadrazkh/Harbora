using FluentAssertions;
using Harbora.Infrastructure.Notifications;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The small JSON envelope <see cref="NotificationDelivery.EncryptedBody"/> carries a template's text
/// and HTML alternative through without a migration (N4, 2026-08-16 notification-system spec) — the
/// spec's own decomposition table says none, and this stays a single string column.
/// </summary>
public class ChannelBodyTests
{
    [Fact]
    public void A_templates_text_and_html_round_trip_through_the_envelope()
    {
        var encoded = ChannelBody.Encode("plain text", "<p>plain text</p>");

        var decoded = ChannelBody.Decode(encoded);

        decoded.Text.Should().Be("plain text");
        decoded.Html.Should().Be("<p>plain text</p>");
    }

    [Fact]
    public void A_plain_string_from_before_N4_decodes_as_text_only()
    {
        // Every row OutboxMail.Queue writes for a password reset, an email verification, or an
        // invite — and every NotificationDelivery ever queued before N4 existed — is exactly this
        // shape: the raw message, not JSON.
        var decoded = ChannelBody.Decode("Open this link to set a new password: https://example.com/reset?token=abc");

        decoded.Text.Should().Be("Open this link to set a new password: https://example.com/reset?token=abc");
        decoded.Html.Should().BeNull("a legacy body has no HTML alternative — mail goes out plain-text-only, unchanged");
    }

    [Fact]
    public void Json_that_is_not_the_envelope_still_decodes_as_text_only()
    {
        // A webhook's own JSON body, say, or any other string that merely happens to parse as JSON —
        // must not be mistaken for the {v, text, html} shape purely because it parses.
        var decoded = ChannelBody.Decode("""{"severity":"Critical","title":"t","body":"b"}""");

        decoded.Html.Should().BeNull();
    }
}
