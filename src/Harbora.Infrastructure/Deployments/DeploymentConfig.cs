using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Deployments;

/// <summary>One environment variable, as it was when a version was released.</summary>
/// <param name="Value">The value, for anything not secret.</param>
/// <param name="Fingerprint">
/// For a secret: a keyed hash of the value instead of the value. Enough to see that it changed
/// between two releases, and useless to anyone who obtains it — see <see cref="DeploymentConfig"/>.
/// </param>
public sealed record ConfigEntry(string Key, string? Value, bool Secret, string? Fingerprint);

/// <summary>
/// The configuration a deployment actually ran with.
///
/// A deployment already recorded its commit and its image. It recorded nothing about how the app was
/// configured — so the most common question after a bad release, "it worked yesterday, what
/// changed?", had no answer anywhere in the platform. Someone edits a variable, redeploys, the app
/// breaks, and the deployment list shows two identical-looking rows.
///
/// Secrets are stored as a keyed hash, never as text. The key comes from the platform master key, so
/// a fingerprint cannot be turned back into a password by trying candidates — which a plain SHA-256
/// of a short secret absolutely can be.
/// </summary>
public sealed record DeploymentConfig(
    string? Image,
    int ContainerPort,
    string? HealthCheckPath,
    string? InstanceSizeKey,
    string? ReleaseCommand,
    string? CronExpression,
    IReadOnlyList<ConfigEntry> Variables,
    IReadOnlyList<string> Volumes,
    IReadOnlyList<string> Domains)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    /// <summary>
    /// Takes the snapshot. <paramref name="reveal"/> decrypts a stored secret; <paramref name="key"/>
    /// is the platform key the fingerprint is derived with.
    /// </summary>
    public static DeploymentConfig From(App app, Func<EnvironmentVariable, string?> reveal, byte[] key) =>
        new(
            app.SourceType == AppSourceType.PrebuiltImage ? app.PrebuiltImage : null,
            app.ContainerPort,
            app.HealthCheckPath,
            app.InstanceSizeKey,
            app.ReleaseCommand,
            app.CronExpression,
            app.EnvironmentVariables
                .OrderBy(v => v.Key, StringComparer.Ordinal)
                .Select(v => v.IsSecret
                    ? new ConfigEntry(v.Key, null, true, Fingerprint(reveal(v), key))
                    : new ConfigEntry(v.Key, v.Value, false, null))
                .ToList(),
            app.Volumes.Select(v => v.MountPath).OrderBy(m => m, StringComparer.Ordinal).ToList(),
            app.Domains.Select(d => d.Host).OrderBy(h => h, StringComparer.Ordinal).ToList());

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a stored snapshot, or null for a deployment taken before these were recorded.</summary>
    public static DeploymentConfig? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<DeploymentConfig>(json, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A keyed hash of a secret. Short enough to compare at a glance, and derived with the platform
    /// key so it cannot be brute-forced back into the value it came from.
    /// </summary>
    private static string? Fingerprint(string? value, byte[] key)
    {
        if (value is null) return null;

        var hash = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }
}
