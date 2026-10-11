using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// Every secret value Harbora holds for one app, decrypted — for redaction by value before a tool
/// result leaves the server. Free text about an app (a deployment's error, its log, a plain variable
/// someone pasted a connection string into) can carry one of these in a shape no pattern recognises,
/// and only the plaintext finds it.
/// </summary>
public static class AgentSecrets
{
    /// <summary>
    /// Everything secret that reaches the app's environment: its own secret variables, the secret
    /// entries of the config groups attached to it, the passwords of the databases attached to it, and
    /// the credentials of the attached storage buckets, email providers and error trackers. One that will
    /// not decrypt is used as stored rather than dropped: failing to redact is the direction that hurts.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ForAppAsync(
        HarboraDbContext db, ISecretProtector protector, Guid appId, Guid workspaceId, CancellationToken ct)
    {
        var stored = await db.EnvironmentVariables.AsNoTracking()
            .Where(v => v.AppId == appId && v.IsSecret)
            .Select(v => v.Value)
            .ToListAsync(ct);

        // Config groups are injected into the app's environment beside its own variables, so a secret
        // entry there is as likely to be printed by the app as one of its own.
        stored.AddRange(await db.AppConfigGroups.AsNoTracking()
            .Where(x => x.AppId == appId && x.ConfigGroup!.WorkspaceId == workspaceId)
            .SelectMany(x => x.ConfigGroup!.Entries.Where(e => e.IsSecret).Select(e => e.Value))
            .ToListAsync(ct));

        var attached = await db.AppManagedServices.AsNoTracking()
            .Where(x => x.AppId == appId && x.ManagedService!.WorkspaceId == workspaceId)
            .Select(x => new
            {
                Service = x.ManagedService!.EncryptedPassword,
                Logical = x.Database != null ? x.Database.EncryptedPassword : null
            })
            .ToListAsync(ct);
        stored.AddRange(attached.Select(p => p.Service));
        stored.AddRange(attached.Where(p => p.Logical is not null).Select(p => p.Logical!));

        stored.AddRange(await db.AppStorageBuckets.AsNoTracking()
            .Where(x => x.AppId == appId)
            .Select(x => x.StorageBucket!.EncryptedSecretKey)
            .ToListAsync(ct));
        stored.AddRange(await db.AppEmailProviders.AsNoTracking()
            .Where(x => x.AppId == appId)
            .Select(x => x.EmailProvider!.EncryptedPassword)
            .ToListAsync(ct));
        stored.AddRange(await db.AppErrorTrackingProviders.AsNoTracking()
            .Where(x => x.AppId == appId)
            .Select(x => x.ErrorTrackingProvider!.EncryptedDsn)
            .ToListAsync(ct));

        return Decrypt(protector, stored);
    }

    /// <summary>The database's own password and those of its logical databases.</summary>
    public static async Task<IReadOnlyList<string>> ForDatabaseAsync(
        HarboraDbContext db, ISecretProtector protector, ManagedService service, CancellationToken ct)
    {
        var stored = new List<string> { service.EncryptedPassword };
        stored.AddRange(await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => d.ManagedServiceId == service.Id)
            .Select(d => d.EncryptedPassword)
            .ToListAsync(ct));
        return Decrypt(protector, stored);
    }

    private static IReadOnlyList<string> Decrypt(ISecretProtector protector, IEnumerable<string?> stored) =>
        stored
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => Unprotect(protector, s!))
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static string Unprotect(ISecretProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch { return value; }
    }
}
