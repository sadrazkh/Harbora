using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// What a worker is allowed to claim, expressed as a query the <b>database</b> evaluates.
///
/// <para>
/// It lives here, apart from <see cref="JobWorker"/>, for one reason: every term in it has to be
/// translatable by <b>Npgsql</b>, and nothing about the C# says so. The queue's tests run on the
/// in-memory provider, which accepts plenty that Postgres does not — an overload taking a
/// <c>StringComparison</c>, to name the classic one — so a predicate can pass the entire suite and
/// then throw on the first claim in production. The worker catches that throw so the loop survives,
/// which means the only symptom is "Job worker loop failed" repeating once per poll while the panel
/// goes on accepting deployments and runs none of them — the worst shape a failure can take here.
/// Pulled out, the exact query the worker runs can be handed to a real Npgsql provider and asked to
/// produce its SQL, with no database in the room. See <c>JobClaimQueryTests</c>.
/// </para>
/// </summary>
public static class JobClaimQuery
{
    /// <summary>
    /// The rows that may be claimed right now, oldest first.
    /// </summary>
    /// <param name="jobs">The job table.</param>
    /// <param name="now">Used to leave a job serving a retry backoff alone until it is due.</param>
    /// <param name="inFlight">
    /// What this process is already running, by the pair each running job excludes on. Rows matching
    /// one of these are removed from the search rather than skipped over afterwards, so oldest-first
    /// goes on to pick the next row that <i>can</i> run instead of the worker idling behind work it
    /// is not allowed to start.
    /// </param>
    public static IQueryable<Job> Claimable(
        IQueryable<Job> jobs, DateTimeOffset now, IEnumerable<(JobKind Kind, Guid ExcludesOn)> inFlight)
    {
        // A job serving a backoff is Pending but not yet due; claiming it anyway would turn the
        // backoff into a retry loop.
        var claimable = jobs.Where(j => j.Status == JobStatus.Pending &&
                                        (j.NextAttemptAt == null || j.NextAttemptAt <= now));

        // One term per held pair rather than a set membership test over the id, because the pair is
        // what excludes: a backup of one thing and a deployment of another are different work
        // whatever their guids happen to be. The list is bounded by the worker's concurrency limit,
        // so this stays a handful of AND terms.
        //
        // The coalesce is Job.ExcludesOn spelled out rather than used. The property is the same rule
        // and is the right thing to say everywhere else, but it is not mapped, so no provider can
        // render it — this is the one place the rule has to be written as data.
        foreach (var (kind, excludesOn) in inFlight)
            claimable = claimable.Where(j => j.Kind != kind || (j.ExclusiveWith ?? j.TargetId) != excludesOn);

        return claimable.OrderBy(j => j.CreatedAt);
    }
}
