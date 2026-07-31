using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Runs scheduled jobs and records what happened.
///
/// A cron service is not a container that stays up: each run is a short-lived container from the
/// service's own image, and the row it leaves behind is the point. "Did it run, did it work, what did
/// it say" are the only questions anyone asks about a scheduled job, and none of them can be answered
/// by a service that merely exists.
///
/// Missed runs are not replayed. A panel that was down for a day should not wake up and fire
/// yesterday's job twenty-four times — it should run the next one on time and leave the gap visible in
/// the history.
/// </summary>
public sealed class CronRunner(IServiceScopeFactory scopeFactory, ILogger<CronRunner> logger) : BackgroundService
{
    /// <summary>Cron's own resolution is a minute, so checking more often buys nothing.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>Kept so one runaway job cannot hold the tick, and so a hung job is visible as failed.</summary>
    private static readonly TimeSpan MaxRunTime = TimeSpan.FromHours(1);

    /// <summary>How much of the job's output is worth keeping per run.</summary>
    private const int MaxOutputChars = 4000;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(Tick);
        do
        {
            try { await TickAsync(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Cron tick failed."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// One pass over every scheduled job: fire what is due, and work out when each is next due.
    /// Public because it is the whole behaviour of this service, and a rule about when a job fires
    /// is not something to find out from production a month later.
    /// </summary>
    public async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var clock = scope.ServiceProvider.GetRequiredService<ISystemClock>();
        var engines = scope.ServiceProvider.GetRequiredService<IServerEngineFactory>();
        var options = scope.ServiceProvider
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<HarboraRuntimeOptions>>().Value;
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();
        var now = clock.UtcNow;

        var jobs = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Where(a => a.Kind == ServiceKind.Cron
                        && a.CronExpression != null && a.CronExpression != ""
                        && a.Status != AppStatus.Stopped)
            .ToListAsync(ct);

        foreach (var job in jobs)
        {
            if (!CronSchedule.TryParse(job.CronExpression, out var schedule, out var error))
            {
                // A schedule that cannot be read is recorded once, not shouted every minute.
                if (job.NextRunAt is not null)
                {
                    job.NextRunAt = null;
                    logger.LogWarning("Cron service {Slug} has an unreadable schedule: {Error}", job.Slug, error);
                    await db.SaveChangesAsync(ct);
                }
                continue;
            }

            // First sight of this job: work out when it is next due and wait for that, rather than
            // treating "never run" as "overdue" and firing immediately.
            if (job.NextRunAt is null)
            {
                job.NextRunAt = schedule!.NextOccurrence(now);
                await db.SaveChangesAsync(ct);
                continue;
            }

            if (job.NextRunAt > now) continue;

            // Advance the schedule BEFORE running. If the run is slow, or this process dies mid-job,
            // the next tick must not start it again.
            job.NextRunAt = schedule!.NextOccurrence(now);
            await db.SaveChangesAsync(ct);

            await RunAsync(db, engines, protector, options, job, clock, ct);
        }
    }

    private async Task RunAsync(
        HarboraDbContext db, IServerEngineFactory engines, ISecretProtector protector,
        HarboraRuntimeOptions options, App job, ISystemClock clock, CancellationToken ct)
    {
        var run = new CronRun
        {
            WorkspaceId = job.WorkspaceId,
            AppId = job.Id,
            StartedAt = clock.UtcNow
        };
        db.CronRuns.Add(run);
        await db.SaveChangesAsync(ct);

        var output = new System.Text.StringBuilder();
        try
        {
            // A prebuilt image is used as configured; anything else runs the image its last successful
            // deployment produced, so a job always runs the code that was actually released.
            var image = job.SourceType == AppSourceType.PrebuiltImage && !string.IsNullOrWhiteSpace(job.PrebuiltImage)
                ? job.PrebuiltImage
                : await db.Deployments
                    .Where(d => d.Id == job.ActiveDeploymentId)
                    .Select(d => d.ImageTag)
                    .FirstOrDefaultAsync(ct);

            if (string.IsNullOrWhiteSpace(image))
                throw new InvalidOperationException(
                    "This job has never been deployed, so there is no image to run. Deploy it once first.");

            var docker = await engines.ResolveAsync(job.ServerId, ct);

            // On its own tenant network, like every other service in the project. Without it a job
            // gets the environment variables naming its database and no route to reach it — which
            // fails in the one way that looks like a credentials problem and is not.
            var workspaceSlug = await db.Workspaces
                .Where(w => w.Id == job.WorkspaceId)
                .Select(w => w.Slug)
                .FirstOrDefaultAsync(ct);
            var network = workspaceSlug is null ? null : options.WorkspaceNetwork(workspaceSlug);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(MaxRunTime);

            var env = job.EnvironmentVariables.ToDictionary(
                v => v.Key,
                v => v.IsSecret ? SafeUnprotect(protector, v.Value) : v.Value);

            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                image!,
                string.IsNullOrWhiteSpace(job.Command) ? [] : ["sh", "-c", job.Command!],
                [],
                Env: env,
                NetworkMode: network),
                // Inline, not Progress<T>: the job's own last lines arrive immediately before the
                // call returns, and an asynchronous hand-off loses exactly them. A run that recorded
                // the image pull and nothing the job printed is the failure this prevents.
                new InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), timeout.Token);

            run.ExitCode = exit;
            if (exit != 0)
                logger.LogWarning("Cron job {Slug} exited {Exit}.", job.Slug, exit);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            run.Error = $"The job was still running after {MaxRunTime.TotalHours:0} hour(s) and was given up on.";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cron job {Slug} could not run.", job.Slug);
            run.Error = LogText.Clean(ex.Message);
        }

        var text = LogText.Clean(output.ToString()).Trim();
        run.Output = text.Length <= MaxOutputChars ? text : "…" + text[^MaxOutputChars..];
        run.FinishedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private static string SafeUnprotect(ISecretProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch { return ""; }
    }
}
