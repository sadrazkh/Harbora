using Harbora.Application.Abstractions;
using Harbora.Domain.Services;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The one place that decides whose login an attachment actually uses (D1, 2026-08-25
/// shared-databases plan): the logical database's own, when the attachment points at one, or the
/// instance's own admin login otherwise — the exact fallback
/// <see cref="Harbora.Domain.Services.AppManagedService.ManagedServiceDatabaseId"/>'s own doc
/// describes. <see cref="AttachedServiceConnectionResolver"/> and <see cref="ManagedServiceAttachEnv"/>
/// both used to read <c>svc.Username</c>/<c>svc.EncryptedPassword</c>/<c>svc.DatabaseName</c> directly
/// — this is that one read, made once, so the two composers cannot drift on which credential an
/// attachment resolves to.
/// </summary>
internal static class AttachedDatabaseCreds
{
    public static ServiceCreds Resolve(ManagedService svc, ManagedServiceDatabase? logical, ISecretProtector protector)
    {
        var port = ServiceCatalog.All[svc.Type].Port;
        var (username, encryptedPassword, database) = logical is not null
            ? (logical.Username, logical.EncryptedPassword, logical.Name)
            : (svc.Username, svc.EncryptedPassword, svc.DatabaseName);

        return new ServiceCreds(svc.ContainerName, port, username, SafeUnprotect(encryptedPassword, protector), database);
    }

    private static string SafeUnprotect(string value, ISecretProtector protector)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        try { return protector.Unprotect(value); } catch { return string.Empty; }
    }
}
