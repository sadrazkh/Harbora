using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Monitoring;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>
/// Opens, resolves, acknowledges and expires <see cref="AlertIncident"/> rows.
///
/// <para>
/// Deliberately separate from <see cref="Notifications.NotificationService"/>: a resolve is not a
/// notification — nothing is sent when a threshold clears or an app recovers — so coupling the two
/// would leave no way to close an incident quietly, which is exactly what a cleared condition needs.
/// Every raiser that opens a condition through <c>NotifyAsync</c>/<c>NotifyRuleAsync</c> holds this
/// alongside it and calls both; every raiser that observes a condition clear holds only this one.
/// </para>
///
/// <para>
/// None of these save on their own — the caller's own <c>SaveChangesAsync</c> covers it, the same
/// way <see cref="MetricsCollector"/> already batches a whole tick's writes into one save — except
/// <see cref="AcknowledgeAsync"/>, the one entry point called on its own from a controller action
/// with nothing else in the same unit of work to batch it with.
/// </para>
/// </summary>
public sealed class IncidentService(HarboraDbContext db)
{
    /// <summary>
    /// Opens a new incident for this (workspace, condition, subject), or — if one is already open —
    /// refreshes it in place. The refresh is what keeps a standing breach, re-observed every collector
    /// tick, as one row for as long as it lasts rather than one row per tick.
    /// </summary>
    public async Task OpenAsync(
        Guid workspaceId, AlertEvent condition, string? subjectRef,
        AlertSeverity severity, string title, string body, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await FindOpenAsync(workspaceId, condition, subjectRef, ct);
        if (existing is not null)
        {
            existing.LastObservedAt = now;
            existing.Severity = severity;
            existing.Title = title;
            existing.Body = body;
            return;
        }

        db.AlertIncidents.Add(new AlertIncident
        {
            WorkspaceId = workspaceId,
            Condition = condition,
            SubjectRef = subjectRef,
            Severity = severity,
            Title = title,
            Body = body,
            OpenedAt = now,
            LastObservedAt = now
        });
    }

    /// <summary>
    /// Closes the open incident for this (workspace, condition, subject) as
    /// <see cref="IncidentClosedReason.Resolved"/> — whatever raised it has just observed it clear.
    /// A no-op when none is open, which is the ordinary case on every tick nothing is wrong.
    /// </summary>
    public async Task ResolveAsync(
        Guid workspaceId, AlertEvent condition, string? subjectRef, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await FindOpenAsync(workspaceId, condition, subjectRef, ct);
        if (existing is null) return;

        existing.ClosedAt = now;
        existing.ClosedReason = IncidentClosedReason.Resolved;
    }

    /// <summary>
    /// A person closes it by hand — the only close a deploy or backup failure ever gets, since
    /// neither one recovers on its own. Valid for any open incident, even one whose condition is
    /// still live: an operator acknowledging a disk warning that is still breaching does not stop it
    /// reopening on the very next tick if the disk is still full, which is the correct outcome, not a
    /// bug in this method — see <see cref="OpenAsync"/>.
    /// </summary>
    public async Task<bool> AcknowledgeAsync(Guid workspaceId, Guid incidentId, DateTimeOffset now, CancellationToken ct)
    {
        var incident = await db.AlertIncidents
            .FirstOrDefaultAsync(i => i.Id == incidentId && i.WorkspaceId == workspaceId && i.ClosedAt == null, ct);
        if (incident is null) return false;

        incident.ClosedAt = now;
        incident.ClosedReason = IncidentClosedReason.Acknowledged;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The bounded backstop: anything still open past <paramref name="maxAge"/> since it opened is
    /// closed as <see cref="IncidentClosedReason.Expired"/>, whether or not the condition that raised
    /// it was ever seen to clear. Exists because a deploy or backup failure nobody acknowledges is the
    /// one kind of incident that otherwise has no way to close at all — those two never resolve on
    /// their own, and without this an unattended one would stay open for ever.
    /// </summary>
    public async Task<int> ExpireStaleAsync(DateTimeOffset now, TimeSpan maxAge, CancellationToken ct)
    {
        var cutoff = now - maxAge;
        var stale = await db.AlertIncidents.IgnoreQueryFilters()
            .Where(i => i.ClosedAt == null && i.OpenedAt <= cutoff)
            .ToListAsync(ct);

        foreach (var incident in stale)
        {
            incident.ClosedAt = now;
            incident.ClosedReason = IncidentClosedReason.Expired;
        }
        return stale.Count;
    }

    /// <summary>
    /// <c>IgnoreQueryFilters</c>: every caller of <see cref="OpenAsync"/> and <see cref="ResolveAsync"/>
    /// is a background evaluator with no session behind it — the metrics collector, the certificate
    /// watcher, the deploy pipeline's own failure path — and the ordinary workspace filter would find
    /// nothing and report a clean pass over an empty table, the exact trap this codebase has paid for
    /// before.
    /// </summary>
    private Task<AlertIncident?> FindOpenAsync(
        Guid workspaceId, AlertEvent condition, string? subjectRef, CancellationToken ct) =>
        db.AlertIncidents.IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.WorkspaceId == workspaceId && i.Condition == condition
                && i.SubjectRef == subjectRef && i.ClosedAt == null, ct);
}
