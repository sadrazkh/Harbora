using System.Text;
using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// As much of a <c>Jobs</c> row as the queue rule reads. A projection rather than the entity, so the
/// rule stays a rule: it can be handed rows from a query, from a test, or from anywhere else without
/// dragging a <c>DbContext</c> behind it.
/// </summary>
/// <param name="ExcludesOn">
/// What the row must not share with another job of the same kind running at the same time —
/// <see cref="Job.ExcludesOn"/>, which for a deployment is its <b>app</b> rather than its own target.
/// </param>
/// <param name="CancelRequested">
/// <see cref="Job.CancelRequested"/>. Not a <c>JobClaimQuery</c> term — the SQL claim does not look at
/// it — but a term of the claim <b>path</b> all the same: <c>JobWorker.ClaimNextAsync</c> settles a
/// Pending row carrying it to <c>Cancelled</c> the instant it is claimed, without running it. A row
/// like that is not really ahead of anything, and it does not hold its key either.
/// </param>
public readonly record struct QueuedJob(
    Guid Id,
    JobKind Kind,
    Guid ExcludesOn,
    JobStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? NextAttemptAt = null,
    bool CancelRequested = false);

/// <summary>Why a job has not started. One member per reason the claim would pass it over.</summary>
public enum QueueWait
{
    /// <summary>Not in the queue: already running, already settled, or no row at all.</summary>
    NotQueued,

    /// <summary>
    /// A free slot for everything claimable ahead of it, and one left over for it too — so it is not
    /// necessarily first, only certain to be claimed in this same pass rather than made to wait for
    /// one of them to finish. (Position can still be greater than 1: three claimable jobs ahead with
    /// four free slots is <c>Next</c> at position 4, not first.)
    /// </summary>
    Next,

    /// <summary>Claimable, but older claimable work fills every slot the worker has.</summary>
    Behind,

    /// <summary>
    /// Something it may not run beside holds its place. For a deployment that is another deployment
    /// of the same app, which is the ordinary reason a redeploy waits.
    /// </summary>
    BlockedBySameTarget,

    /// <summary>A failed attempt is serving its backoff, so nothing claims it until it is due.</summary>
    BackingOff
}

/// <summary>Where a job stands, as of the moment it was asked.</summary>
/// <param name="Kind">The job being asked about — what "another one of these" means below.</param>
/// <param name="Position">1-based place among the rows the claim would actually consider; 0 when not queued.</param>
/// <param name="Ahead">Those rows, oldest first, by kind.</param>
/// <param name="Running">Jobs holding a slot right now, whatever they are.</param>
/// <param name="DueAt">When a backing-off job becomes claimable again.</param>
public sealed record QueuePlace(
    QueueWait Wait,
    JobKind Kind,
    int Position,
    IReadOnlyList<JobKind> Ahead,
    int Running,
    DateTimeOffset? DueAt = null);

