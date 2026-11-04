using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Configuration;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The env vars an <see cref="AppManagedService"/> attachment contributes to a merge (C1, 2026-08-22
/// config-delivery plan) — the database-side mirror of
/// <see cref="Harbora.Domain.Storage.BucketEnvKeys.EntriesFor"/>, one attachment kind later.
///
/// <para>
/// A bucket's secret entry is a single stored field, so <c>BucketEnvKeys.EntriesFor</c> can take its
/// ciphertext as-is and never touch <see cref="ISecretProtector"/> at all — decryption happens exactly
/// once, wherever the merge is finally turned into a container's environment. A database's connection
/// string cannot work that way: <c>DATABASE_URL</c>, <c>DATABASE_DSN</c> and friends are composed from
/// several fields including the password, so there is no pre-existing ciphertext for the composed
/// string to pass through unchanged. This method is therefore the one place that decrypts a service's
/// password (<see cref="ISecretProtector.Unprotect"/>, called exactly once), composes every value
/// <see cref="ServiceCatalog.AttachEnv"/> defines for this engine, and — for every value that embeds
/// the plaintext password — re-encrypts the composed string
/// (<see cref="ISecretProtector.Protect"/>) so the result still satisfies the merge's own contract:
/// an <c>IsSecret</c> entry's <c>Value</c> is ciphertext, decrypted exactly once downstream
/// (<c>DeploymentPipeline.BuildEnv</c>'s <c>SafeUnprotect(e.Value)</c>). Getting this wrong — leaving
/// an already-plaintext value marked secret — is exactly the F5 bucket bug
/// <c>StorageBucketSecretDecryptionTests</c> exists to catch, restated here for databases by
/// <c>AppManagedServiceSecretDecryptionTests</c>.
/// </para>
///
/// <para>
/// Every key comes back twice: once under its "magic" name (<c>DATABASE_URL</c>, <c>PGHOST</c>, …,
/// exactly what <see cref="AttachGuidance"/> already documents an app can read with zero
/// configuration) and once under <paramref name="alias"/>'s prefix
/// (<c>{ALIAS}_DATABASE_URL</c>, …) — see <see cref="Harbora.Domain.Apps.AttachedDatabaseEnv"/>'s own
/// doc for why both are needed and how a collision on the magic name between two databases is
/// resolved.
/// </para>
/// </summary>
public static class ManagedServiceAttachEnv
{
    /// <param name="ms">
    /// The attachment itself, not just the service it points at (D1, 2026-08-25 shared-databases
    /// plan) — its own <see cref="AppManagedService.ManagedService"/> and
    /// <see cref="AppManagedService.Database"/> navigations must already be loaded. Composing env for
    /// one attachment needs both: which login to use (<see cref="AttachedDatabaseCreds"/> picks the
    /// logical database's own login when <see cref="AppManagedService.Database"/> is set, the
    /// instance's admin login otherwise) and, unchanged from before, <see cref="AppManagedService.Alias"/>.
    /// </param>
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(
        AppManagedService ms, ISecretProtector protector)
    {
        var svc = ms.ManagedService ?? throw new ArgumentException(
            "The attachment's ManagedService must be loaded before composing its env.", nameof(ms));

        var creds = AttachedDatabaseCreds.Resolve(svc, ms.Database, protector);
        var plainPassword = creds.Password;
        var definition = ServiceCatalog.All[svc.Type];
        var wanted = definition.AttachEnv(creds);
        var prefix = AttachKeys.PrefixFor(ms.Alias);

        var result = new List<(string Key, string Value, bool IsSecret)>(wanted.Count * 2);
        var computed = new Dictionary<string, (string Stored, bool IsSecret)>(wanted.Count, StringComparer.Ordinal);
        foreach (var (key, value) in wanted)
        {
            // A value that embeds the plaintext password is secret and must go back to being
            // ciphertext before it leaves this method — see the type doc above. A value that does not
            // (PGHOST, PGPORT, PGDATABASE, …) needs no protection and staying plaintext is what lets
            // the env page show it unmasked, the way it already shows a bucket's endpoint.
            var isSecret = plainPassword.Length > 0 && value.Contains(plainPassword, StringComparison.Ordinal);
            var stored = isSecret ? protector.Protect(value) : value;

            result.Add((key, stored, isSecret));
            result.Add(($"{prefix}{key}", stored, isSecret));
            computed[key] = (stored, isSecret);
        }

        // The names this app's own example env file reads (MONGO_URI, DB_HOST). Computed here beside
        // the rest rather than copied onto the app, which is the whole point: a password rotation
        // reaches them too. Bare, with no alias prefix, because the bare name is what the app reads;
        // and they are only one more entry to the merge, so an app's own variable still outranks them.
        foreach (var alias in ReadAliases(ms.EnvAliasesJson))
            if (computed.TryGetValue(alias.SourceKey, out var source))
                result.Add((alias.Name, source.Stored, source.IsSecret));

        return result;
    }

    /// <summary>
    /// Every usable alias on an attachment. Malformed JSON, a blank entry or a name no shell could read
    /// is dropped rather than thrown: this runs inside a deployment, and one bad column must not be the
    /// reason an app stops deploying.
    /// </summary>
    public static IReadOnlyList<AttachEnvAlias> ReadAliases(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return (JsonSerializer.Deserialize<List<AttachEnvAlias?>>(json) ?? [])
                .OfType<AttachEnvAlias>()
                .Where(a => a.SourceKey is { Length: > 0 } && EnvConfigFileEditor.IsValidKey(a.Name ?? ""))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>What <see cref="AppManagedService.EnvAliasesJson"/> holds; null for none, the ordinary case.</summary>
    public static string? WriteAliases(IReadOnlyList<AttachEnvAlias>? aliases) =>
        aliases is { Count: > 0 } ? JsonSerializer.Serialize(aliases) : null;
}
