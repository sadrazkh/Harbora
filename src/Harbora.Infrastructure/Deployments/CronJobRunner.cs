using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// One execution of a scheduled job, and the row it leaves behind.
///
/// Separate from <see cref="CronRunner"/> — which decides <i>when</i> — because the same execution is
/// wanted on demand: a nightly backup nobody can try until tomorrow is a nightly backup nobody
/// trusts. The schedule and the "run now" button take exactly the same path, so what someone tests
/// by hand is what will happen at 03:00.
/// </summary>
public sealed class CronJobRunner(
    HarboraDbContext db,
    IServerEngineFactory engines,
    ISecretProtector protector,
    IOptions<HarboraRuntimeOptions> options,
    ISystemClock clock,
    ILogger<CronJobRunner> logger)
{
    /// <summary>Kept so one runaway job cannot hold the tick, and so a hung job is visible as failed.</summary>
    public static readonly TimeSpan MaxRunTime = TimeSpan.FromHours(1);

    /// <summary>How much of the job's output is worth keeping per run.</summary>
    private const int MaxOutputChars = 4000;

    /// <summary>Runs the job with this id — the path the "run now" button and the job queue take.</summary>
    public async Task RunAsync(Guid appId, CancellationToken ct)
    {
        var job = await db.Apps.Include(a => a.EnvironmentVariables)
            .FirstOrDefaultAsync(a => a.Id == appId, ct);

        // Checked here and not only where the button is: the queue is durable, so a request can be
        // claimed long after it was made — by which time the service may have been deleted or
        // changed into something that has no business being started as a one-off container.
        if (job is null || job.Kind != ServiceKind.Cron) return;

        await RunAsync(job, manual: true, ct);
    }

    /// <summary>
    /// Runs a job that is already loaded. <paramref name="manual"/> is recorded, because "why did
    /// this run at 14:32 when it is scheduled for 03:00?" is otherwise unanswerable.
    /// </summary>
    public async Task RunAsync(App job, bool manual, CancellationToken ct)
    {
        // One at a time. Without this a held-down button starts a container per press, and a job
        // that takes longer than its own interval overlaps itself. Safe to rely on because an
        // interrupted run is settled at startup rather than left unfinished for ever — see
        // ReconcileAsync.
        if (await db.CronRuns.AnyAsync(r => r.AppId == job.Id && r.FinishedAt == null, ct))
        {
            logger.LogInformation("Cron job {Slug} is already running; this run was skipped.", job.Slug);
            return;
        }

        var run = new CronRun
        {
            WorkspaceId = job.WorkspaceId,
            AppId = job.Id,
            StartedAt = clock.UtcNow,
            IsManual = manual
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
            var network = workspaceSlug is null ? null : options.Value.WorkspaceNetwork(workspaceSlug);

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

    /// <summary>
    /// Settles runs that a restart interrupted. A row with no finish time is shown as still running,
    /// so without this a job killed mid-run is reported as running for ever — and the "one at a time"
    /// guard above would never let it start again.
    /// </summary>
    public async Task ReconcileAsync(CancellationToken ct)
    {
        var stranded = await db.CronRuns.IgnoreQueryFilters()
            .Where(r => r.FinishedAt == null)
            .ToListAsync(ct);
        if (stranded.Count == 0) return;

        foreach (var run in stranded)
        {
            run.FinishedAt = clock.UtcNow;
            run.Error = "Interrupted by a platform restart, so how it ended is not known.";
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Settled {Count} cron run(s) interrupted by a restart.", stranded.Count);
    }

    private static string SafeUnprotect(ISecretProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch { return ""; }
    }
}
