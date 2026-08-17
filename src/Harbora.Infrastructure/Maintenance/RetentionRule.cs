using System.Linq.Expressions;
using Harbora.Domain.Apps;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Nodes;
using Harbora.NodeAgent.Contracts;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Which rows a retention sweep may delete, and by what cutoff.
///
/// <para>
/// Pure and synchronous, for the reason the backup module's <c>RetentionCalculator</c>
/// is: this is the part of the platform that deletes things, so it is the part that most needs to be
/// exercised directly rather than only through a service that has to have a database first. Every
/// member returns an <see cref="Expression"/> so the same predicate a test evaluates in memory is
/// the one the sweeper hands to <c>ExecuteDeleteAsync</c> — there is no second copy to drift.
/// </para>
/// <para>
/// Each rule carries the answer to "how do you know this cannot remove a row a running operation
/// still needs". Those answers are the point of the file; the date arithmetic is incidental.
/// </para>
/// </summary>
public static class RetentionRule
{
    /// <summary>
    /// A cutoff <paramref name="days"/> before <paramref name="now"/>, or <c>null</c> for "keep
    /// forever".
    ///
    /// <para>
    /// <b>Two values mean keep forever.</b> Zero or less, because "never delete" and "delete
    /// everything older than now" differ by the whole table and one of them is unrecoverable — and a
    /// blanked setting, a mistyped key and a section that never got bound all arrive here as 0. And
    /// any span too long to be a date, because that is what somebody reaching for a large integer
    /// meant, and because the alternative is an exception thrown from outside the sweeper's
    /// per-table guard.
    /// </para>
    /// </summary>
    public static DateTimeOffset? CutoffFor(int days, DateTimeOffset now)
    {
        if (days <= 0) return null;

        // A span too long to be a date also means "keep for ever", and it has to be answered here
        // rather than left to the arithmetic. TimeSpan.FromDays gives up past ~10.7 million days
        // and the subtraction underflows past however far "now" is from the start of time — and
        // this is called before the sweeper enters its per-table guard, so either exception used to
        // escape every table's catch and end the whole pass. Deployment logs are swept first, so
        // one operator reaching for a big integer to mean "keep this a very long time" stopped
        // every table being swept, every night, behind a line that named neither table nor key.
        // The two readings agree anyway: nothing in a database is older than the year 1.
        //
        // A day of slack keeps the subtraction off the boundary itself for any clock offset.
        if (days >= (now - DateTimeOffset.MinValue).TotalDays - 1) return null;

        return now - TimeSpan.FromDays(days);
    }

    /// <summary>
    /// The nearest ahead a sweep is ever scheduled.
    ///
    /// <para>
    /// It earns its keep at both ends of the loop, which is why it is not named for start-up: on the
    /// first pass it keeps a delete off the boot path, and on every pass after it stops a sweep that
    /// finished inside its own hour from firing a second time the same night.
    /// </para>
    /// </summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromMinutes(10);

    /// <summary>
    /// How long to wait, from <paramref name="now"/>, for the next sweep at <paramref name="hourUtc"/>.
    ///
    /// <para>
    /// An hour of the day rather than a period since start-up: a 24-hour period counted from boot
    /// runs the sweep at whatever time the panel was last restarted, which is the one time of day
    /// nobody chose — so "nightly" would mean "nightly at 14:20" for an install restarted after
    /// lunch. The first pass on a year-old install is the largest <c>DELETE</c> this platform will
    /// ever issue, and an operator ought to be able to put it somewhere quiet.
    /// </para>
    /// <para>
    /// The window is never less than <see cref="MinimumWait"/> away, so a panel started a few
    /// minutes before its own sweep hour still keeps the delete pass off the boot path — and a sweep
    /// that finishes inside its own hour cannot start again the same night. An hour outside 0–23 is
    /// clamped: a mistyped setting should cost a sweep at an odd time, not the sweeper.
    /// </para>
    /// </summary>
    public static TimeSpan DelayUntilNextSweep(DateTimeOffset now, int hourUtc)
    {
        var next = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero)
            .AddHours(Math.Clamp(hourUtc, 0, 23));

        while (next - now < MinimumWait) next = next.AddDays(1);