/// <summary>
/// What a queued deployment is waiting for, worked out from the <c>Jobs</c> table alone.
///
/// <para>
/// A deployment used to sit in <c>Queued</c> saying nothing, and the honest reason it was waiting was
/// never a mystery — it was a row in a table nobody showed. The hard part is not counting rows, it is
/// counting the <b>right</b> ones: every term of <c>JobClaimQuery.Claimable</c> is repeated here, plus
/// <c>CancelRequested</c> from <c>JobWorker.ClaimNextAsync</c>'s own check just after it, so the number
/// a person is shown is the number the worker would arrive at. A position that counted rows the claim
/// will skip over — a snapshot whose target is busy, a job serving a retry backoff, a row about to be
/// cancelled without running — would be a confident, precise, wrong answer, which is worse than none.
/// One thing is <i>not</i> a mirror: the id tiebreak in <see cref="IsBefore"/> stabilises an
/// exact-timestamp collision that the claim's own <c>OrderBy(CreatedAt)</c> leaves Postgres to resolve
/// arbitrarily, so on that one, rare collision this rule's order and the claim's order can disagree.
/// </para>
///
/// <para>
/// And where the exclusion is the reason, it says the exclusion. "Blocked by another deployment of
/// this app" is both truer and more useful than a queue position, because it names something the
/// person can look at; a number tells them to wait for work they cannot see.
/// </para>
///
/// <para>
/// It answers about <i>now</i>. A row blocked behind a busy target is not counted, and it will start
/// counting once its target frees up, so a position can grow. That is what the queue does — reporting
/// a bigger number today to make the display monotonic would be describing a queue the worker is not
/// draining.
/// </para>
/// </summary>
public static class QueuePosition
{
    /// <summary>
    /// Where <paramref name="jobId"/> stands among <paramref name="jobs"/>.
    /// </summary>
    /// <param name="jobs">Every Pending and Running row — platform-wide, because the queue is.</param>
    /// <param name="maxConcurrency">
    /// <c>JobQueueOptions.EffectiveMaxConcurrency</c>. Clamped the same way here: reporting from a
    /// different number than the worker runs on is the class of bug this rule exists to avoid. It is
    /// a per-process limit and the Running rows are the whole platform's, which is the same
    /// single-panel assumption <c>InFlightTargets</c> already states out loud — a second instance
    /// would make this pessimistic, never optimistic.
    /// </param>
    public static QueuePlace For(
        IEnumerable<QueuedJob> jobs, Guid jobId, DateTimeOffset now, int maxConcurrency)
    {
        var all = jobs as IReadOnlyList<QueuedJob> ?? jobs.ToList();
        var running = all.Count(j => j.Status == JobStatus.Running);

        var mine = all.FirstOrDefault(j => j.Id == jobId);
        if (mine.Id != jobId || mine.Status != JobStatus.Pending)
            return new QueuePlace(QueueWait.NotQueued, mine.Kind, 0, [], running);

        // Its own backoff. Nothing signals a backed-off row, so the 5-second backstop poll is what
        // eventually claims it; until then no position it could be given would be true.
        if (mine.NextAttemptAt is { } due && due > now)
            return new QueuePlace(QueueWait.BackingOff, mine.Kind, 0, [], running, due);

        if (IsHeldBySomethingElse(mine, all, now))
            return new QueuePlace(QueueWait.BlockedBySameTarget, mine.Kind, 0, [], running);

        var ahead = all
            .Where(j => j.Id != mine.Id
                        && IsClaimable(j, now)
                        && IsBefore(j, mine)
                        && !IsHeldBySomethingElse(j, all, now))
            .OrderBy(j => j.CreatedAt).ThenBy(j => j.Id)
            .Select(j => j.Kind)
            .ToList();

        var free = Math.Max(0, Math.Max(1, maxConcurrency) - running);

        return new QueuePlace(
            ahead.Count < free ? QueueWait.Next : QueueWait.Behind,
            mine.Kind, ahead.Count + 1, ahead, running);
    }

    /// <summary>
    /// Pending and due — the two terms the claim's WHERE clause has — plus <c>CancelRequested</c>,
    /// which is not a term of that WHERE clause but is a term of what gets run: a Pending row carrying
    /// it is claimed, then settled straight to <c>Cancelled</c> by <c>JobWorker.ClaimNextAsync</c>
    /// without ever running, so it is not really claimable in the sense this rule cares about — it
    /// neither counts as work ahead of anything nor holds the key it excludes on.
    /// </summary>
    private static bool IsClaimable(QueuedJob job, DateTimeOffset now) =>
        job.Status == JobStatus.Pending && !job.CancelRequested &&
        (job.NextAttemptAt is null || job.NextAttemptAt <= now);

