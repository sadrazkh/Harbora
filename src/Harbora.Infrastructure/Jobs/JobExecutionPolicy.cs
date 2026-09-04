using System.Net.Mail;
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
        JobKind.BillingHour => TimeSpan.FromMinutes(30),

        // One channel attempt, bounded by NotificationOptions.DeliveryTimeout (10s by default) well
        // inside this. Generous anyway, the same way every deadline here is: this is the backstop
        // behind that budget, not a competing one.
        JobKind.NotificationDelivery => TimeSpan.FromMinutes(2),

        // One webhook POST or one Telegram send (P6, 2026-08-20 platform-options plan). Same shape
        // as NotificationDelivery immediately above, and the same reasoning: this is the backstop
        // behind NotificationOptions.DeliveryTimeout, not a competing budget.
        JobKind.EventDelivery => TimeSpan.FromMinutes(2),

        // VACUUM FULL/REINDEX rewrite the table/index and can legitimately run for a long time on a
        // large one; the statement itself imposes no timeout of its own, so this is the only backstop
        // it will ever have. Generous for the same reason JobKind.Backup's own six hours is.
        JobKind.DatabaseMaintenance => TimeSpan.FromHours(6),

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
        // BillingRunHandler records an incomplete result and the scheduler offers it again. A job
        // retry here would create a second retry loop with a different clock.
        JobKind.BillingHour => 1,

        // §7 Q4(a): widened here rather than given a second retry mechanism on the delivery row
        // itself — one notion of "how many times do we try". Three because R-13's own worked example
        // is a transient 502; one attempt was the defect this exists to fix, and a channel still
        // refusing after three, thirty-one minutes apart, is not going to be fixed by a fourth.
        JobKind.NotificationDelivery => 3,

        // Same budget as NotificationDelivery immediately above, and the same §7 Q4(a) reasoning —
        // a webhook endpoint or Telegram chat still refusing after three attempts, thirty-one
        // minutes apart, needs a person, not a fourth try.
        JobKind.EventDelivery => 3,

        // VACUUM FULL in particular takes an ACCESS EXCLUSIVE lock; a run the worker killed at its own
        // deadline may have been mid-rewrite, and retrying that blind is not obviously safer than
        // leaving it for an operator to look at and press "run now" on again. The run's own row already
        // records the failure with the engine's own words, so nothing is lost by stopping at one.
        JobKind.DatabaseMaintenance => 1,

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

        // §7 Q4(a): the channel senders' own verdict — a 5xx or the delivery's timeout budget, both
        // worth a second attempt; a 4xx, which is not (see NotificationChannelException's own doc).
        Notifications.NotificationChannelException ex => ex.IsRetryable,

        // The platform's own SMTP, for the same reason: most SmtpException codes are the server
        // having a bad moment (mailbox busy, insufficient storage, "try again"); a handful are the
        // message itself being refused for good, and retrying those buries the one useful line.
        SmtpException ex => !IsPermanentSmtpFailure(ex.StatusCode),

        _ => false
    };

    /// <summary>
    /// The classic permanent SMTP replies — a mailbox that does not exist, a domain the server will
    /// never deliver to, a message it has already rejected outright. Everything else
    /// <see cref="SmtpStatusCode"/> can carry (busy, out of space, a generic failure) is at least
    /// plausibly transient, and the safer wrong guess is one more attempt, not a message given up on
    /// after a single try.
    /// </summary>
    private static bool IsPermanentSmtpFailure(SmtpStatusCode code) => code switch
    {
        SmtpStatusCode.MailboxUnavailable => true,
        SmtpStatusCode.MailboxNameNotAllowed => true,
        SmtpStatusCode.UserNotLocalTryAlternatePath => true,
        SmtpStatusCode.TransactionFailed => true,
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
