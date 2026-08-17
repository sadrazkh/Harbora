using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// What actually goes out over a channel: text always, and an HTML alternative when the message came
/// from a template (N4, 2026-08-16 notification-system spec) rather than from a literal string a
/// controller composed by hand — a password-reset link, an invite. Those keep sending exactly the
/// plain text they always have; <see cref="Html"/> is null for them, and <c>NotificationService</c>
/// sends plain-text-only mail in that case, unchanged from before N4.
///
/// <para>
/// <c>NotificationDelivery.EncryptedBody</c> stayed a single string column on purpose — N4's own
/// decomposition table says no migration — so a template's two bodies share it as a small JSON
/// envelope tagged with <see cref="Marker"/>. <see cref="Decode"/> only ever recognises that exact
/// shape; anything else (every pre-N4 row, every transactional email <c>OutboxMail.Queue</c> still
/// writes as plain text) decodes as <see cref="Html"/> <c>null</c> — the legacy, text-only behaviour —
/// rather than risk matching prose that merely happens to parse as JSON.
/// </para>
/// </summary>
internal sealed record ChannelBody(string Text, string? Html)
{
    private const string Marker = "n4";

    private sealed record Envelope(
        [property: JsonPropertyName("v")] string V,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("html")] string Html);

    public static string Encode(string text, string html) =>
        JsonSerializer.Serialize(new Envelope(Marker, text, html));

    public static ChannelBody Decode(string raw)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(raw);
            if (envelope is { V: Marker } && envelope.Text is not null && envelope.Html is not null)
                return new ChannelBody(envelope.Text, envelope.Html);
        }
        catch (JsonException) { /* not our envelope — a plain string from before N4, or a transactional body */ }

        return new ChannelBody(raw, null);
    }
}
