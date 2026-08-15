using Harbora.Application.Abstractions;
using Harbora.Domain.Functions;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// A bus that tells nobody.
///
/// <para>
/// For the callers that legitimately have no functions to notify — tests that drive the deployment
/// pipeline, the webhook processor or the notification service directly. It exists so those call
/// sites stay honest: they pass something that visibly does nothing, rather than being handed a
/// nullable dependency that quietly swallows publishing everywhere else too.
/// </para>
/// </summary>
public sealed class NullFunctionEventBus : IFunctionEventBus
{
    public static readonly NullFunctionEventBus Instance = new();

    public Task PublishAsync(FunctionEvent evt, CancellationToken ct) => Task.CompletedTask;
}
