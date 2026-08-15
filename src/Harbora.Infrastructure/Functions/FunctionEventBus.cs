using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Functions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Functions;

/// <summary>
/// Hands a platform event to whichever functions asked for it.
///
/// <para>
/// Every call site is something that has just succeeded or just failed at its own job — a deployment
/// finishing, an alert being raised, a webhook arriving. None of them may be broken by this, so
/// nothing here throws: a publish that cannot find its own tables logs and returns, and the thing
/// that published carries on.
/// </para>
///
/// <para>
/// The workspace boundary is the filter, not a courtesy. Subscriptions are matched inside one
/// workspace only, so no customer's code can be woken by another customer's deployment.
/// </para>
/// </summary>
public sealed class FunctionEventBus(
    HarboraDbContext db,
    IFunctionInvoker invoker,
    ILogger<FunctionEventBus> logger) : IFunctionEventBus
{
    public async Task PublishAsync(FunctionEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);

        if (!FunctionEvents.IsKnown(evt.Key))
        {
            // A key nothing can subscribe to is a bug at the call site, not a customer's problem.
            logger.LogWarning("Ignoring function event with unknown key '{Key}'.", evt.Key);
            return;
        }

        try
        {
            // IgnoreQueryFilters because this runs on background paths that have no session, and a
            // filtered read there returns nothing at all — which would look exactly like "nobody
            // subscribed" and never be noticed. The workspace is applied explicitly instead.
            var subscribers = await db.FunctionDefinitions.IgnoreQueryFilters()
                .Where(f => f.WorkspaceId == evt.WorkspaceId
                         && f.Trigger == FunctionTrigger.Event
                         && f.EventKey == evt.Key
                         && f.IsEnabled)
                .Select(f => f.Id)
                .ToListAsync(ct);

            foreach (var id in subscribers)
                await invoker.QueueAsync(id, FunctionTrigger.Event, evt, ct);

            if (subscribers.Count > 0)
                logger.LogInformation("Event {Key} queued for {Count} function(s).", evt.Key, subscribers.Count);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Publishing function event {Key} failed; the caller was not affected.", evt.Key);
        }
    }
}
