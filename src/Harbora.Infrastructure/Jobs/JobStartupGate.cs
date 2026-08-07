using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// Holds the job worker at the door until startup reconciliation has finished.
///
/// A reconciler is an <see cref="IHostedService"/> whose <c>StartAsync</c> runs to completion, but
/// <see cref="JobWorker"/> is a <see cref="BackgroundService"/> — its <c>StartAsync</c> does not
/// wait for <c>ExecuteAsync</c>, so without this its claim loop runs *alongside* the reconcilers
/// that come after it in registration order. It would then claim a job whose deployment
/// <c>DeploymentReconciler</c> is at that moment marking Failed, and the pipeline would try to move
/// a terminal deployment back to Building.
///
/// Opened by <see cref="JobStartupGateOpener"/>, registered after every startup reconciler.
/// </summary>
public class JobStartupGate
{
    private readonly TaskCompletionSource _opened = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether startup reconciliation has finished. Diagnostics only — waiters use <see cref="WaitAsync"/>.</summary>
    public bool IsOpen => _opened.Task.IsCompleted;

    /// <summary>Idempotent: the second call is a no-op, so a retried or duplicated open is harmless.</summary>
    public void Open() => _opened.TrySetResult();

    /// <summary>
    /// Completes when the gate opens, or throws when <paramref name="ct"/> is cancelled. The token
    /// is what stops this deadlocking a host that gives up before startup finished: the worker is
    /// released by its own stopping token and leaves, rather than waiting for an opener that is
    /// never going to run.
    ///
    /// Virtual only so a test double can observe the moment a waiter parks here — see
    /// <c>ObservableJobStartupGate</c> in the test project's <c>JobHarness</c> — without that
    /// observation ever being reachable from production wiring.
    /// </summary>
    public virtual Task WaitAsync(CancellationToken ct) => _opened.Task.WaitAsync(ct);
}

/// <summary>
/// Opens <see cref="JobStartupGate"/> once every startup reconciler has run. It exists as a hosted
/// service of its own so the ordering requirement is a line in <c>DependencyInjection</c> that can
/// be read and moved, rather than a comment on a reconciler nobody edits.
/// </summary>
public sealed class JobStartupGateOpener(
    JobStartupGate gate, ILogger<JobStartupGateOpener> logger) : IHostedService
{
    public Task StartAsync(CancellationToken ct)
    {
        gate.Open();
        logger.LogInformation("Startup reconciliation finished; the job worker may claim work.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A host that fails or is stopped part-way through startup never reaches the open above. Open
    /// the gate here too: everything waiting on it is also being shut down, and a worker released
    /// into an already-cancelled loop simply exits — whereas one left waiting holds up the stop.
    /// </summary>
    public Task StopAsync(CancellationToken ct)
    {
        gate.Open();
        return Task.CompletedTask;
    }
}
