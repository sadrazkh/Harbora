using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>A new grant, with the one and only sight of its password.</summary>
public sealed record IssuedAccess(DatabaseAccessGrant Grant, string Password, string ConnectionString);

/// <summary>The outcome of asking for access.</summary>
public sealed record AccessResult(IssuedAccess? Issued, string? Error)
{
    public bool Ok => Issued is not null;
}

/// <summary>
/// Creating, extending and closing outside access to a managed database.
///
/// The order of operations is the design. The grant row is written first, then the node is asked to
/// make the login and the tunnel; if either fails the grant is marked Failed rather than left
/// Pending. That way every credential that exists on a database has a row here that knows about it —
/// the reverse order leaves orphan logins nobody can find, which is how a database ends up with
/// accounts that outlive the platform that made them.
/// </summary>
public sealed class DatabaseAccessService(
    HarboraDbContext db,
    INodeAgentClient node,
    ISystemClock clock,
    ILogger<DatabaseAccessService> logger)
{
    /// <summary>
    /// Issues access. The password is returned here and never again — it is hashed before the row
    /// is saved.
    /// </summary>
    public async Task<AccessResult> IssueAsync(
        Guid managedServiceId, DatabaseAccessKind kind, TimeSpan? duration,
        string? allowedIps, Guid? userId, string? userEmail, CancellationToken ct)
    {
        var service = await db.ManagedServices
            .FirstOrDefaultAsync(s => s.Id == managedServiceId, ct);
        if (service is null) return new AccessResult(null, "That database no longer exists.");

        if (kind == DatabaseAccessKind.Temporary)
        {
            var window = duration ?? TimeSpan.FromHours(1);
            if (DatabaseAccessPolicy.RefuseDuration(window) is { } bad)
                return new AccessResult(null, bad.Reason);
            duration = window;
        }

        var now = clock.UtcNow;
        var credential = DatabaseCredentialManager.Create(service.Name);

        var grant = new DatabaseAccessGrant
        {
            WorkspaceId = service.WorkspaceId,
            ManagedServiceId = service.Id,
            CreatedByUserId = userId,
            CreatedByEmail = userEmail,
            Kind = kind,
            Status = DatabaseAccessStatus.Pending,
            Username = credential.Username,
            PasswordHash = credential.PasswordHash,
            AllowedIps = allowedIps,
            ExpiresAt = kind == DatabaseAccessKind.Temporary ? now + duration!.Value : null
        };

        db.DatabaseAccessGrants.Add(grant);
        await AuditAsync(grant, "created", userEmail, null, ct);
        await db.SaveChangesAsync(ct);

        // The login first: a tunnel to a database that will not accept the credential is worse than
        // no tunnel, because it looks like it works until the client authenticates.
        var made = await node.CreateDatabaseGrantAsync(
            service.ServerId, service.ContainerName, credential.Username, credential.Password, ct);

        if (!made.Ok)
        {
            grant.Status = DatabaseAccessStatus.Failed;
            await AuditAsync(grant, "failed", userEmail, made.Error, ct);
            await db.SaveChangesAsync(ct);
            return new AccessResult(null, made.Error ?? "The database refused to create the login.");
        }

        var tunnel = await node.CreateTcpTunnelAsync(
            service.ServerId, service.ContainerName, service.InternalPort, ct);

        if (tunnel is null)
        {
            // Undo the login rather than leaving an account nobody can reach or account for.
            await node.RevokeDatabaseGrantAsync(service.ServerId, service.ContainerName, credential.Username, ct);
            grant.Status = DatabaseAccessStatus.Failed;
            await AuditAsync(grant, "failed", userEmail, "No tunnel could be opened.", ct);
            await db.SaveChangesAsync(ct);
            return new AccessResult(null, "A connection endpoint could not be reserved. Nothing was left open.");
        }

        grant.TunnelId = tunnel.TunnelId;
        grant.GatewayHost = tunnel.GatewayHost;
        grant.GatewayPort = tunnel.GatewayPort;
        grant.Status = DatabaseAccessStatus.Active;

        await AuditAsync(grant, "activated", userEmail, null, ct);
        await db.SaveChangesAsync(ct);

        var connection = DatabaseCredentialManager.ConnectionString(
            service.Type.ToString(), tunnel.GatewayHost, tunnel.GatewayPort,
            credential.Username, credential.Password, service.DatabaseName);

        return new AccessResult(new IssuedAccess(grant, credential.Password, connection), null);
    }

    /// <summary>
    /// Closes a grant: tunnel down, login removed, row marked. Safe to call twice — the sweeper and
    /// a person pressing revoke can race, and both should end with the access closed.
    /// </summary>
    public async Task CloseAsync(
        DatabaseAccessGrant grant, DatabaseAccessStatus reason, string? detail, string? actor, CancellationToken ct)
    {
        var service = await db.ManagedServices.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Id == grant.ManagedServiceId, ct);

        if (service is not null)
        {
            if (grant.TunnelId is { } tunnelId)
                await node.RemoveTcpTunnelAsync(service.ServerId, tunnelId, ct);

            await node.RevokeDatabaseGrantAsync(service.ServerId, service.ContainerName, grant.Username, ct);
        }
        else
        {
            // The database is gone, so there is nothing to revoke on it. The row still closes.
            logger.LogInformation("Closing grant {Grant} whose database no longer exists.", grant.Id);
        }

        grant.Status = reason;
        grant.RevokedAt = clock.UtcNow;
        grant.RevokedReason = detail;
        grant.TunnelId = null;
        grant.GatewayHost = null;
        grant.GatewayPort = null;

        await AuditAsync(grant, reason == DatabaseAccessStatus.Expired ? "expired" : "revoked", actor, detail, ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Pushes a temporary grant's window out, within the policy's limits.</summary>
    public async Task<string?> ExtendAsync(
        DatabaseAccessGrant grant, TimeSpan extension, string? actor, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (DatabaseAccessPolicy.RefuseExtension(grant, extension, now) is { } refusal)
            return refusal.Reason;

        grant.ExpiresAt = now + extension;
        grant.ExtensionCount++;

        await AuditAsync(grant, "extended", actor, $"+{extension.TotalHours:0.#}h", ct);
        await db.SaveChangesAsync(ct);
        return null;
    }

    /// <summary>
    /// Replaces the password on a live grant. Returns the new one once, like issuing does.
    /// </summary>
    public async Task<(string? Password, string? Error)> RotateAsync(
        DatabaseAccessGrant grant, string? actor, CancellationToken ct)
    {
        if (!DatabaseAccessPolicy.IsUsable(grant, clock.UtcNow))
            return (null, "That access is not active.");

        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == grant.ManagedServiceId, ct);
        if (service is null) return (null, "That database no longer exists.");

        var replacement = DatabaseCredentialManager.Create(service.Name);
        var rotated = await node.RotateDatabaseCredentialAsync(
            service.ServerId, service.ContainerName, grant.Username, replacement.Password, ct);

        if (!rotated.Ok) return (null, rotated.Error ?? "The database refused to change the password.");

        grant.PasswordHash = DatabaseCredentialManager.Hash(replacement.Password);
        await AuditAsync(grant, "rotated", actor, null, ct);
        await db.SaveChangesAsync(ct);

        return (replacement.Password, null);
    }

    /// <summary>Grants past their time, read without a session because the sweeper has none.</summary>
    public async Task<IReadOnlyList<DatabaseAccessGrant>> ExpiredAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var candidates = await db.DatabaseAccessGrants.IgnoreQueryFilters()
            .Where(g => g.Kind == DatabaseAccessKind.Temporary && g.Status == DatabaseAccessStatus.Active)
            .ToListAsync(ct);

        return candidates.Where(g => DatabaseAccessPolicy.HasExpired(g, now)).ToList();
    }

    private async Task AuditAsync(
        DatabaseAccessGrant grant, string action, string? actor, string? detail, CancellationToken ct)
    {
        db.DatabaseAccessAudits.Add(new DatabaseAccessAudit
        {
            WorkspaceId = grant.WorkspaceId,
            GrantId = grant.Id,
            ManagedServiceId = grant.ManagedServiceId,
            Action = action,
            ActorEmail = actor,
            // Never a password. The detail field carries refusal reasons and node errors only.
            Detail = detail
        });

        await Task.CompletedTask;
    }
}