        return next - now;
    }

    /// <summary>
    /// Persisted build output past the cutoff, except for deployments named in
    /// <paramref name="protectedDeployments"/>.
    ///
    /// <para>
    /// <b>Safety.</b> Two kinds of deployment are protected by the caller and never appear here: one
    /// that has not reached a terminal status — <c>DeploymentPipeline</c> is still appending lines to
    /// it, and taking the head of a log while its tail is being written leaves a record that reads as
    /// though the build began halfway through — and the one an app is currently running, whose build
    /// output is the only account of what is live. Nothing else reads these rows for a decision:
    /// the three readers (the log view, the API download and the assistant's explanation of a
    /// failure) all display them to a person.
    /// </para>
    /// </summary>
    public static Expression<Func<DeploymentLog, bool>> DeploymentLogsToDelete(
        DateTimeOffset cutoff, IReadOnlyCollection<Guid> protectedDeployments) =>
        log => log.Timestamp < cutoff && !protectedDeployments.Contains(log.DeploymentId);

    /// <summary>
    /// Audit entries past the cutoff.
    ///
    /// <para>
    /// <b>Safety.</b> The table is append-only by design and is read only by the audit page, which
    /// filters and pages over it. No code path takes a decision from an audit row, so removing one
    /// cannot change what the platform does — only what it can still tell you it did. That is why
    /// the risk here is a policy risk rather than a correctness one, and why the default is
    /// documented rather than merely chosen (see <see cref="RetentionOptions.AuditLogDays"/>).
    /// </para>
    /// </summary>
    public static Expression<Func<AuditLog, bool>> AuditLogsToDelete(DateTimeOffset cutoff) =>
        entry => entry.CreatedAt < cutoff;

    /// <summary>
    /// Finished scheduled-job runs whose finish is past the cutoff.
    ///
    /// <para>
    /// <b>Safety.</b> A run with no <c>FinishedAt</c> is not history — it <i>is</i> the mutual
    /// exclusion <c>CronJobRunner.RunAsync</c> takes before starting a container ("is there a run for
    /// this app that has not finished?"). Deleting one would let a second container start alongside
    /// the first, so an unfinished row is never a candidate however old it is. Those rows cannot
    /// accumulate either: <c>CronJobRunner.ReconcileAsync</c> settles every unfinished run at
    /// startup. Age is measured from the finish, not the start, so a long run is judged by when it
    /// stopped mattering.
    /// </para>
    /// </summary>
    public static Expression<Func<CronRun, bool>> CronRunsToDelete(DateTimeOffset cutoff) =>
        run => run.FinishedAt != null && run.FinishedAt < cutoff;

    /// <summary>
    /// Finished function invocations whose finish is past the cutoff.
    ///
    /// <para>
    /// <b>Safety.</b> The same rule as a cron run's, for the same reason: a row with no
    /// <c>CompletedAt</c> is not history, it is a call that has been queued and not yet made. Its job
    /// still holds the row id, so deleting one would leave <c>FunctionInvoker.ExecuteAsync</c> with
    /// nothing to execute and nowhere to record that it could not — a call that vanishes rather than
    /// fails. Age is measured from the completion, so a slow call is judged by when it stopped
    /// mattering. Abandoned rows do not accumulate either: <c>FunctionCronScheduler</c> settles them.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Functions.FunctionInvocation, bool>> FunctionInvocationsToDelete(
        DateTimeOffset cutoff) =>
        invocation => invocation.CompletedAt != null && invocation.CompletedAt < cutoff;

    /// <summary>
    /// Node commands that have finished, were issued before the cutoff, and are not part of the
    /// database-access grant ledger.
    ///
    /// <para>
    /// <b>Safety, three ways.</b>
    /// </para>
    /// <para>
    /// 1. A command that has not reached a terminal status is never swept. The node's ack and result
    /// frames find their row by <c>CommandId</c>; delete the row and the answer to a question this
    /// panel asked arrives to a panel with no record of asking, and is dropped with a warning.
    /// </para>
    /// <para>
    /// 2. <c>CreateDatabaseAccessGrant</c> and <c>RevokeDatabaseAccessGrant</c> are excluded
    /// entirely. <c>NodeTunnelGateway.AuthoriseAsync</c> does not treat them as history: it
    /// authorises a live tunnel by finding the issuing command and refuses it by finding a later
    /// revocation. A permanent grant is legitimately older than any cutoff and still in use, so
    /// sweeping its row would close a working connection at the next reconnect. The pair is bounded
    /// by the number of grants an operator has ever issued, which is small.
    /// </para>
    /// <para>
    /// 3. Age is taken from <c>IssuedAt</c> in the predicate below, and that is what keeps the
    /// exclusion in 2 from being the only thing standing between this sweep and a security
    /// regression. A revocation is always issued after the grant it revokes, so a cutoff that
    /// reaches a revocation has already reached its grant: the pair can only ever disappear
    /// grant-first, which fails closed. Sweeping by <c>CompletedAt</c> would not have that property
    /// — a revocation that completed quickly could outrank a grant that took an hour — and losing a
    /// revocation while its grant survives would re-authorise access somebody deliberately withdrew.
    /// The basis is asserted on this expression itself
    /// (<c>A_command_issued_later_is_never_swept_while_an_earlier_one_survives</c>) rather than on a
    /// parallel member, because a member the delete path never calls pins nothing about the delete.
    /// </para>
    /// </summary>
    public static Expression<Func<NodeCommandRecord, bool>> NodeCommandsToDelete(DateTimeOffset cutoff) =>
        // Status is spelled out rather than going through NodeCommandRecord.IsTerminal: that
        // property is C#, and this expression has to become SQL. The two must agree — if a status is
        // ever appended to the enum, it belongs in both.
        record => record.IssuedAt < cutoff
                  && (record.Status == NodeCommandStatus.Succeeded
                      || record.Status == NodeCommandStatus.Failed
                      || record.Status == NodeCommandStatus.Cancelled
                      || record.Status == NodeCommandStatus.TimedOut
                      || record.Status == NodeCommandStatus.Rejected)
                  && record.Command != NodeCommands.CreateDatabaseAccessGrant
                  && record.Command != NodeCommands.RevokeDatabaseAccessGrant;

    /// <summary>
    /// Node events past the cutoff.
    ///
    /// <para>
    /// <b>Safety.</b> Events are what a node reported unprompted, held so the panel can show a node's
    /// story without polling it. Nothing derives state from them — a node's status, scopes and
    /// capabilities all live on the node row — and the readers take only the most recent handful
    /// (40 on the node page, 50 through the admin API).
    /// </para>
    /// </summary>
    public static Expression<Func<NodeEventRecord, bool>> NodeEventsToDelete(DateTimeOffset cutoff) =>
        record => record.At < cutoff;

    /// <summary>
    /// Idempotency records whose own expiry has passed.
    ///
    /// <para>
    /// <b>Safety.</b> This is the one table that sets its own deadline, and the deadline is the
    /// contract: past <c>ExpiresAt</c> the key may be reused, and <c>IdempotencyStore.FindAsync</c>
    /// already requires <c>ExpiresAt &gt; now</c> and so cannot see the row. Deleting it therefore
    /// changes no answer any client can still be given. Deleting a row before its expiry would be a
    /// duplicated restore, which is why there is no configurable cutoff to get wrong.
    /// </para>
    /// </summary>
    public static Expression<Func<IdempotencyRecord, bool>> IdempotencyRecordsToDelete(DateTimeOffset now) =>
        record => record.ExpiresAt < now;

    /// <summary>
    /// Password-reset tokens that died — by use or by expiry — before the cutoff.
    ///
    /// <para>
    /// <b>Safety.</b> A token is only a candidate once it can no longer work: either it has been
    /// redeemed (<c>UsedAt</c> is set, and a used token never works twice) or its own expiry has
    /// passed. An unused token still inside its window is never touched, whatever the cutoff says —
    /// that row is a live link in somebody's inbox, and deleting it would turn a working reset into
    /// "this link is invalid" with nothing to explain why. The cutoff is measured from the death,
    /// not from issue, so the week is a week of being dead.
    /// </para>
    /// </summary>
    public static Expression<Func<PasswordResetToken, bool>> PasswordResetTokensToDelete(DateTimeOffset cutoff) =>
        token => (token.UsedAt != null && token.UsedAt < cutoff) || token.ExpiresAt < cutoff;

    public static Expression<Func<UserSession, bool>> UserSessionsToDelete(DateTimeOffset now) =>
        session => session.ExpiresAt < now;

    public static Expression<Func<EmailVerificationToken, bool>> EmailVerificationTokensToDelete(DateTimeOffset now) =>
        token => token.ExpiresAt < now;

    /// <summary>
    /// Closed incidents whose close is past the cutoff (2026-08-16 notification-system spec, N1 —
    /// the retention knob M4 shipped the table without).
    ///
    /// <para>
    /// <b>Safety.</b> An incident with <c>ClosedAt == null</c> is still open — the current state of a
    /// condition, read by the bell badge and the timeline — and is never a candidate however old
    /// <c>OpenedAt</c> is. Age is measured from the close, not the open, for the same reason a cron
    /// run's age is measured from its finish: a long-standing incident is judged by when it stopped
    /// mattering, not by when it started.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Monitoring.AlertIncident, bool>> AlertIncidentsToDelete(
        DateTimeOffset cutoff) =>
        incident => incident.ClosedAt != null && incident.ClosedAt < cutoff;

    /// <summary>
    /// Terminal notification deliveries past the cutoff (2026-08-16 notification-system spec, N1).
    ///
    /// <para>
    /// <b>Safety.</b> <c>Pending</c> is not history — it is either a delivery that has not been
    /// attempted yet or one waiting out a retry backoff, and <c>NotificationDeliveryJobHandler</c>
    /// still holds its id in a queued <c>Job</c> row. Only <c>Sent</c>, <c>Failed</c> and
    /// <c>Suppressed</c> are terminal, the same three-way split <c>Job.IsTerminal</c> already uses.
    /// Age is measured from <c>CreatedAt</c>, when the row was queued, matching the audit log's own
    /// choice for the same reason: nothing about a delivery's own lifecycle marks a "finished at" the
    /// way a cron run or a function invocation does.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Notifications.NotificationDelivery, bool>>
        NotificationDeliveriesToDelete(DateTimeOffset cutoff) =>
        delivery => delivery.CreatedAt < cutoff
                    && delivery.Status != Domain.Common.NotificationDeliveryStatus.Pending;

    /// <summary>
    /// Dedup marks past the cutoff (2026-08-16 notification-system spec, N2).
    ///
    /// <para>
    /// <b>Safety.</b> Every row here is inert the moment it is written: <c>AlertDedup.ShouldFireAsync</c>
    /// never reads <c>FiredAt</c>, only whether <c>Key</c> exists, and the window is baked into the key
    /// itself — so a mark whose window has passed cannot be asked about again under the same key. There
    /// is no "still needed" case to protect, unlike every other rule in this file; age is measured from
    /// <c>FiredAt</c> because it is the only timestamp the row carries.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Monitoring.AlertDedupMark, bool>> AlertDedupMarksToDelete(
        DateTimeOffset cutoff) =>
        mark => mark.FiredAt < cutoff;

    /// <summary>
    /// Per-user in-app rows past the cutoff (2026-08-16 notification-system spec, N3).
    ///
    /// <para>
    /// <b>Safety.</b> Unlike a delivery or an incident, nothing about <em>unread</em> makes a row not
    /// yet history — <c>ReadAt</c> is what a person answers "have I seen this" with, not a lock a
    /// running operation still needs, so an old unread row is swept exactly like an old read one. Age
    /// is measured from <c>CreatedAt</c>, the only timestamp every row is guaranteed to carry (most
    /// never get a <c>ReadAt</c> at all). 180 days follows doc 14's own figure for in-app rows.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Notifications.UserNotification, bool>> UserNotificationsToDelete(
        DateTimeOffset cutoff) =>
        n => n.CreatedAt < cutoff;

    /// <summary>
    /// Digest/weekly-report lines past the cutoff (2026-08-16 notification-system spec, N5).
    ///
    /// <para>
    /// <b>Safety.</b> A row with <c>DeliveryId == null</c> is not history — it is exactly what
    /// <c>NotificationDigestRunner</c> is waiting to fold into a delivery, the same "not yet attempted"
    /// guard <c>NotificationDeliveriesToDelete</c> gives <c>Pending</c>. Sweeping it would be the
    /// digest silently losing something it had not said yet, which is the one outcome this whole
    /// sub-project exists to rule out. Age is measured from <c>CreatedAt</c>, the only timestamp a
    /// still-pending row is guaranteed to carry.
    /// </para>
    /// </summary>
    public static Expression<Func<Domain.Notifications.NotificationDigestEntry, bool>>
        NotificationDigestEntriesToDelete(DateTimeOffset cutoff) =>
        entry => entry.DeliveryId != null && entry.CreatedAt < cutoff;
}
