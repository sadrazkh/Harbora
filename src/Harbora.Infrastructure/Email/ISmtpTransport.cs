namespace Harbora.Infrastructure.Email;

/// <summary>What a provider needs to be reached — the plaintext form, resolved once by the caller
/// so <see cref="ISmtpTransport"/> never has to know about <c>ISecretProtector</c>.</summary>
public sealed record SmtpEndpoint(string Host, int Port, string User, string Password, bool UseSsl);

/// <summary>
/// Sends one message over SMTP, honestly (F6, 2026-08-21 functions-and-services plan).
///
/// <para>
/// The seam this codebase has needed since <c>AdminSettingsController.TestSmtp</c>'s own comment:
/// "this codebase has already shipped a Test button that reported success regardless; the lesson is
/// that the button must be capable of saying no." <see cref="SendAsync"/> throws on refusal with the
/// server's own words — it never swallows a failure into a boolean, because a caller that only gets
/// <c>true</c>/<c>false</c> back has already lost the one thing worth showing a customer whose mail
/// never arrives.
/// </para>
///
/// <para>
/// No Docker and no real SMTP server exist on this dev machine, so a live send can never be proven
/// here — <see cref="SystemNetSmtpTransport"/> is exercised only by pointing it at a closed local
/// port (a real connection refusal, not a fake one) in
/// <c>EmailProviderMailerTransportTests</c>. Everywhere else — the controller's honest-refusal
/// behaviour, the "never sent" wording — is proven against <c>Fakes.FakeSmtpTransport</c>, a fake at
/// this exact seam, the same way <c>FakeDockerEngine</c> stands in for a real container engine.
/// </para>
/// </summary>
public interface ISmtpTransport
{
    Task SendAsync(SmtpEndpoint endpoint, string from, string to, string subject, string body, CancellationToken ct);
}

/// <summary>The real transport: <see cref="System.Net.Mail.SmtpClient"/>, exactly the shape
/// <c>PlatformMailer</c> and <c>NotificationService.SendEmail</c>'s per-alert path already use for
/// SMTP — reused here rather than reinvented, parameterised by whichever provider is being tested
/// instead of the platform's own settings row.</summary>
public sealed class SystemNetSmtpTransport : ISmtpTransport
{
    public async Task SendAsync(
        SmtpEndpoint endpoint, string from, string to, string subject, string body, CancellationToken ct)
    {
        using var client = new System.Net.Mail.SmtpClient(endpoint.Host, endpoint.Port)
        {
            EnableSsl = endpoint.UseSsl
        };
        if (!string.IsNullOrWhiteSpace(endpoint.User))
            client.Credentials = new System.Net.NetworkCredential(endpoint.User, endpoint.Password);

        using var message = new System.Net.Mail.MailMessage(from, to, subject, body);
        await client.SendMailAsync(message, ct);
    }
}
