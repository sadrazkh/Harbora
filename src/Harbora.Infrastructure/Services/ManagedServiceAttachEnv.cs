using Harbora.Application.Abstractions;
using Harbora.Domain.Services;

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
    public static IReadOnlyList<(string Key, string Value, bool IsSecret)> EntriesFor(
        ManagedService svc, string alias, ISecretProtector protector)
    {
        var plainPassword = SafeUnprotect(svc.EncryptedPassword, protector);
        var definition = ServiceCatalog.All[svc.Type];
        var creds = new ServiceCreds(svc.ContainerName, definition.Port, svc.Username, plainPassword, svc.DatabaseName);
        var wanted = definition.AttachEnv(creds);
        var prefix = AttachKeys.PrefixFor(alias);

        var result = new List<(string Key, string Value, bool IsSecret)>(wanted.Count * 2);
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
        }

        return result;
    }

    private static string SafeUnprotect(string value, ISecretProtector protector)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try { return protector.Unprotect(value); } catch { return string.Empty; }
    }
}
