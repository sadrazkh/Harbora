using System.Net.Sockets;
using Harbora.Domain.Jobs;

namespace Harbora.Infrastructure.Jobs;

/// <summary>
/// How long a job may run, how often it may be tried again, and which failures are worth trying
/// again at all. Pure: no clock, no database, no I/O — the worker does all of that and asks here
/// what to do.
///
/// <para>
/// The queue had no answer to any of the three. Nothing bounded a dispatched job, so a
/// <c>docker build</c> hanging against a live daemon ran until the process was killed — and because
/// the worker runs one job at a time, it took every other tenant's deployments, backups and cron
/// runs with it. <c>Job.Attempts</c> was incremented on every claim and read by nobody, so a
/// provision that failed because the daemon happened to be restarting was final.
/// </para>
///
/// <para>
/// The deadlines below are deliberately generous. This is a backstop against a hang, not a service
/// level: where a single bounded operation is the whole job — <c>CronJobRunner.MaxRunTime</c>, the
/// backup module's Kopia command timeout — the deadline here sits above that bound so the inner,
/// more specific message is the one the operator reads. Where the job is a sequence of steps, only
/// some of them bounded, no such ordering can be promised: this is then the only limit the job as a
/// whole has, and it may well pre-empt an inner one. Cutting a legitimately slow deployment short is
/// the worse mistake of the two, because a deployment is never retried.
/// </para>
/// </summary>
public static class JobExecutionPolicy
{
    /// <summary>How long this kind of work may run before the worker gives up on it.</summary>
    public static TimeSpan TimeoutFor(JobKind kind) => kind switch
    {
        // Clone, build, push, start, health-gate, then the release task, in that order. Only the
        // last of those is bounded (HarboraRuntimeOptions.ReleaseTaskTimeoutMinutes, 30 by default),
        // so this is the only limit the pipeline as a whole has — and because it is the outer bound
        // of a sequence, not a backstop behind an inner one, a 35-minute build followed by a stuck
        // release task hits this rather than the release task's own timeout. An operator who raises
        // ReleaseTaskTimeoutMinutes past 60 makes that the ordinary case. An hour is chosen to leave
        // a first build of a large image the half that a default release task does not need.
        JobKind.Deployment => TimeSpan.FromMinutes(60),

        // Pull an image, start it, wait for it to accept connections. Nothing here is slow unless
        // something is wrong.
        JobKind.ServiceProvision => TimeSpan.FromMinutes(15),

        // CronJobRunner bounds the container itself at MaxRunTime (1 hour) and writes a far better
        // sentence about it into the run record. This sits above that so it stays the backstop.
        JobKind.CronRun => TimeSpan.FromMinutes(75),

        // The platform's own backup engine: dump or archive, then upload. It imposes no limit of
        // its own, so this is the only one it will ever have.
        JobKind.Backup => TimeSpan.FromHours(6),

        // The backup module allows a single Kopia snapshot or restore six hours
        // (KopiaOptions.CommandTimeout), around which it also stages and measures files. Seven keeps
        // that command's own timeout the one that fires.
        JobKind.BackupSnapshot => TimeSpan.FromHours(7),
        JobKind.BackupRestore => TimeSpan.FromHours(7),

        // Metadata work: browse, delete, list. Each command is bounded at two minutes; a prune of a
        // long retention chain runs many of them.
        JobKind.BackupVerify => TimeSpan.FromHours(1),
        JobKind.BackupPrune => TimeSpan.FromHours(1),
        JobKind.RepositoryHealthCheck => TimeSpan.FromMinutes(5),

        // A kind appended to the enum without a deadline still gets one. "For ever" is the defect
        // this class exists to remove, so it cannot be the default.
        _ => TimeSpan.FromHours(1)
    };

    /// <summary>
    /// How many times this kind of work may be started in total, counting the first attempt. One
    /// means the worker never retries it on its own.
    /// </summary>
    public static int MaxAttemptsFor(JobKind kind) => kind switch
    {
        // A deployment that failed part-way may have already pushed an image, started a container
        // or repointed the proxy. Re-running it blind compounds whatever went wrong; the operator
        // decides, from a deploy log that still says what happened.
        JobKind.Deployment => 1,

        // A restore writes over live data. Once is already the frightening number.
        JobKind.BackupRestore => 1,

        // A scheduled run has a schedule: the next tick is the retry, and it is the one the user
        // reasoned about. Running it twice for one tick is not.
        JobKind.CronRun => 1,

        JobKind.ServiceProvision => 3,
        JobKind.RepositoryHealthCheck => 3,

        JobKind.Backup => 2,
        JobKind.BackupSnapshot => 2,
        JobKind.BackupVerify => 2,
        JobKind.BackupPrune => 2,

        // Unknown work is assumed to have side effects, which is the safe assumption to be wrong about.
        _ => 1
    };

    /// <summary>
    /// Whether this failure is the kind that a second attempt could get past. Only transient
    /// transport and I/O faults are: a bad Dockerfile fails identically every time, and retrying it
    /// buries the message the operator needs under three copies of itself.
    /// </summary>
    public static bool IsRetryable(Exception? exception) => exception switch
    {
        // A cancellation is the operator stopping the job, the host shutting down, or this worker's
        // own deadline expiring. None of the three is a transient fault, and the deadline case is
        // the important one: a job that spent its entire allowance and finished nothing will spend
        // the whole allowance again.
        OperationCanceledException => false,

        HttpRequestException => true,
        SocketException => true,
        IOException => true,
        TimeoutException => true,

        _ => false
    };

    /// <summary>
    /// How long to wait before the next attempt, given how many have been made. Grows, then holds:
    /// work that has failed three times is not waiting for a longer pause, it is waiting for a person.
    /// </summary>
    public static TimeSpan BackoffFor(int attempts) => attempts switch
    {
        // Defensive lower bound. The caller passes Job.Attempts, which is at least 1 by the time a
        // job can fail; a zero here would mean "claim it again immediately" and spin the worker.
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(30)
    };
}
