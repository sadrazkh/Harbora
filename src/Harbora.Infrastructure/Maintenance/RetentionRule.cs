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
    /// Zero has to mean "never delete" rather than "delete everything older than now", because those
    /// two readings differ by the whole table and one of them is unrecoverable. A blanked setting,
    /// a mistyped key and a section that never got bound all arrive here as 0.
    /// </para>
    /// </summary>
    public static DateTimeOffset? CutoffFor(int days, DateTimeOffset now) =>
        days <= 0 ? null : now - TimeSpan.FromDays(days);

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
    /// The age a node command is judged by: when it was issued.
    ///
    /// <para>
    /// Exposed as its own member because the choice is load-bearing rather than cosmetic — see
    /// <see cref="NodeCommandsToDelete"/>.
    /// </para>
    /// </summary>
    public static DateTimeOffset NodeCommandAgeBasis(NodeCommandRecord record) => record.IssuedAt;

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
    /// 3. Age is taken from <c>IssuedAt</c>, and that is what keeps the exclusion in 2 from being
    /// the only thing standing between this sweep and a security regression. A revocation is always
    /// issued after the grant it revokes, so a cutoff that reaches a revocation has already reached
    /// its grant: the pair can only ever disappear grant-first, which fails closed. Sweeping by
    /// <c>CompletedAt</c> would not have that property — a revocation that completed quickly could
    /// outrank a grant that took an hour — and losing a revocation while its grant survives would
    /// re-authorise access somebody deliberately withdrew.
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
    /// capabilities all live on the node row — and the only reader takes the most recent 40.
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
}
