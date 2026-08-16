using System.Linq.Expressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>The tables this sweep is responsible for, named the way it reports them.</summary>
public static class RetentionTables
{
    public const string DeploymentLogs = "DeploymentLogs";
    public const string AuditLogs = "AuditLogs";
    public const string CronRuns = "CronRuns";
    public const string FunctionInvocations = "FunctionInvocations";
    public const string NodeCommands = "NodeCommands";
    public const string NodeEvents = "NodeEvents";
    public const string IdempotencyRecords = "IdempotencyRecords";
    public const string PasswordResetTokens = "PasswordResetTokens";
    public const string UserSessions = "UserSessions";
    public const string EmailVerificationTokens = "EmailVerificationTokens";
    public const string AlertIncidents = "AlertIncidents";
    public const string NotificationDeliveries = "NotificationDeliveries";
}

/// <summary>
/// What one sweep did, per table.
///
/// <para>
/// Three outcomes, kept apart on purpose: a table was swept and lost <i>n</i> rows, a table is
/// configured to keep everything, or a table failed. Collapsing those into one number is how a sweep
/// comes to report success for work it never did — a table missing from <see cref="Deleted"/> would
/// otherwise be indistinguishable from one that had nothing to delete.
/// </para>
/// </summary>
public sealed record RetentionSweepResult(
    IReadOnlyDictionary<string, int> Deleted,
    IReadOnlyCollection<string> KeptForever,
    IReadOnlyDictionary<string, string> Failures)
{
    public int TotalDeleted => Deleted.Values.Sum();
}

