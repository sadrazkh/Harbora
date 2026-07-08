using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Tenancy;

/// <summary>
/// Accumulates per-workspace resource-hours (GB-hours + vCPU-hours) for the current billing month,
/// based on the committed size of running apps. This is the metered basis a provider can invoice on
/// (independent of noisy instantaneous usage — you pay for what you provisioned, like a dyno/droplet).
/// </summary>
public sealed class MeteringService(IServiceScopeFactory scopeFactory, ILogger<MeteringService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Interval);
        do
        {
            try { await MeterAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Metering tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task MeterAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();

        var now = clock.UtcNow;
        var period = new DateOnly(now.Year, now.Month, 1);
        var hours = Interval.TotalHours;
        const double gb = 1024d * 1024 * 1024;

        var committed = await db.Apps.AsNoTracking()
            .Where(a => a.Status == AppStatus.Running)
            .GroupBy(a => a.WorkspaceId)
            .Select(g => new
            {
                WorkspaceId = g.Key,
                Mem = g.Sum(a => (long?)a.MemoryLimitBytes) ?? 0,
                Cpu = g.Sum(a => (double?)a.CpuLimit) ?? 0,
                Count = g.Count()
            })
            .ToListAsync(ct);

        foreach (var w in committed)
        {
            var record = await db.UsageRecords.FirstOrDefaultAsync(r => r.WorkspaceId == w.WorkspaceId && r.Period == period, ct);
            if (record is null)
            {
                record = new UsageRecord { WorkspaceId = w.WorkspaceId, Period = period };
                db.UsageRecords.Add(record);
            }
            record.MemoryGbHours += w.Mem / gb * hours;
            record.CpuCoreHours += w.Cpu * hours;
            record.AppCountPeak = Math.Max(record.AppCountPeak, w.Count);
        }

        if (committed.Count > 0) await db.SaveChangesAsync(ct);
    }
}
