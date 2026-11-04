using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Launch;

/// <summary>A database a launch created, as the tick sees it.</summary>
public sealed record LaunchServiceState(Guid Id, string Name, ServiceStatus Status, string? ErrorMessage);

/// <summary>A launch's deployment, as the tick sees it.</summary>
public sealed record LaunchDeploymentState(DeploymentStatus Status, string? ErrorMessage);

/// <summary>Everything the tick needs to know about one launch to decide what happens next.</summary>
/// <param name="LastTouchedAt">The row's last write. While a launch is verifying, only the checks write to it.</param>
/// <param name="ExpectedServices">How many databases were agreed to on the confirm screen.</param>
/// <param name="Services">The ones that exist right now.</param>
/// <param name="HoldsLeftovers">A token or an archive is still on the row.</param>
public sealed record LaunchSnapshot(
    LaunchStatus Status,
    LaunchSourceKind Source,
    DateTimeOffset Now,
    DateTimeOffset StatusChangedAt,
    DateTimeOffset LastTouchedAt,
    DateTimeOffset? VerifyStartedAt,
    Guid? AppId,
    int ExpectedServices,
    IReadOnlyList<LaunchServiceState> Services,
    Guid? DeploymentId,
    LaunchDeploymentState? Deployment,
    bool HoldsLeftovers);

public enum LaunchMoveKind
{
    Stay,
    StartDeploying,
    QueueDeployment,
    StartVerifying,
    RunChecks,
    Fail,
    FailInspection,
    Abandon,
    DropLeftovers
}

/// <param name="FailedStep">For <see cref="LaunchMoveKind.Fail"/>: a key from <see cref="LaunchStepKeys"/>.</param>
public sealed record LaunchMove(
    LaunchMoveKind Kind, string? FailedStep = null, string? Message = null, string? MessageFa = null)
{
    public static readonly LaunchMove Stay = new(LaunchMoveKind.Stay);
}

/// <summary>
/// What the tick does with a launch: a pure function of a snapshot, so every branch of "what happens
/// next" can be tried without a database, a queue or a clock.
/// </summary>
public static class LaunchAdvance
{
    /// <summary>Longer than any provision the job queue would let run, so this only fires for one that was lost.</summary>
    public static readonly TimeSpan ServicesGiveUpAfter = TimeSpan.FromMinutes(20);

    /// <summary>Above the inspection job's own 15-minute deadline: past this, its job was lost.</summary>
    public static readonly TimeSpan InspectingGiveUpAfter = TimeSpan.FromMinutes(20);

    public static readonly TimeSpan ConfirmationLifetime = TimeSpan.FromHours(24);

    /// <summary>
    /// Creating runs on a request and takes seconds. A row still here long after is one whose panel
    /// restarted in the middle: no tick moves it on, because each create judges the signed-in user, so
    /// it is handed back to a person to press retry.
    /// </summary>
    public static readonly TimeSpan CreatingGiveUpAfter = TimeSpan.FromMinutes(30);

    /// <summary>A failed launch holds a retry open this long, and with it a token and an archive.</summary>
    public static readonly TimeSpan RetryLifetime = TimeSpan.FromHours(24);

    /// <summary>Each run can cost a container per database, so one launch asks no more often than this.</summary>
    public static readonly TimeSpan ChecksEvery = TimeSpan.FromSeconds(10);

    /// <summary>How long a check may stay Pending (a certificate being issued) before it counts as failed.</summary>
    public static readonly TimeSpan ChecksPatience = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The verifier settles everything by <see cref="ChecksPatience"/>, so this only fires when it could
    /// not run at all. Without it a launch whose checks keep throwing says "checking" for ever.
    /// </summary>
    public static readonly TimeSpan ChecksGiveUpAfter = TimeSpan.FromMinutes(15);

