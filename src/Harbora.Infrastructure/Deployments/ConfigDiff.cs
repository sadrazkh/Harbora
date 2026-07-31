namespace Harbora.Infrastructure.Deployments;

/// <summary>One difference between two releases.</summary>
/// <param name="Detail">Deliberately never the value of a secret — see <see cref="ConfigDiff"/>.</param>
public sealed record ConfigChange(string What, string Detail);

/// <summary>
/// What changed between two releases.
///
/// This is the answer to the question people actually ask after a bad deploy — "it worked yesterday,
/// what changed?" — and until deployments recorded their configuration there was nowhere in the
/// platform it could come from. A settings change and a code change looked identical in the history.
///
/// A secret that changed is reported as having changed and nothing more. Showing the old value to
/// explain a break would put every rotated password permanently in the deployment history, which is
/// exactly the sort of convenience that becomes an incident.
/// </summary>
public static class ConfigDiff
{
    public static IReadOnlyList<ConfigChange> Between(DeploymentConfig? before, DeploymentConfig? after)
    {
        if (before is null || after is null) return [];

        var changes = new List<ConfigChange>();

        Scalar(changes, "Image", before.Image, after.Image);
        Scalar(changes, "Port", before.ContainerPort.ToString(), after.ContainerPort.ToString());
        Scalar(changes, "Health check path", before.HealthCheckPath, after.HealthCheckPath);
        Scalar(changes, "Instance size", before.InstanceSizeKey, after.InstanceSizeKey);
        Scalar(changes, "Release task", before.ReleaseCommand, after.ReleaseCommand);
        Scalar(changes, "Schedule", before.CronExpression, after.CronExpression);

        Variables(changes, before, after);
        Sets(changes, "Volume", before.Volumes, after.Volumes);
        Sets(changes, "Domain", before.Domains, after.Domains);

        return changes;
    }

    /// <summary>True when the two releases were configured identically — worth stating outright.</summary>
    public static bool AreIdentical(DeploymentConfig? before, DeploymentConfig? after) =>
        before is not null && after is not null && Between(before, after).Count == 0;

    private static void Scalar(List<ConfigChange> changes, string name, string? before, string? after)
    {
        if (string.Equals(Normalise(before), Normalise(after), StringComparison.Ordinal)) return;

        changes.Add(new ConfigChange(name, (Normalise(before), Normalise(after)) switch
        {
            (null, { } added) => $"set to {added}",
            ({ } removed, null) => $"cleared (was {removed})",
            var (from, to) => $"{from} → {to}"
        }));
    }

    private static void Variables(List<ConfigChange> changes, DeploymentConfig before, DeploymentConfig after)
    {
        var previous = before.Variables.ToDictionary(v => v.Key, StringComparer.Ordinal);
        var current = after.Variables.ToDictionary(v => v.Key, StringComparer.Ordinal);

        foreach (var (key, entry) in current)
        {
            if (!previous.TryGetValue(key, out var old))
            {
                changes.Add(new ConfigChange($"Variable {key}", entry.Secret ? "added (secret)" : $"added: {entry.Value}"));
                continue;
            }

            if (entry.Secret != old.Secret)
            {
                changes.Add(new ConfigChange($"Variable {key}",
                    entry.Secret ? "became a secret" : "is no longer a secret"));
                continue;
            }

            if (entry.Secret)
            {
                // Reported as changed and nothing more. Showing the old value to explain a break
                // would put every rotated password permanently in the deployment history.
                if (!string.Equals(entry.Fingerprint, old.Fingerprint, StringComparison.Ordinal))
                    changes.Add(new ConfigChange($"Variable {key}", "changed (secret)"));
                continue;
            }

            if (!string.Equals(entry.Value, old.Value, StringComparison.Ordinal))
                changes.Add(new ConfigChange($"Variable {key}", $"{Show(old.Value)} → {Show(entry.Value)}"));
        }

        foreach (var key in previous.Keys.Where(k => !current.ContainsKey(k)))
            changes.Add(new ConfigChange($"Variable {key}", "removed"));
    }

    private static void Sets(List<ConfigChange> changes, string noun,
                             IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        foreach (var added in after.Except(before, StringComparer.Ordinal))
            changes.Add(new ConfigChange($"{noun} {added}", "added"));

        foreach (var removed in before.Except(after, StringComparer.Ordinal))
            changes.Add(new ConfigChange($"{noun} {removed}", "removed"));
    }

    /// <summary>Empty and absent are the same thing here; treating them apart produces noise changes.</summary>
    private static string? Normalise(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Show(string? value) => string.IsNullOrEmpty(value) ? "(empty)" : value;
}
