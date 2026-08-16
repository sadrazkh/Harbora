namespace Harbora.Infrastructure.Notifications;

/// <summary>
/// A channel judged the message rather than merely failing to reach it — <c>EnsureAcceptedAsync</c>'s
/// non-2xx verdict, or the delivery's own timeout budget expiring. Carries the one fact
/// <see cref="Harbora.Infrastructure.Jobs.JobExecutionPolicy.IsRetryable"/> cannot infer from an HTTP
/// status alone once it has already become a message string: whether a second attempt could plausibly
/// get past this.
///
/// <para>
/// N1 (2026-08-16 notification-system spec) §7 Q4(a) — a 5xx or a timeout is the daemon/host having a
/// bad moment, worth trying again; a 4xx is the message itself being refused (a revoked webhook, a
/// wrong chat id, a typo'd URL), and three copies of the same refusal, thirty-one minutes apart, would
/// only bury the one sentence that explains it.
/// </para>
/// </summary>
public sealed class NotificationChannelException(string message, bool isRetryable) : Exception(message)
{
    public bool IsRetryable { get; } = isRetryable;
}
