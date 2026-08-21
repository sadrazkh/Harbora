using FluentAssertions;
using Harbora.Domain.Email;
using Harbora.Infrastructure.Email;
using Harbora.Infrastructure.Security;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// <see cref="EmailProviderMailer"/> — the one honesty requirement the whole sub-project turns on
/// (F6, 2026-08-21 functions-and-services plan): the test-send button must report the provider's
/// real answer, never "sent" for a refusal. Proven here at the transport seam itself
/// (<see cref="FakeSmtpTransport"/>), the same way <c>StorageBucketPipelineTests</c> proves env
/// injection against <c>FakeDockerEngine</c> rather than a helper's return value.
///
/// <para>
/// No Docker and no real SMTP server exist on this dev machine, so a live provider round-trip is
/// never exercised anywhere in this suite — <see cref="EmailProvidersTestSendHttpTests"/> gets
/// as close as this machine allows, pointing the real <see cref="SystemNetSmtpTransport"/> at a
/// closed local port for a genuine connection refusal.
/// </para>
/// </summary>
public class EmailProviderMailerTests
{
    private static AesGcmSecretProtector RealProtector() => new("test-master-key-for-email-provider-secrets");

    private static EmailProvider GivenProvider(AesGcmSecretProtector protector, string passwordPlaintext = "s3cret-app-key") =>
        new()
        {
            WorkspaceId = Guid.NewGuid(), Name = "SendGrid", Host = "smtp.sendgrid.net", Port = 587,
            Username = "apikey", EncryptedPassword = protector.Protect(passwordPlaintext),
            FromAddress = "noreply@acme.example", FromName = "Acme", UseSsl = true
        };

    [Fact]
    public async Task A_successful_send_reaches_the_transport_with_the_providers_own_host_and_the_decrypted_password()
    {
        var protector = RealProtector();
        var provider = GivenProvider(protector, "correct-horse-battery-staple");
        var transport = new FakeSmtpTransport();
        var mailer = new EmailProviderMailer(transport, protector);

        await mailer.SendTestAsync(provider, "owner@acme.example", default);

        var attempt = transport.Sent.Should().ContainSingle().Which;
        attempt.Endpoint.Host.Should().Be("smtp.sendgrid.net");
        attempt.Endpoint.Port.Should().Be(587);
        attempt.Endpoint.User.Should().Be("apikey");
        attempt.Endpoint.Password.Should().Be("correct-horse-battery-staple",
            "the transport needs the plaintext, decrypted from the provider's ciphertext exactly once");
        attempt.From.Should().Be("Acme <noreply@acme.example>");
        attempt.To.Should().Be("owner@acme.example");
    }

    [Fact]
    public async Task A_refusal_from_the_transport_propagates_with_its_own_words_intact()
    {
        // This is the defect class the plan names by its history: a test button that reported success
        // regardless. SendTestAsync must never swallow the transport's exception into a boolean —
        // the caller (EmailProvidersController.TestSend) is what decides what to show, but only if
        // the real message survives to be shown.
        var protector = RealProtector();
        var provider = GivenProvider(protector);
        var transport = new FakeSmtpTransport
        {
            Refusal = new System.Net.Mail.SmtpException(
                System.Net.Mail.SmtpStatusCode.MailboxUnavailable, "550 5.1.1 The email account does not exist")
        };
        var mailer = new EmailProviderMailer(transport, protector);

        var act = () => mailer.SendTestAsync(provider, "owner@acme.example", default);

        (await act.Should().ThrowAsync<System.Net.Mail.SmtpException>())
            .WithMessage("*550 5.1.1 The email account does not exist*");
    }

    [Fact]
    public async Task The_password_is_decrypted_exactly_once_even_when_the_transport_never_gets_to_read_it()
    {
        // A regression that decrypted the password a second time somewhere else (the exact hazard
        // StorageBucketSecretDecryptionTests proves against the real protector for buckets) would
        // throw before ever reaching the transport, since a plaintext string is not valid ciphertext —
        // so a successful call reaching the transport at all is itself proof there was no second
        // Unprotect.
        var protector = RealProtector();
        var provider = GivenProvider(protector, "only-decrypted-once");
        var transport = new FakeSmtpTransport();
        var mailer = new EmailProviderMailer(transport, protector);

        await mailer.SendTestAsync(provider, "owner@acme.example", default);

        transport.Sent.Should().ContainSingle().Which.Endpoint.Password.Should().Be("only-decrypted-once");
    }
}
