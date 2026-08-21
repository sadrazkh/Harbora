using Harbora.Domain.Functions;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Calls one function on a running function app, on the platform's own behalf.
///
/// <para>
/// Only the scheduler and the event bus use it: a visitor's HTTP request reaches a function through
/// the proxy like any other traffic and never passes through here.
/// </para>
/// </summary>
public interface IFunctionInvoker
{
    /// <summary>
    /// Queues one call, durably. Returns the invocation id, or null when there was nothing to call —
    /// a disabled function, an app that has never been published, a workspace without the feature.
    /// </summary>
    Task<Guid?> QueueAsync(Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, CancellationToken ct);

    /// <summary>Executes a queued invocation. Idempotent: a completed row is left alone.</summary>
    Task ExecuteAsync(Guid invocationId, CancellationToken ct);

    /// <summary>
    /// Calls one function right now, through the same door as <see cref="QueueAsync"/> +
    /// <see cref="ExecuteAsync"/> (same guard checks, same <c>FunctionInvocation</c> row, same 60s
    /// timeout, same <c>EventKind.FunctionFailed</c> publish on failure) but without the durable job
    /// queue in between, and returns the completed row rather than only its id.
    ///
    /// <para>
    /// F2's queue-trigger consumer (2026-08-21 functions-and-services plan) is the only caller: it
    /// must know pass/fail synchronously to decide whether to ack, nack-requeue, or dead-letter the
    /// broker message, and the durable queue's own worker would answer that question on its own
    /// schedule rather than the consumer's. Returns null under exactly the conditions
    /// <see cref="QueueAsync"/> returns null for — nothing was called, so there is nothing to have a
    /// verdict about.
    /// </para>
    /// </summary>
    /// <param name="body">Handed to the function as <c>req.Body</c> — the queue message's payload.</param>
    Task<FunctionInvocation?> InvokeNowAsync(
        Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, string? body, CancellationToken ct);
}

/// <summary>
/// Tells functions that something happened.
///
/// <para>
/// Publishing must never be able to break the thing that published: a deployment does not fail
/// because a customer's event handler is broken, so every failure here is recorded on the invocation
/// row and swallowed at the call site.
/// </para>
/// </summary>
public interface IFunctionEventBus
{
    /// <summary>Queues a call for every enabled function in that workspace subscribed to this event.</summary>
    Task PublishAsync(FunctionEvent evt, CancellationToken ct);
}
