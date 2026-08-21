using Harbora.Application.Abstractions;
using Harbora.Domain.Email;

namespace Harbora.Infrastructure.Email;

/// <summary>
/// Sends a test message through a workspace's own BYO SMTP provider (F6, 2026-08-21
/// functions-and-services plan). Decrypts the stored password exactly once, builds the endpoint the
/// provider's attached apps would actually get, and hands it to <see cref="ISmtpTransport"/>.
///
/// <para>
/// Throws on refusal rather than returning a boolean — the caller (<c>EmailProvidersController</c>)
/// decides what to persist and what to say, but the words in the exception are the provider's own,
/// not a re-interpretation. "Sent" is never said for a refusal.
/// </para>
/// </summary>
public sealed class EmailProviderMailer(ISmtpTransport transport, ISecretProtector protector)
{
    public async Task SendTestAsync(EmailProvider provider, string to, CancellationToken ct)
    {
        var password = protector.Unprotect(provider.EncryptedPassword);
        var endpoint = new SmtpEndpoint(provider.Host, provider.Port, provider.Username, password, provider.UseSsl);

        await transport.SendAsync(
            endpoint,
            EmailProviderEnvKeys.FromHeader(provider),
            to,
            $"Harbora test email via {provider.Name}",
            "If you can read this, the SMTP provider attached to your Harbora apps works.",
            ct);
    }
}