    /// <summary>
    /// Oldest first, with the id breaking a tie so the order is total and stable. This tiebreak has no
    /// counterpart in <c>JobClaimQuery.Claimable</c>, which orders by <c>CreatedAt</c> alone — so on an
    /// exact-timestamp collision, Postgres resolves the tie arbitrarily while this always resolves it
    /// the same way. Kept anyway for a rule that gives the same answer twice given the same rows,
    /// which is worth more here than matching an arbitrary database tiebreak nobody could predict either.
    /// </summary>
    private static bool IsBefore(QueuedJob job, QueuedJob other) =>
        job.CreatedAt < other.CreatedAt ||
        (job.CreatedAt == other.CreatedAt && job.Id.CompareTo(other.Id) < 0);

    /// <summary>
    /// Whether another job owns what this one excludes on. Two ways it can: one is running and holds
    /// the key now, or one is claimable and older, in which case oldest-first hands it the key before
    /// this row is ever looked at.
    /// </summary>
    private static bool IsHeldBySomethingElse(
        QueuedJob job, IReadOnlyList<QueuedJob> all, DateTimeOffset now) =>
        all.Any(other => other.Id != job.Id
                         && other.Kind == job.Kind
                         && other.ExcludesOn == job.ExcludesOn
                         && (other.Status == JobStatus.Running ||
                             (IsClaimable(other, now) && IsBefore(other, job))));

    /// <summary>
    /// The same fact as a sentence. Null when there is nothing to say, so a page that asks about a
    /// deployment which is already running renders no explanation rather than an empty box.
    /// </summary>
    public static string? Describe(QueuePlace place, bool persian) => place.Wait switch
    {
        QueueWait.NotQueued => null,

        QueueWait.Next => persian
            ? "بعدی در صف — به‌محض آزاد شدن یک کارگر شروع می‌شود."
            : "Next in the queue — it starts as soon as a worker picks it up.",

        // Ahead can be empty here: running >= EffectiveMaxConcurrency is what makes this Behind rather
        // than Next, and that is the ordinary way a deployment waits — every worker busy with running
        // jobs that are not ahead of this one in the queue at all. Formatting "waiting behind 0 jobs:
        // nothing" there would be a sentence that contradicts its own numbers.
        QueueWait.Behind when place.Ahead.Count == 0 => persian
            ? $"در صف، جایگاه {place.Position} — منتظر آزاد شدن یک کارگر: " +
              $"{place.Running} کار در حال اجراست."
            : $"{Ordinal(place.Position)} in the queue — waiting for a worker to free up: " +
              $"{place.Running} job{(place.Running == 1 ? "" : "s")} running now.",

        QueueWait.Behind => persian
            ? $"در صف، جایگاه {place.Position} — پشت {place.Ahead.Count} کار: {List(place, persian: true)}."
            : $"{Ordinal(place.Position)} in the queue — waiting behind " +
              $"{place.Ahead.Count} job{(place.Ahead.Count == 1 ? "" : "s")}: {List(place, persian: false)}.",

        QueueWait.BlockedBySameTarget => persian
            ? BlockedFa(place.Kind)
            : BlockedEn(place.Kind),

        QueueWait.BackingOff => persian
            ? $"تلاش قبلی ناموفق بود؛ تلاش بعدی ساعت {place.DueAt:HH:mm} (UTC)."
            : $"A previous attempt failed; the next one is due at {place.DueAt:HH:mm} UTC.",

        _ => null
    };

    /// <summary>
    /// What is in front, grouped rather than listed. Eleven queued snapshots of eleven targets is a
    /// fact about the platform; eleven lines of "a backup snapshot" is not a sentence.
    /// </summary>
    private static string List(QueuePlace place, bool persian)
    {
        var groups = new List<(JobKind Kind, int Count)>();
        foreach (var kind in place.Ahead)
        {
            var at = groups.FindIndex(g => g.Kind == kind);
            if (at < 0) groups.Add((kind, 1));
            else groups[at] = (kind, groups[at].Count + 1);
        }

        var parts = groups
            .Select(g => g.Count == 1 ? One(g.Kind, place.Kind, persian) : Many(g.Kind, g.Count, persian))
            .ToList();

        return Join(parts, persian);
    }

