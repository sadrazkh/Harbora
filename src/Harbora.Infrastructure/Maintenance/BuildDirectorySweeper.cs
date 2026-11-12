using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Removes leftover build directories once a day, unattended (<see cref="BuildDirectorySweep"/>).
///
/// <para>
/// The pipeline removes its own directory when a deployment ends, so on an install running this
/// version the sweep mostly finds nothing. It exists for what was there before: every build an older
/// version ever made is still on the <c>harbora_builds</c> volume, and a panel killed mid-build leaves
/// one more. The first pass is ten minutes after start, like <see cref="DanglingImageSweeper"/>'s and
/// for the same two reasons: off the boot path, and not a full day away on a panel that restarts at
/// least daily — so an upgrade clears an old install's pile ten minutes after it comes up.
/// </para>
///
/// <para>
/// No setting. A directory nothing owns is never read again by anything in the platform, so there is
/// nothing to turn off; Clean up disk runs the same sweep on demand.
/// </para>
///
/// <para>
/// <b>Exactly one line per run</b>, including a run that removed nothing (HARBORA-0062), and it never
/// throws into the host.
/// </para>
/// </summary>
public sealed class BuildDirectorySweeper : BackgroundService
{
    /// <summary>The fixed prefix of the per-run line, for anything that watches the log.</summary>
    internal const string LogPrefix = "Build directory sweep:";

    internal static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<BuildDirectorySweeper> _logger;
    private readonly TimeSpan _startupDelay;

    public BuildDirectorySweeper(IServiceScopeFactory scopeFactory, ILogger<BuildDirectorySweeper> logger)
        : this(scopeFactory, logger, DanglingImageSweeper.DefaultStartupDelay)
    {
    }

    /// <summary>For tests, which must not wait ten minutes.</summary>
    internal BuildDirectorySweeper(
        IServiceScopeFactory scopeFactory, ILogger<BuildDirectorySweeper> logger, TimeSpan startupDelay)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _startupDelay = startupDelay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(_startupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try { await Task.Delay(Interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass, and its one line. Never throws; a shutdown returns null without a line.</summary>
    internal async Task<BuildDirectorySweepResult?> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var result = await scope.ServiceProvider.GetRequiredService<BuildDirectorySweep>().RunAsync(ct);

            _logger.Log(
                result.Failed.Count > 0 ? LogLevel.Warning : LogLevel.Information,
                "Build directory sweep: removed {Removed} build director(ies), {Freed}; kept {Kept} that an " +
                "unfinished deployment owns; failed on {Failed} [{Failures}].",
                result.Removed, Tenancy.ByteSize.Measured(result.FreedBytes), result.Kept,
                result.Failed.Count, result.Failed.Count == 0 ? "none" : string.Join("; ", result.Failed));
            return result;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return null;

            _logger.LogError(ex,
                "Build directory sweep: the run failed before it could finish ({Reason}); it will be tried again " +
                "in a day.", ex.Message);
            return null;
        }
    }
}