/// <summary>
/// Keeps the platform's append-only tables from growing without end (HARBORA-0012).
///
/// <para>
/// Nine tables need bounded retention: build logs, the audit trail, cron-run history, the node
/// command and event records, idempotency keys, password-reset tokens, browser sessions, and email
/// verification tokens. On an install running for
/// a year the first of those alone is every line of every build ever made. The platform already did
/// this properly for metrics — <c>MetricsCollector</c> trims raw samples at 24 h, <c>MetricsRollupService</c>
/// trims summaries at 31 and 365 days — and this is the same idea applied to the rest.
/// </para>
///
/// <para>
/// <b>What it will not do.</b> Every decision about which rows go lives in <see cref="RetentionRule"/>,
/// which is pure and tested on its own; this class only carries those predicates to the database.
/// Each rule documents why it cannot take a row a running operation still needs — an unfinished cron
/// run is a lock, an in-flight node command is waiting for its own answer, an unused reset token is
/// a live link, and the database-access grant ledger is an authorisation record rather than history.
/// </para>
///
/// <para>
/// <b>Audit retention is a policy decision, not a technical one.</b> The shipped default keeps audit
/// entries for a year, which is a guess about your obligations. An operator with a compliance
/// requirement — SOC 2, PCI DSS, ISO 27001, a contract, a national record-keeping rule — must set
/// <c>Retention:AuditLogDays</c> deliberately instead of inheriting it. <c>0</c> keeps them for ever.
/// Every other cutoff is configurable the same way, and <c>0</c> always means keep for ever.
/// </para>
///
/// <para>
/// <b>One failure does not end the sweep.</b> Each table is swept inside its own try/catch, because
/// a single locked or damaged table ending the pass would leave the other eight growing for ever while
/// the logs mentioned only the ninth. The same goes for a value that cannot be a cutoff: a span of
/// days too long to be a date is read as "keep for ever" by <see cref="RetentionRule.CutoffFor"/>
/// rather than thrown, because that arithmetic happens before the per-table guard is entered.
/// </para>
///
/// <para>
/// <b>Once a night, at an hour you choose.</b> <c>Retention:SweepHourUtc</c> (default 03:00 UTC)
/// decides when, so the largest delete pass an install ever runs — its first — can be put somewhere
/// quiet instead of landing at whatever time the panel was last restarted.
/// </para>
/// </summary>
public sealed class DataRetentionSweeper(
    IServiceScopeFactory scopeFactory,
    IOptions<RetentionOptions> options,
    ISystemClock clock,
    ILogger<DataRetentionSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // An hour of the night, not a period since boot. A 24-hour period started ten minutes
            // after start-up means the sweep runs at whatever time this panel was last restarted,
            // which is the one time of day nobody chose — and the first pass on an old install is
            // the largest DELETE this platform will ever issue. RetentionRule.DelayUntilNextSweep
            // also keeps the first one clear of the boot path, where it settles nothing anyone is
            // waiting for and would only compete with the reconcilers.
            try { await Task.Delay(RetentionRule.DelayUntilNextSweep(clock.UtcNow, options.Value.SweepHourUtc), stoppingToken); }
            catch (OperationCanceledException) { return; }

            try { await SweepAsync(stoppingToken); }
            catch (Exception ex)
            {
                // Shutdown is not a sweep failure, for the same reason it is not a table failure
                // one frame down: the per-table guard deliberately re-raises what it sees when the
                // token is cancelled, and without the same guard here that landed as "the sweep
                // failed" — an error on every stop that caught a sweep in progress.
                if (stoppingToken.IsCancellationRequested) return;

                logger.LogError(ex, "The data retention sweep failed.");
            }
        }
    }

    /// <summary>
    /// One pass over every table. Public so "which rows go" can be exercised directly rather than by
    /// waiting a day and hoping.
    /// </summary>
    public async Task<RetentionSweepResult> SweepAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var now = clock.UtcNow;
        var config = options.Value;

        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        var keptForever = new List<string>();
        var failures = new Dictionary<string, string>(StringComparer.Ordinal);

        // One table. The rule is built INSIDE the try on purpose: the deployment-log rule has to read
        // its protection set first, and a failure to work out what must be protected has to fail the
        // sweep of that table rather than fall through to a delete that protects nothing.
        async Task SweepTableAsync<TEntity>(
            string table, Func<Task<Expression<Func<TEntity, bool>>>> ruleFor)
            where TEntity : class
        {
            try
            {
                deleted[table] = await DeleteAsync(db, await ruleFor(), ct);
            }
            // Shutdown is not a table failure. Without the guard, stopping the panel mid-sweep would
            // record nine "failures" and log nine errors about a sweep that was simply asked to stop.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Named, counted and stepped over. The next table is the one that matters now.
                failures[table] = ex.Message;
                logger.LogError(ex, "Retention sweep of {Table} failed; the remaining tables were still swept.", table);
            }
        }

        // A table swept by a configured age, which the operator may turn off entirely — or turn off
        // by accident, by typing a number too long to be a date.
        async Task SweepAgedTableAsync<TEntity>(
            string table, string key, int days,
            Func<DateTimeOffset, Task<Expression<Func<TEntity, bool>>>> ruleFor)
            where TEntity : class
        {
            if (RetentionRule.CutoffFor(days, now) is not { } cutoff)
            {
                keptForever.Add(table);

                // Said out loud, not merely recorded. RetentionSweepResult keeps "kept" apart from
                // "swept" so that a table nobody swept can never pass for a table with nothing to
                // sweep — but the result itself goes nowhere: the timer loop discards it, and the
                // nightly line counts only what was deleted. A table absent from that line is
                // exactly the thing this has to make visible, so it goes in the log.
                if (days > 0)
                    // Nobody means this. The value was read as "keep for ever" so that it could not
                    // end the whole sweep, and this is the other half of that bargain: a table has
                    // silently stopped being bounded, and the line has to name the setting to edit
                    // and the value that was typed, or the operator is hunting for it.
                    logger.LogWarning(
                        "Retention of {Table} is set to {Days} days, which is too long to be a date, so " +
                        "nothing is being deleted from it. Set {Setting} to a number of days it can keep, " +
                        "or to 0 if you meant to keep everything for ever.",
                        table, days, $"{RetentionOptions.SectionName}:{key}");
                else
                    logger.LogInformation(
                        "Retention of {Table} is off ({Setting} is {Days}); every row is being kept.",
                        table, $"{RetentionOptions.SectionName}:{key}", days);

                return;
            }

            await SweepTableAsync(table, () => ruleFor(cutoff));
        }

        await SweepAgedTableAsync<DeploymentLog>(RetentionTables.DeploymentLogs, nameof(RetentionOptions.DeploymentLogDays), config.DeploymentLogDays,
            async cutoff => RetentionRule.DeploymentLogsToDelete(cutoff, await ProtectedDeploymentsAsync(db, ct)));

        await SweepAgedTableAsync<Domain.Auditing.AuditLog>(RetentionTables.AuditLogs, nameof(RetentionOptions.AuditLogDays), config.AuditLogDays,
            cutoff => Task.FromResult(RetentionRule.AuditLogsToDelete(cutoff)));

        await SweepAgedTableAsync<Domain.Apps.CronRun>(RetentionTables.CronRuns, nameof(RetentionOptions.CronRunDays), config.CronRunDays,
            cutoff => Task.FromResult(RetentionRule.CronRunsToDelete(cutoff)));

        // The highest-frequency table here: one row per scheduled or event-driven call, so a
        // one-minute function writes 1,440 a day by itself.
        await SweepAgedTableAsync<Domain.Functions.FunctionInvocation>(
            RetentionTables.FunctionInvocations, nameof(RetentionOptions.FunctionInvocationDays), config.FunctionInvocationDays,
            cutoff => Task.FromResult(RetentionRule.FunctionInvocationsToDelete(cutoff)));

        await SweepAgedTableAsync<Domain.Nodes.NodeCommandRecord>(RetentionTables.NodeCommands, nameof(RetentionOptions.NodeCommandDays), config.NodeCommandDays,
            cutoff => Task.FromResult(RetentionRule.NodeCommandsToDelete(cutoff)));

        await SweepAgedTableAsync<Domain.Nodes.NodeEventRecord>(RetentionTables.NodeEvents, nameof(RetentionOptions.NodeEventDays), config.NodeEventDays,
            cutoff => Task.FromResult(RetentionRule.NodeEventsToDelete(cutoff)));

        // No configured age, and so no "keep forever" either: the row's own ExpiresAt is the
        // deadline, and a row past it is already invisible to the store that wrote it.
        await SweepTableAsync<IdempotencyRecord>(RetentionTables.IdempotencyRecords,
            () => Task.FromResult(RetentionRule.IdempotencyRecordsToDelete(now)));

        await SweepAgedTableAsync<Domain.Identity.PasswordResetToken>(
            RetentionTables.PasswordResetTokens, nameof(RetentionOptions.PasswordResetTokenDays),
            config.PasswordResetTokenDays,
            cutoff => Task.FromResult(RetentionRule.PasswordResetTokensToDelete(cutoff)));

        // Both records carry a short, authoritative expiry of their own. Live rows are required for
        // authentication; expired rows are already unusable and must not grow these tables forever.
        await SweepTableAsync<Domain.Identity.UserSession>(RetentionTables.UserSessions,
            () => Task.FromResult(RetentionRule.UserSessionsToDelete(now)));
        await SweepTableAsync<Domain.Identity.EmailVerificationToken>(RetentionTables.EmailVerificationTokens,
            () => Task.FromResult(RetentionRule.EmailVerificationTokensToDelete(now)));

        // N1/M4 (2026-08-16 notification-system spec): the retention knob AlertIncident shipped
        // without, and the delivery log's own table.
        await SweepAgedTableAsync<Domain.Monitoring.AlertIncident>(
            RetentionTables.AlertIncidents, nameof(RetentionOptions.AlertIncidentDays), config.AlertIncidentDays,
            cutoff => Task.FromResult(RetentionRule.AlertIncidentsToDelete(cutoff)));

        await SweepAgedTableAsync<Domain.Notifications.NotificationDelivery>(
            RetentionTables.NotificationDeliveries, nameof(RetentionOptions.NotificationDeliveryDays),
            config.NotificationDeliveryDays,
            cutoff => Task.FromResult(RetentionRule.NotificationDeliveriesToDelete(cutoff)));

        var result = new RetentionSweepResult(deleted, keptForever, failures);

        if (result.TotalDeleted > 0)
            logger.LogInformation("Retention sweep removed {Count} row(s): {Breakdown}.",
                result.TotalDeleted, string.Join(", ", deleted.Where(d => d.Value > 0).Select(d => $"{d.Key} {d.Value}")));

        return result;
    }

    /// <summary>
    /// Deployments whose build output must survive whatever the cutoff says: everything that has not
    /// finished, and whatever each app is currently running.
    ///
    /// <para>
    /// Read with <c>IgnoreQueryFilters</c> and materialised. A filtered read here would return an
    /// empty protection set and hand the delete a clean conscience about the very rows it must not
    /// touch — the failure mode being unprotected, not merely unfound. Both sets are small: one row
    /// per app plus whatever is in flight.
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyCollection<Guid>> ProtectedDeploymentsAsync(
        HarboraDbContext db, CancellationToken ct)
    {
        var unfinished = await db.Deployments.IgnoreQueryFilters().AsNoTracking()
            .Where(d => d.Status == DeploymentStatus.Queued
                        || d.Status == DeploymentStatus.Building
                        || d.Status == DeploymentStatus.Pushing
                        || d.Status == DeploymentStatus.Deploying
                        || d.Status == DeploymentStatus.HealthChecking)
            .Select(d => d.Id)
            .ToListAsync(ct);

        var live = await db.Apps.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.ActiveDeploymentId != null)
            .Select(a => a.ActiveDeploymentId!.Value)
            .ToListAsync(ct);

        return unfinished.Union(live).ToHashSet();
    }

    /// <summary>
    /// Deletes everything the rule selects.
    ///
    /// <para>
    /// <c>IgnoreQueryFilters</c> on every table, not only the two that carry a workspace filter
    /// (<c>CronRun</c> and <c>IdempotencyRecord</c>). A sweeper has no session, and this codebase has
    /// already shipped four separate bugs where a filtered read from a sessionless path found
    /// nothing and reported a clean pass over it. Relying on the ambient scope happening to be
    /// unscoped is the same bet, made silently.
    /// </para>
    /// <para>
    /// <c>ExecuteDeleteAsync</c> is one statement that never loads a row, which is the only shape
    /// that can bound a table holding every line of every build. The <c>InMemory</c> provider the
    /// unit tests use does not implement it, so there is a fallback — and what falls back is only
    /// <i>how</i> the rows are removed. <i>Which</i> rows is the predicate above, identical on both
    /// paths, so there is no behaviour a test can pass that the real provider would fail. The
    /// statement itself is exercised where a real PostgreSQL is available.
    /// </para>
    /// </summary>
    private static async Task<int> DeleteAsync<TEntity>(
        HarboraDbContext db, Expression<Func<TEntity, bool>> rule, CancellationToken ct)
        where TEntity : class
    {
        var doomed = db.Set<TEntity>().IgnoreQueryFilters().Where(rule);

        if (db.Database.IsRelational())
            return await doomed.ExecuteDeleteAsync(ct);

        var rows = await doomed.ToListAsync(ct);
        if (rows.Count == 0) return 0;

        db.RemoveRange(rows);
        await db.SaveChangesAsync(ct);
        return rows.Count;
    }
}
