using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// At startup, settles every assistant plan a restart left <c>Executing</c>
/// (<see cref="AgentPlanService.FailInterruptedAsync(HarboraDbContext, DateTimeOffset, ILogger, CancellationToken)"/>).
/// The panel is one process, so nothing can still be running them; without this, each would stay
/// "executing" for good, and its conversation could never be deleted — by the person or by retention.
/// Shaped like <c>JobReconciler</c>: it never blocks startup on a failure, it says what it settled, and it
/// runs with no session.
/// </summary>
public sealed class AgentPlanStartupReconciler(
    IServiceScopeFactory scopeFactory,
    ISystemClock clock,
    ILogger<AgentPlanStartupReconciler> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try { await ReconcileAsync(ct); }
        catch (Exception ex)
        {
            // Never block startup on this: a plan left executing only keeps its conversation, and the next
            // start tries again.
            logger.LogError(ex, "Settling assistant plans left executing by a restart failed on startup.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task<int> ReconcileAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var settled = await AgentPlanService.FailInterruptedAsync(db, clock.UtcNow, logger, ct);
        if (settled > 0)
            logger.LogWarning("Settled {Count} assistant plan(s) a restart left executing; their running step is marked failed.", settled);
        return settled;
    }
}
