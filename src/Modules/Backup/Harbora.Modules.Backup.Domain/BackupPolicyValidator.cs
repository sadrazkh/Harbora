using Harbora.Modules.Backup.Contracts;

namespace Harbora.Modules.Backup.Domain;

/// <summary>One thing wrong with a policy, in words the person who wrote it can act on.</summary>
public sealed record PolicyValidationError(string Field, string Message);

/// <summary>
/// Checks a policy before it is saved.
///
/// <para>
/// Every rule here exists to prevent a policy that looks configured and does nothing — an
/// unparseable schedule that never fires, a retention that keeps zero, a timezone the server cannot
/// resolve. All of those fail silently at run time, and a backup that silently does not happen is
/// indistinguishable from one that does until someone needs it.
/// </para>
/// <para>
/// Schedule syntax is supplied by <c>isScheduleValid</c> rather than parsed here, so this project
/// stays free of the infrastructure that owns the cron parser and the platform keeps ONE cron
/// implementation (<c>Harbora.Infrastructure.Deployments.CronSchedule</c>).
/// </para>
/// </summary>
public static class BackupPolicyValidator
{
    public static IReadOnlyList<PolicyValidationError> Validate(
        BackupPolicy policy,
        Func<string, bool> isScheduleValid)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(isScheduleValid);

        var errors = new List<PolicyValidationError>();

        if (string.IsNullOrWhiteSpace(policy.Name))
            errors.Add(new PolicyValidationError(nameof(policy.Name), "Give the policy a name."));
        else if (!EngineArgumentGuard.IsSafeName(policy.Name))
            errors.Add(new PolicyValidationError(nameof(policy.Name),
                "Use letters, digits, spaces, dots, hyphens or underscores."));

        if (policy.RepositoryId == Guid.Empty)
            errors.Add(new PolicyValidationError(nameof(policy.RepositoryId),
                "Choose where these backups should be stored."));

        if (string.IsNullOrWhiteSpace(policy.TargetRef))
            errors.Add(new PolicyValidationError(nameof(policy.TargetRef),
                "Choose what to back up."));

        if (string.IsNullOrWhiteSpace(policy.Schedule) || !isScheduleValid(policy.Schedule))
            errors.Add(new PolicyValidationError(nameof(policy.Schedule),
                "That schedule could not be understood, so it would never run."));

        if (!IsKnownTimeZone(policy.Timezone))
            errors.Add(new PolicyValidationError(nameof(policy.Timezone),
                $"'{policy.Timezone}' is not a timezone this server knows."));

        errors.AddRange(ValidateRetention(policy.Retention));

        return errors;
    }

    private static IEnumerable<PolicyValidationError> ValidateRetention(RetentionPolicy retention)
    {
        if (retention is null)
        {
            yield return new PolicyValidationError(nameof(BackupPolicy.Retention),
                "Set how long backups should be kept.");
            yield break;
        }

        int[] tiers =
        [
            retention.KeepLatest, retention.KeepHourly, retention.KeepDaily,
            retention.KeepWeekly, retention.KeepMonthly, retention.KeepYearly
        ];

        if (tiers.Any(t => t < 0))
            yield return new PolicyValidationError(nameof(BackupPolicy.Retention),
                "Retention counts cannot be negative.");

        // The rule that matters most. Every tier at zero is a policy whose first prune deletes every
        // snapshot it has ever taken, and it reads as a perfectly ordinary configuration.
        if (tiers.All(t => t <= 0))
            yield return new PolicyValidationError(nameof(RetentionPolicy.KeepLatest),
                "This would keep no backups at all. Keep at least the most recent one.");

        if (retention.KeepLatest < 1)
            yield return new PolicyValidationError(nameof(RetentionPolicy.KeepLatest),
                "Always keep at least one recent backup, so a misconfigured tier cannot empty the repository.");

        if (retention.MaximumAgeDays is { } age && age < 1)
            yield return new PolicyValidationError(nameof(RetentionPolicy.MaximumAgeDays),
                "A maximum age of less than a day would delete every backup as soon as it was taken.");

        if (retention.MaximumRepositorySizeBytes is { } size && size < 1)
            yield return new PolicyValidationError(nameof(RetentionPolicy.MaximumRepositorySizeBytes),
                "A repository size limit must be positive.");
    }

    private static bool IsKnownTimeZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(id);
            return true;
        }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }
}
