using Harbora.NodeAgent.Database;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Closes database grants when their TTL runs out, and re-opens the ones that are still valid after
/// a restart.
///
/// <para>
/// The interval is short because the guarantee is "access ends when it says it ends". A grant that
/// stays open a minute past its expiry is a grant whose expiry the customer cannot rely on, and the
/// whole reason temporary access is preferable to permanent access is that it is trustworthy.
/// </para>
/// </summary>
public sealed class GrantSweeper(DatabaseAccessManager grants, ILogger<GrantSweeper> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Before the sweep loop: a node powered off through a grant's expiry must close it on
            // the way back up, not resume it.
            await grants.RestoreAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception e)
        {
            log.LogError(e, "Could not restore database grants after startup.");
        }

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await grants.SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                // Never fatal: a sweep that throws must not stop the next one, or a grant that
                // could not be closed once would stay open forever.
                log.LogError(e, "Database grant sweep failed; retrying in {Interval}.", Interval);
            }
        }
    }
}
