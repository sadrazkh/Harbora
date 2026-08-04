using Harbora.NodeAgent.Commands;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// Expires old nonces and command outcomes on a timer.
///
/// <para>
/// Without it the ledger only shrinks when a command happens to touch it, so a node that goes
/// quiet after a busy week keeps the whole week on disk and reads all of it on every admission
/// check. The sweep is what keeps an idle node's state file small.
/// </para>
/// </summary>
public sealed class LedgerSweeper(CommandLedger ledger, ILogger<LedgerSweeper> log) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                ledger.Sweep();
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Command ledger sweep failed; will try again in {Interval}.", Interval);
            }
        }
    }
}