    public static LaunchMove Decide(LaunchSnapshot launch) => launch.Status switch
    {
        LaunchStatus.Inspecting => launch.Now - launch.StatusChangedAt > InspectingGiveUpAfter
            ? new LaunchMove(LaunchMoveKind.FailInspection, LaunchStepKeys.Source,
                "Reading the source did not finish. Nothing was created. Please try again.",
                "خواندن کد تمام نشد. چیزی ساخته نشد. لطفاً دوباره امتحان کنید.")
            : LaunchMove.Stay,

        LaunchStatus.AwaitingConfirmation => launch.Now - launch.StatusChangedAt > ConfirmationLifetime
            ? new LaunchMove(LaunchMoveKind.Abandon)
            : LaunchMove.Stay,

        LaunchStatus.Creating => launch.Now - launch.StatusChangedAt > CreatingGiveUpAfter
            ? Fail(launch.Services.Count < launch.ExpectedServices ? LaunchStepKeys.Services : LaunchStepKeys.App,
                "The panel stopped while this launch was being created. Try again: only what is missing will be created.",
                "وقتی این راه‌اندازی در حال ساخته شدن بود پنل متوقف شد. دوباره امتحان کنید: فقط چیزهایی که هنوز نیستند ساخته می‌شوند.")
            : LaunchMove.Stay,

        LaunchStatus.WaitingForServices => DecideWaiting(launch),
        LaunchStatus.Deploying => DecideDeploying(launch),
        LaunchStatus.Verifying => DecideVerifying(launch),

        LaunchStatus.Failed => launch.HoldsLeftovers && launch.Now - launch.StatusChangedAt > RetryLifetime
            ? new LaunchMove(LaunchMoveKind.DropLeftovers)
            : LaunchMove.Stay,

        _ => LaunchMove.Stay
    };

    private static LaunchMove DecideWaiting(LaunchSnapshot launch)
    {
        if (launch.AppId is null) return AppGone();

        if (launch.Services.Count < launch.ExpectedServices)
            return Fail(LaunchStepKeys.Services,
                "A database this launch created no longer exists. Create it again from the Databases page, attach it to the app, and redeploy.",
                "دیتابیسی که این راه‌اندازی ساخته بود دیگر وجود ندارد. آن را از صفحهٔ دیتابیس‌ها دوباره بسازید، به اپ وصل کنید و دوباره مستقر کنید.");

        if (launch.Services.FirstOrDefault(s => s.Status == ServiceStatus.Failed) is { } failed)
        {
            var reason = failed.ErrorMessage ?? "It gave no reason.";
            return Fail(LaunchStepKeys.Services,
                $"The database {failed.Name} could not be started: {reason}",
                $"دیتابیس «{failed.Name}» بالا نیامد: {failed.ErrorMessage ?? "دلیلی گزارش نکرد."}");
        }

        if (launch.Services.All(s => s.Status == ServiceStatus.Running))
            return new LaunchMove(LaunchMoveKind.StartDeploying);

        if (launch.Now - launch.StatusChangedAt > ServicesGiveUpAfter)
        {
            var waiting = string.Join(", ", launch.Services.Where(s => s.Status != ServiceStatus.Running).Select(s => s.Name));
            return Fail(LaunchStepKeys.Services,
                $"Gave up waiting: {waiting} did not become ready in {ServicesGiveUpAfter.TotalMinutes:0} minutes.",
                $"انتظار تمام شد: {waiting} در {ServicesGiveUpAfter.TotalMinutes:0} دقیقه آماده نشد.");
        }

        return LaunchMove.Stay;
    }

    private static LaunchMove DecideDeploying(LaunchSnapshot launch)
    {
        if (launch.AppId is null) return AppGone();
        if (launch.DeploymentId is null) return new LaunchMove(LaunchMoveKind.QueueDeployment);

        if (launch.Deployment is null)
            return Fail(LaunchStepKeys.Deploy,
                "The deployment this launch started no longer exists. Deploy again from the app's page.",
                "استقراری که این راه‌اندازی شروع کرده بود دیگر وجود ندارد. از صفحهٔ اپ دوباره مستقر کنید.");

        switch (launch.Deployment.Status)
        {
            case DeploymentStatus.Succeeded:
                return new LaunchMove(LaunchMoveKind.StartVerifying);

            case DeploymentStatus.Failed or DeploymentStatus.Cancelled or DeploymentStatus.RolledBack:
                var happened = launch.Deployment.Status == DeploymentStatus.Failed
                    ? ("The deployment failed", "استقرار شکست خورد")
                    : ("The deployment was stopped", "استقرار متوقف شد");
                var detail = string.IsNullOrWhiteSpace(launch.Deployment.ErrorMessage)
                    ? ""
                    : ": " + launch.Deployment.ErrorMessage;

                // The pipeline consumes an upload on its first attempt, so there is nothing here to try
                // again with. Saying so is what stops a person pressing a button that cannot help.
                var (next, nextFa) = launch.Source == LaunchSourceKind.Upload
                    ? (" The uploaded code was used by that attempt: upload a new version from the app's page.",
                        " کد آپلودشده در همان تلاش مصرف شد: نسخهٔ تازه را از صفحهٔ اپ آپلود کنید.")
                    : ("", "");
                return Fail(LaunchStepKeys.Deploy,
                    $"{happened.Item1}{detail}.{next}", $"{happened.Item2}{detail}.{nextFa}");

            default:
                // Queued, building, or PendingApproval: none of them is a failure, and all of them end
                // by themselves. An approval that is never given expires into Cancelled.
                return LaunchMove.Stay;
        }
    }

