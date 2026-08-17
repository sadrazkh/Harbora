using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Notifications;

/// <summary>How often the digest job runs — N5, 2026-08-16 notification-system spec, "noise control".</summary>
public sealed class NotificationDigestOptions
{
    public const string SectionName = "NotificationDigest";

    /// <summary>Doc 09 §4.2: "hourly job". Every pass is cheap when there is nothing pending — one
    /// query that finds no rows — so an hour costs nothing on a quiet install.</summary>
    public int IntervalMinutes { get; set; } = 60;

    internal TimeSpan Interval => TimeSpan.FromMinutes(Math.Max(1, IntervalMinutes));
}

/// <summary>
/// Runs <see cref="NotificationDigestRunner.RunDigestAsync"/> and
/// <see cref="NotificationDigestRunner.RunWeeklyReportAsync"/> on a timer — N5, 2026-08-16
/// notification-system spec, "noise control". The same shape <c>CertificateWatcher</c> already
/// established: a startup delay so this does not compete with the boot-path reconcilers, then a
/// <see cref="PeriodicTimer"/>, with the actual work in a separately-testable method on a fresh scope
/// per tick.
///
/// <para>
/// Both runs share one tick rather than each owning a timer of their own: the weekly one is cheap to
/// ask about even when nothing is due (one query, filtered to opted-in users past their own cutoff),
/// so a second scheduler would only be a second thing that can silently stop running.
/// </para>
/// </summary>
public sealed class NotificationDigestScheduler(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationDigestOptions> options,
    ILogger<NotificationDigestScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(options.Value.Interval);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "The notification digest pass failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Internal rather than private — exercised directly the same reason
    /// <c>CertificateWatcher.CheckAllAsync</c> is, rather than waiting on the timer.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var runner = scope.ServiceProvider.GetRequiredService<NotificationDigestRunner>();
        await runner.RunDigestAsync(ct);
        await runner.RunWeeklyReportAsync(ct);
    }
}
