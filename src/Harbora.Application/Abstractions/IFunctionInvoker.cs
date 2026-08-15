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
