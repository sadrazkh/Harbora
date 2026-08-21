namespace Harbora.Application.Abstractions;

/// <summary>
/// One message pulled off a queue, waiting for a verdict.
/// </summary>
/// <param name="Body">The message exactly as the broker delivered it — becomes the function's <c>req.Body</c>.</param>
/// <param name="Redelivered">
/// The broker's own flag, true when this exact message has already been handed to a consumer at
/// least once before (including this one, after a prior nack-requeue, or any consumer, after a
/// connection dropped before an ack). F2's "nack-requeue once, then dead-letter" rule reads this
/// rather than keeping its own attempt counter — the broker already knows, and a second counter
/// would just be a second place for the two to drift apart.
/// </param>
public sealed record QueueDelivery(string Body, bool Redelivered);

/// <summary>What a delivery's handler decided, in terms the broker protocol already has words for.</summary>
public enum QueueAckOutcome
{
    /// <summary>The function accepted it. Gone for good.</summary>
    Ack,

    /// <summary>The function failed and this is the message's first attempt (<see cref="QueueDelivery.Redelivered"/>
    /// was false) — put it back for exactly one more try.</summary>
    NackRequeue,

    /// <summary>
    /// The function failed twice, or the message could not be attempted at all. Removed from the
    /// queue either way — never left to redeliver forever — but only after the caller has parked a
    /// row a person can see, per F2's "a message must never vanish silently" rule.
    /// </summary>
    NackDrop
}

/// <summary>Where to reach one broker. Never logged or persisted as a unit — <see cref="Password"/> is a secret.</summary>
public sealed record QueueBrokerAddress(string Host, int Port, string User, string Password);

/// <summary>
/// One open connection to one broker, subscribed to one queue.
///
/// <para>
/// F2 (2026-08-21 functions-and-services plan, "Queue-triggered functions") is the only user: a
/// panel-side <c>BackgroundService</c> holds one of these per enabled queue-triggered function. This
/// is also the seam F2's tests replace with a fake — there is no Docker and no live RabbitMQ on this
/// machine, so nothing about the real AMQP round-trip is provable here, only the consumer's own
/// ack/nack/dead-letter/reconnect behaviour against something shaped exactly like it.
/// </para>
/// </summary>
public interface IQueueBrokerConnection : IAsyncDisposable
{
    /// <summary>
    /// Declares <paramref name="queueName"/> (durable, not exclusive, not auto-delete — so a
    /// publisher or another consumer that reaches it first, or after this one stops, still finds it)
    /// and delivers its messages one at a time (prefetch 1 — this bridge is one panel-side consumer,
    /// not a scaled pool) to <paramref name="handle"/>, acking or nacking exactly as it returns, until
    /// <paramref name="ct"/> is cancelled or the connection is lost — either way this returns (or
    /// throws on a lost connection) rather than silently going quiet, so the caller's reconnect loop
    /// notices.
    /// </summary>
    Task ConsumeAsync(
        string queueName, Func<QueueDelivery, CancellationToken, Task<QueueAckOutcome>> handle, CancellationToken ct);
}

/// <summary>Opens connections. The real implementation speaks AMQP; tests hand the consumer a fake.</summary>
public interface IQueueBrokerConnectionFactory
{
    /// <summary>Throws when the broker cannot be reached — the caller turns that into an Attention fact, not silence.</summary>
    Task<IQueueBrokerConnection> ConnectAsync(QueueBrokerAddress address, CancellationToken ct);
}