    private static string Join(IReadOnlyList<string> parts, bool persian)
    {
        if (parts.Count == 0) return persian ? "هیچ" : "nothing";
        if (parts.Count == 1) return parts[0];

        var conjunction = persian ? " و " : " and ";
        var text = new StringBuilder();
        for (var i = 0; i < parts.Count - 1; i++)
        {
            if (i > 0) text.Append(persian ? "، " : ", ");
            text.Append(parts[i]);
        }
        return text.Append(conjunction).Append(parts[^1]).ToString();
    }

    /// <summary>One of them — "another deployment" when it is the same kind as the job asking.</summary>
    private static string One(JobKind kind, JobKind subject, bool persian) => persian
        ? kind == subject ? $"یک {Noun(kind, persian: true)} دیگر" : $"یک {Noun(kind, persian: true)}"
        : kind == subject ? $"another {Noun(kind, persian: false)}" : $"a {Noun(kind, persian: false)}";

    private static string Many(JobKind kind, int count, bool persian) => persian
        ? $"{count} {Noun(kind, persian: true)}"
        : $"{count} {Noun(kind, persian: false)}s";

    private static string Noun(JobKind kind, bool persian) => kind switch
    {
        JobKind.Deployment => persian ? "استقرار" : "deployment",
        JobKind.Backup => persian ? "پشتیبان‌گیری" : "backup",
        JobKind.ServiceProvision => persian ? "راه‌اندازی سرویس" : "service provision",
        JobKind.CronRun => persian ? "کار زمان‌بندی‌شده" : "scheduled job",
        JobKind.BackupSnapshot => persian ? "اسنپ‌شات پشتیبان" : "backup snapshot",
        JobKind.BackupRestore => persian ? "بازگردانی پشتیبان" : "backup restore",
        JobKind.BackupVerify => persian ? "بررسی پشتیبان" : "backup verification",
        JobKind.BackupPrune => persian ? "هرس پشتیبان" : "backup prune",
        JobKind.RepositoryHealthCheck => persian ? "بررسی سلامت مخزن" : "repository health check",
        _ => persian ? "کار" : "job"
    };

    /// <summary>
    /// The exclusion, named. A deployment excludes on its app; everything else on its own target, so
    /// the two say different things about what to go and look at.
    ///
    /// <para>
    /// Deliberately silent on whether the blocker is running or merely queued ahead of this one —
    /// <see cref="IsHeldBySomethingElse"/> treats an older Pending row on the same key as holding it
    /// too, because oldest-first will hand it the key before this row is ever looked at. "…to finish"
    /// would be true of a running blocker and false of a pending one that has not started; the wording
    /// below is true of both instead of picking one and being wrong half the time.
    /// </para>
    /// </summary>
    private static string BlockedEn(JobKind kind) => kind == JobKind.Deployment
        ? "Blocked by another deployment of this app; only one may run at a time."
        : $"Blocked by another {Noun(kind, persian: false)} of the same target; only one may run at a time.";

    private static string BlockedFa(JobKind kind) => kind == JobKind.Deployment
        ? "مسدود شده به‌دلیل استقرار دیگری از همین برنامه؛ در هر لحظه فقط یکی اجرا می‌شود."
        : $"مسدود شده به‌دلیل {Noun(kind, persian: true)} دیگری روی همین هدف؛ در هر لحظه فقط یکی اجرا می‌شود.";

    /// <summary>"1st", "2nd", "3rd", "11th" — the teens are the reason this is not one line.</summary>
    private static string Ordinal(int n)
    {
        var suffix = (n % 100) is >= 11 and <= 13
            ? "th"
            : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };
        return $"{n}{suffix}";
    }
}
