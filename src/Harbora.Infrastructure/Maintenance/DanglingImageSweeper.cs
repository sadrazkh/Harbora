using Harbora.Infrastructure.Deployments;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Prunes every server's DANGLING images once a day, unattended — the half of disk cleanup that
/// nothing ran on its own.
///
/// <para>
/// Until this existed the only caller of <see cref="DiskCleanupService"/> was the Monitoring page's
/// button, so nothing ran unless a person pressed it, and even pressed it could not see this: it only
/// ever considered Harbora's own tagged build images. On the production server that prompted this class
/// the disk reached 96% (92 of 96 GB) with 165 dangling images — 70 GB — that nothing in the platform
/// could see or remove; one manual <c>docker image prune -f</c> took it to 28%. Every classic-builder
/// rebuild leaves its predecessor's untagged layers behind, so it would have filled again.
/// </para>
///
/// <para>
/// <b>Dangling images only.</b> It calls <see cref="DiskCleanupService.PruneDanglingImagesAsync"/>,
/// which is the daemon's own prune with the dangling filter and nothing else — not orphans, not
/// retention, not any named image. There is no path from here to a tagged image, so build bases and
/// the panel's rollback tags cannot be reached whatever the interval.
/// </para>
///
/// <para>
/// <b>It must never stop the panel.</b> An unhandled exception in a <see cref="BackgroundService"/>
/// stops the host by default, so nothing that can throw is outside a guard: reading the setting, making
/// the scope, and the sweep itself. A failed run is logged and the next one is attempted; only the
/// host's own shutdown ends the loop.
/// </para>
///
/// <para>
/// <b>Once a day, and the first pass soon after boot.</b> <c>Runtime:DanglingImageSweepHours</c>
/// (default 24; <c>0</c> turns it off). The first pass is ten minutes after start rather than a full
/// interval later: a panel that is restarted at least daily — it redeploys itself, and the host reboots
/// — would otherwise restart its own timer every time and never sweep. Ten minutes keeps it off the
/// boot path, where the reconcilers are competing for the daemon and this settles nothing anyone is
/// waiting for.
/// </para>
///
/// <para>
/// <b>Exactly one line per run</b>, including a run that reclaimed nothing — the same rule
/// <see cref="DataRetentionSweeper"/> keeps, and for the same reason (HARBORA-0062): a sweep that logs
/// only when it did something is indistinguishable from a sweep that never ran, and this one would be
/// silent on precisely the installs where it has nothing to do.
/// </para>
/// </summary>
public sealed class DanglingImageSweeper : BackgroundService
{
    /// <summary>The fixed prefix of the per-run line — a monitoring rule keys on it, so it does not
    /// change.</summary>
    internal const string LogPrefix = "Dangling image sweep:";

    /// <summary>Not on the boot path, and not a full interval away — see the class remarks.</summary>
    internal static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(10);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<HarboraRuntimeOptions> _options;
    private readonly ILogger<DanglingImageSweeper> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan? _intervalOverride;

    public DanglingImageSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<HarboraRuntimeOptions> options,
        ILogger<DanglingImageSweeper> logger)
        : this(scopeFactory, options, logger, DefaultStartupDelay, intervalOverride: null)
    {
    }

    /// <summary>
    /// For tests, which must not sleep for real: the same class with its two waits made settable. The
    /// interval override bypasses <see cref="HarboraRuntimeOptions.DanglingImageSweepInterval"/> and
    /// its one-minute floor — the floor is the operator-facing rule and is tested where it lives.
    /// </summary>
    internal DanglingImageSweeper(
        IServiceScopeFactory scopeFactory,
        IOptions<HarboraRuntimeOptions> options,
        ILogger<DanglingImageSweeper> logger,
        TimeSpan startupDelay,
        TimeSpan? intervalOverride)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _logger = logger;
        _startupDelay = startupDelay;
        _intervalOverride = intervalOverride;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Everything before the first await runs synchronously inside the host's StartAsync, so a
        // throw here is a throw while the panel is starting. Reading IOptions<T>.Value is where a
        // setting that cannot be bound to a number ("Runtime__DanglingImageSweepHours=daily") throws.
        TimeSpan? interval;
        try
        {
            interval = _intervalOverride ?? _options.Value.DanglingImageSweepInterval;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dangling image sweep: the interval setting could not be read, so the periodic sweep is not running " +
                "in this process. Fix Runtime:DanglingImageSweepHours (hours; 0 turns it off).");
            return;
        }

        if (interval is null)
        {
            _logger.LogInformation(
                "Dangling image sweep: off (Runtime:DanglingImageSweepHours is 0 or less); no dangling image is pruned " +
                "unless someone presses Clean up disk.");
            return;
        }

        try { await Task.Delay(_startupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try { await Task.Delay(interval.Value, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass: prune every server's dangling images and log exactly one line saying what each
    /// machine did. Public-in-effect (internal) so "what a run does" can be exercised directly rather
    /// than by waiting a day.
    ///
    /// <para>
    /// Never throws — that is the whole contract of this method, and what the loop above relies on to
    /// keep the panel up. A shutdown is the one thing that returns null without a line, since a pass
    /// interrupted by the host stopping is neither a failure nor a sweep.
    /// </para>
    /// </summary>
    internal async Task<DanglingSweepResult?> RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cleanup = scope.ServiceProvider.GetRequiredService<DiskCleanupService>();

            var result = await cleanup.PruneDanglingImagesAsync(ct);
            LogRun(result);
            return result;
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested) return null;

            // The run's one line, in the failure case. The next pass is still scheduled.
            _logger.LogError(ex,
                "Dangling image sweep: the run failed before it could finish ({Reason}); it will be tried again in the " +
                "next interval.", ex.Message);
            return null;
        }
    }

    private void LogRun(DanglingSweepResult result)
    {
        var examined = result.Servers.Count(s => s.NotExamined is null);
        var faulted = result.Servers.Count(s => s.Faulted);

        // A machine that was pruned, one that could not be asked, and one that was asked and failed
        // are three different facts, and the per-server text keeps them apart: only the first is a
        // figure, the others carry their reason. A zero next to "not examined" never appears.
        var perServer = result.Servers.Count == 0
            ? "no servers"
            : string.Join("; ", result.Servers.Select(s =>
                s.NotExamined is null
                    ? $"{s.ServerName}: {s.Removed} image(s), {Tenancy.ByteSize.Measured(s.ReclaimedBytes ?? 0)}"
                    : s.Faulted
                        ? $"{s.ServerName}: failed — {s.NotExamined}"
                        : $"{s.ServerName}: not examined — {s.NotExamined}"));

        // Warning only for a prune that was attempted and failed. A v1 node, or an agent nobody has
        // updated yet, is a steady state: warning about it every day would bury the real ones.
        _logger.Log(
            faulted > 0 ? LogLevel.Warning : LogLevel.Information,
            "Dangling image sweep: pruned {Removed} dangling image(s), the daemons reported {Reclaimed} reclaimed, " +
            "across {Examined} of {Total} server(s) [{PerServer}]; failed on {Faulted} server(s).",
            result.Removed, Tenancy.ByteSize.Measured(result.ReclaimedBytes ?? 0),
            examined, result.Servers.Count, perServer, faulted);
    }
}