    private static LaunchMove DecideVerifying(LaunchSnapshot launch)
    {
        if (launch.AppId is null) return AppGone();

        if (launch.Now - (launch.VerifyStartedAt ?? launch.StatusChangedAt) > ChecksGiveUpAfter)
            return Fail(LaunchStepKeys.Verify,
                "The app is deployed, but its checks could not be finished. Try again, or look at the app's page.",
                "اپ مستقر شده، ولی بررسی‌هایش تمام نشد. دوباره امتحان کنید یا صفحهٔ اپ را ببینید.");

        return launch.Now - launch.LastTouchedAt < ChecksEvery
            ? LaunchMove.Stay
            : new LaunchMove(LaunchMoveKind.RunChecks);
    }

    private static LaunchMove AppGone() => Fail(LaunchStepKeys.App,
        "The app was deleted while it was being launched.",
        "اپ در حین راه‌اندازی حذف شد.");

    private static LaunchMove Fail(string step, string message, string messageFa) =>
        new(LaunchMoveKind.Fail, step, message, messageFa);

    /// <summary>
    /// Whether running the failed step again could help. A deployed app's checks can always be asked
    /// again. An upload whose archive is gone cannot be deployed again, and an old failure with no app
    /// yet has had its token and archive swept, so its app would be made without them.
    /// </summary>
    public static bool CanRetry(AppLaunch launch, DateTimeOffset now)
    {
        if (launch.Status != LaunchStatus.Failed) return false;
        if (launch.FailedStep == LaunchStepKeys.Verify) return launch.AppId is not null;
        if (launch.SourceKind == LaunchSourceKind.Upload && launch.ArchivePath is null) return false;
        return launch.AppId is not null || now - launch.StatusChangedAt <= RetryLifetime;
    }

    public static bool IsTerminal(LaunchStatus status) => status is LaunchStatus.InspectFailed
        or LaunchStatus.Succeeded or LaunchStatus.SucceededWithProblems or LaunchStatus.Failed or LaunchStatus.Abandoned;
}

/// <summary>
/// A launch's one conditional move. Two submits of one confirm screen are two concurrent statements
/// against one row; Postgres serialises them, the loser matches nothing, and only the caller whose
/// update matched goes on to create anything. The same shape as <c>AgentPlanService.TryMoveAsync</c>,
/// with the same caveat: EF InMemory has no <c>ExecuteUpdateAsync</c>, so there it is a plain
/// read-then-write that is not atomic, and the race is proven only in the Postgres lane
/// (<c>LaunchConfirmRaceTests</c>).
/// </summary>
public static class LaunchMoves
{
    public static async Task<bool> TryMoveAsync(
        HarboraDbContext db, Guid launchId, LaunchStatus from, LaunchStatus to, DateTimeOffset now, CancellationToken ct)
    {
        var movable = db.AppLaunches.IgnoreQueryFilters().Where(l => l.Id == launchId && l.Status == from);

        if (db.Database.IsRelational())
            return await movable.ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Status, to)
                .SetProperty(l => l.StatusChangedAt, now)
                .SetProperty(l => l.UpdatedAt, now), ct) > 0;

        var row = await movable.FirstOrDefaultAsync(ct);
        if (row is null) return false;

        row.Status = to;
        row.StatusChangedAt = now;
        await db.SaveChangesAsync(ct);
        db.Entry(row).State = EntityState.Detached;
        return true;
    }
}
