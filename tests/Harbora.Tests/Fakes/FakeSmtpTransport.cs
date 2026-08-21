using Harbora.Infrastructure.Email;

namespace Harbora.Tests.Fakes;

/// <summary>
/// Stands in for a real SMTP server at the exact seam <see cref="ISmtpTransport"/> exists for (F6,
/// 2026-08-21 functions-and-services plan) — the same role <c>FakeDockerEngine</c> plays for a
/// container engine. Records every attempt (<see cref="Sent"/>), and either succeeds or throws
/// whatever <see cref="Refusal"/> is configured, so a test can prove the controller reports the
/// provider's real answer without opening a socket.
/// </summary>
public sealed class FakeSmtpTransport : ISmtpTransport
{
    public sealed record Attempt(SmtpEndpoint Endpoint, string From, string To, string Subject, string Body);

    public List<Attempt> Sent { get; } = [];

    /// <summary>Set to make the next (and every subsequent) send throw this instead of succeeding —
    /// the provider's own refusal, in whatever words a test wants proven through to the page.</summary>
    public Exception? Refusal { get; set; }

    public Task SendAsync(SmtpEndpoint endpoint, string from, string to, string subject, string body, CancellationToken ct)
    {
        Sent.Add(new Attempt(endpoint, from, to, subject, body));
        if (Refusal is { } refusal) throw refusal;
        return Task.CompletedTask;
    }
}
