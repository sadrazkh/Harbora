using Harbora.Data;
using Harbora.Infrastructure.Deployments;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Domain;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Modules.Backup.Infrastructure;

public sealed record PolicyOutcome(
    bool Succeeded,
    Guid? PolicyId = null,
    IReadOnlyList<PolicyValidationError>? Errors = null);

/// <summary>
/// Creating and editing backup policies, and working out when each next runs.
///
/// <para>
/// Schedule syntax is parsed by the platform's existing <see cref="CronSchedule"/> rather than a
/// second parser: one implementation means one set of behaviours to understand, and the policy
/// validator takes it as a delegate so the domain project stays free of infrastructure.
/// </para>
/// </summary>
public sealed class BackupPolicyService(HarboraDbContext db)
{
    /// <summary>Whether the platform's cron parser accepts this expression.</summary>
    public static bool IsScheduleValid(string expression) =>
        CronSchedule.TryParse(expression, out _, out _);

    public async Task<PolicyOutcome> SaveAsync(BackupPolicy policy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var errors = BackupPolicyValidator.Validate(policy, IsScheduleValid);
        if (errors.Count > 0) return new PolicyOutcome(false, Errors: errors);

        var repositoryExists = await db.BackupRepositories.AnyAsync(r => r.Id == policy.RepositoryId, ct);
        if (!repositoryExists)
            return new PolicyOutcome(false, Errors:
            [
                new PolicyValidationError(nameof(BackupPolicy.RepositoryId),
                    "That repository does not exist, or is not yours.")
            ]);

        policy.NextRunAt = NextRun(policy, DateTimeOffset.UtcNow);

        if (await db.BackupPolicies.AnyAsync(p => p.Id == policy.Id, ct))
            db.BackupPolicies.Update(policy);
        else
            db.BackupPolicies.Add(policy);

        await db.SaveChangesAsync(ct);
        return new PolicyOutcome(true, policy.Id);
    }

    /// <summary>
    /// When this policy next fires, in UTC.
    ///
    /// <para>
    /// The cron expression is read in the policy's own timezone. "3am" has to mean the tenant's 3am:
    /// evaluating it against the server's clock would silently move every backup window when the
    /// host is UTC and its users are not — which is the ordinary case, not the exotic one.
    /// </para>
    /// </summary>
    public static DateTimeOffset? NextRun(BackupPolicy policy, DateTimeOffset afterUtc)
    {
        ArgumentNullException.ThrowIfNull(policy);

        if (!policy.Enabled) return null;
        if (!CronSchedule.TryParse(policy.Schedule, out var schedule, out _) || schedule is null) return null;

        TimeZoneInfo zone;
        try
        {
            zone = TimeZoneInfo.FindSystemTimeZoneById(policy.Timezone);
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Falls back rather than returning null: a policy that stops scheduling because a tzdata
            // package changed would go quiet with nothing to show for it.
            zone = TimeZoneInfo.Utc;
        }

        var local = TimeZoneInfo.ConvertTime(afterUtc, zone);
        var next = schedule.NextOccurrence(local);
        return next?.ToUniversalTime();
    }

    public Task<List<BackupPolicy>> ListAsync(CancellationToken ct) =>
        db.BackupPolicies.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);

    public async Task<bool> DeleteAsync(Guid policyId, CancellationToken ct)
    {
        var policy = await db.BackupPolicies.FirstOrDefaultAsync(p => p.Id == policyId, ct);
        if (policy is null) return false;

        // Snapshots keep their history: the FK is SetNull, so "what did we back up last month"
        // survives someone tidying up a schedule.
        db.BackupPolicies.Remove(policy);
        await db.SaveChangesAsync(ct);
        return true;
    }
}
