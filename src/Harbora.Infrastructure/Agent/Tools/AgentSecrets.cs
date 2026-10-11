using Harbora.Application.Abstractions;
using Harbora.Data;
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
    /// The app's secret environment variables and the passwords of the databases attached to it. One
    /// that will not decrypt is used as stored rather than dropped: failing to redact is the direction
    /// that hurts.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ForAppAsync(
        HarboraDbContext db, ISecretProtector protector, Guid appId, Guid workspaceId, CancellationToken ct)
    {
        var stored = await db.EnvironmentVariables.AsNoTracking()
            .Where(v => v.AppId == appId && v.IsSecret)
            .Select(v => v.Value)
            .ToListAsync(ct);

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

        return stored
            .Where(s => !string.IsNullOrEmpty(s))
            .Select(s => Unprotect(protector, s))
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Unprotect(ISecretProtector protector, string value)
    {
        try { return protector.Unprotect(value); }
        catch { return value; }
    }
}
