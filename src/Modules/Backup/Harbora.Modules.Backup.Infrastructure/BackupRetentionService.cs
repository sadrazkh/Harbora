using Harbora.Data;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Modules.Backup.Infrastructure;

/// <summary>
/// Applies a policy's retention: works out which snapshots may go, and deletes them.
///
/// <para>
/// The decision and the deletion are separate. <see cref="RetentionCalculator"/> is pure and is
/// tested directly; this class does the part that touches storage. Anything that goes wrong at the
/// engine leaves the row alone, so a snapshot is never recorded as deleted while its data is still
/// in the repository.
/// </para>
/// </summary>
public sealed class BackupRetentionService(
    HarboraDbContext db,
    BackupSnapshotService snapshots,
    ILogger<BackupRetentionService> logger)
{
    public async Task<int> PruneAsync(Guid policyId, CancellationToken ct)
    {
        // Unfiltered: the prune job runs without a session, across tenants.
        var policy = await db.BackupPolicies.IgnoreQueryFilters()
            .FirstOrDefaultAsync(p => p.Id == policyId, ct);

        if (policy is null) return 0;

        var candidates = await db.BackupSnapshots.IgnoreQueryFilters()
            .Where(s => s.PolicyId == policyId
                        && (s.Status == BackupSnapshotStatus.Completed
                            || s.Status == BackupSnapshotStatus.CompletedWithWarnings))
            .Select(s => new { s.Id, s.CreatedAt })
            .ToListAsync(ct);

        if (candidates.Count == 0) return 0;

        var timeZone = ResolveTimeZone(policy.Timezone);
        var decision = RetentionCalculator.Evaluate(
            candidates.Select(c => new RetentionCandidate(c.Id, c.CreatedAt)).ToList(),
            policy.Retention,
            DateTimeOffset.UtcNow,
            timeZone);

        // A belt-and-braces refusal. The validator should already have made this impossible, but a
        // prune that would empty a repository is worth refusing twice rather than explaining once.
        if (decision.Keep.Count == 0 && decision.Prune.Count > 0)
        {
            logger.LogError(
                "Retention for policy {PolicyId} would delete all {Count} snapshots. Refused.",
                policyId, decision.Prune.Count);
            return 0;
        }

        var deleted = 0;
        foreach (var snapshotId in decision.Prune)
        {
            ct.ThrowIfCancellationRequested();

            var result = await snapshots.DeleteAsync(snapshotId, ct);
            if (result.Succeeded) deleted++;
            else logger.LogWarning("Retention could not delete snapshot {SnapshotId}: {Error}",
                snapshotId, result.Error);
        }

        if (deleted > 0)
            logger.LogInformation("Retention removed {Deleted} snapshot(s) for policy {PolicyId}; {Kept} kept.",
                deleted, policyId, decision.Keep.Count);

        return deleted;
    }

    /// <summary>
    /// A policy whose timezone the server cannot resolve falls back to UTC rather than throwing.
    ///
    /// <para>
    /// Retention that stops running because a tzdata package changed is worse than retention that
    /// runs against slightly different day boundaries — the first silently accumulates snapshots
    /// forever, and nobody notices until a disk fills.
    /// </para>
    /// </summary>
    private TimeZoneInfo ResolveTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Timezone '{TimeZone}' is unknown here; retention is using UTC.", id);
            return TimeZoneInfo.Utc;
        }
    }
}
