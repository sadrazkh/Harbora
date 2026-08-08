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
    ILogger<DatabaseAccessService> logger,
    DockerTcpGateway? gateway = null,
    DatabaseGrantExecutor? grants = null,
    ManagedServiceEngine? services = null,
    ISecretProtector? protector = null)
{
    /// <summary>
    /// Whether this installation can really open a database, rather than simulate it.
    ///
    /// True on a single-server install, where the control plane talks to the same Docker daemon the
    /// databases run on. The node contract stays for the multi-server case that has not shipped —
    /// what changed is that the single-server case no longer pretends to need it.
    /// </summary>
    public bool CanOpenLocally => gateway is not null && grants is not null
                                  && services is not null && protector is not null;

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

        // The private network the database is really on, asked for rather than rebuilt: the gateway
        // has to land on the same one or it comes up beside nothing and times out.
        var network = CanOpenLocally ? await services!.NetworkForAsync(service, ct) : string.Empty;

        // The login first. An endpoint onto a database that will not accept the credential is worse
        // than no endpoint: it looks like it works until the client authenticates.
        var made = CanOpenLocally
            ? await grants!.CreateAsync(service, network, credential.Username, credential.Password, ct)
            : ToLocal(await node.CreateDatabaseGrantAsync(
                service.ServerId, service.ContainerName, credential.Username, credential.Password, ct));

        if (!made.Ok)
        {
            grant.Status = DatabaseAccessStatus.Failed;
            await AuditAsync(grant, "failed", userEmail, made.Error, ct);
            await db.SaveChangesAsync(ct);
            return new AccessResult(null, made.Error ?? "The database refused to create the login.");
        }

        string tunnelId, gatewayHost;
        int gatewayPort;

        if (CanOpenLocally)
        {
            var (endpoint, error) = await gateway!.OpenAsync(grant, service, network, ct);
            if (endpoint is null)
            {
                // Undo the login rather than leaving an account nobody can reach or account for.
                await grants!.DropAsync(service, network, credential.Username, ct);
                grant.Status = DatabaseAccessStatus.Failed;
                await AuditAsync(grant, "failed", userEmail, error, ct);
                await db.SaveChangesAsync(ct);
                return new AccessResult(null, error ?? "No connection endpoint could be opened.");
            }

            (tunnelId, gatewayHost, gatewayPort) = (endpoint.ContainerName, endpoint.Host, endpoint.Port);
        }
        else
        {
            var tunnel = await node.CreateTcpTunnelAsync(
                service.ServerId, service.ContainerName, service.InternalPort, ct);

            if (tunnel is null)
            {
                await node.RevokeDatabaseGrantAsync(service.ServerId, service.ContainerName, credential.Username, ct);
                grant.Status = DatabaseAccessStatus.Failed;
                await AuditAsync(grant, "failed", userEmail, "No tunnel could be opened.", ct);
                await db.SaveChangesAsync(ct);
                return new AccessResult(null, "A connection endpoint could not be reserved. Nothing was left open.");
            }

            (tunnelId, gatewayHost, gatewayPort) = (tunnel.TunnelId, tunnel.GatewayHost, tunnel.GatewayPort);
        }

        grant.TunnelId = tunnelId;
        grant.GatewayHost = gatewayHost;
        grant.GatewayPort = gatewayPort;
        grant.Status = DatabaseAccessStatus.Active;

        await AuditAsync(grant, "activated", userEmail, null, ct);
        await db.SaveChangesAsync(ct);

        // Asks for encryption only when the server can actually give it. A string that demands
        // sslmode=require from a server with SSL off just fails to connect, and one that asks for
        // "prefer" encrypts nothing while reading as though it does.
        var connection = DatabaseCredentialManager.ConnectionString(
            service.Type.ToString(), gatewayHost, gatewayPort,
            credential.Username, credential.Password, service.DatabaseName,
            service.TlsEnabled ? DatabaseTls.ConnectionParameter(service.Type) : null);

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
            if (CanOpenLocally)
            {
                // The endpoint goes first. While the login still exists a client that is already
                // connected keeps working, which is the right order: closing the door before taking
                // the key back never leaves a usable key on an open door.
                await gateway!.CloseAsync(grant, ct);

                var network = await services!.NetworkForAsync(service, ct);
                await grants!.DropAsync(service, network, grant.Username, ct);
            }
            else
            {
                if (grant.TunnelId is { } tunnelId)
                    await node.RemoveTcpTunnelAsync(service.ServerId, tunnelId, ct);

                await node.RevokeDatabaseGrantAsync(service.ServerId, service.ContainerName, grant.Username, ct);
            }
        }
        else if (CanOpenLocally)
        {
            // The database is gone, so there is no login left to remove — but the gateway container
            // is ours and is still holding a published port open.
            await gateway!.CloseAsync(grant, ct);
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
    ///
    /// <para>
    /// Branches on <see cref="CanOpenLocally"/> the way <see cref="IssueAsync"/> and
    /// <see cref="CloseAsync"/> do. It did not, and went to the node contract on every install: on a
    /// single-server one the login was made locally, so the node had never heard of it and answered
    /// "No such login to rotate." Every rotation this feature ever attempted failed, and the message
    /// sent the operator looking for a missing login rather than a missing branch.
    /// </para>
    ///
    /// <para>
    /// The database is changed first and the row second. If the statement fails nothing here moves,
    /// so the credential the customer already holds keeps working — which is the only safe way round.
    /// The reverse would record a password the database never took.
    /// </para>
    ///
    /// <para>
    /// Between those two writes there is a window no ordering can close, because a customer's
    /// database and this row are two systems that cannot be committed together. So the window is
    /// answered rather than avoided: whenever the <c>ALTER</c> may already have landed, the password
    /// comes back <em>with</em> the error instead of dying with it. A failure that returns nothing
    /// reads as "the rotation did not happen and my old password still works", which in exactly
    /// those cases is inverted — the old password is dead and the new one has never been seen.
    /// </para>
    /// </summary>
    public async Task<(string? Password, string? Error)> RotateAsync(
        DatabaseAccessGrant grant, string? actor, CancellationToken ct)
    {
        if (!DatabaseAccessPolicy.IsUsable(grant, clock.UtcNow))
            return (null, "That access is not active.");

        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == grant.ManagedServiceId, ct);
        if (service is null) return (null, "That database no longer exists.");

        // Refused before a replacement is even generated. The node agent itself does implement
        // credential rotation (Harbora.NodeAgent DatabaseEngineOperations.RotatePasswordAsync); what
        // does not exist yet is a non-simulated INodeAgentClient on this side, so the only thing
        // registered here answers about a book of logins it never wrote in. That is HARBORA-0034,
        // and naming it is the difference between "wait for it" and "hunt for a login that was never
        // missing".
        if (!CanOpenLocally)
            return (null,
                "This installation cannot reach that database itself, and rotating a password through " +
                "a node agent is not built yet (HARBORA-0034). Nothing was changed, and the current " +
                "password still works.");

        var network = await services!.NetworkForAsync(service, ct);
        var replacement = DatabaseCredentialManager.Create(service.Name);

        var rotated = await grants!.RotateAsync(
            service, network, grant.Username, replacement.Password, ct);

        if (!rotated.Ok)
        {
            // Answered: the client ran and gave a verdict. The rotation is one statement with
            // ON_ERROR_STOP set, so a non-zero exit means the ALTER did not take — the customer's
            // existing password is still the live one and there is nothing to hand over.
            if (rotated.Answered)
                return (null, rotated.Error ?? "The database refused to change the password.");

            // Unanswered. The statement may have reached the database, so the generated password may
            // already be the live one; it is handed over with the doubt attached rather than thrown
            // away, because if it did land it is the only copy in existence.
            logger.LogError(
                "Rotation of grant {Grant} on {Service} got no verdict; the new password may be live.",
                grant.Id, service.Name);

            return (replacement.Password, (rotated.Error is { } lost ? lost + " " : "") + MayBeLive);
        }

        grant.PasswordHash = DatabaseCredentialManager.Hash(replacement.Password);
        await AuditAsync(grant, "rotated", actor, null, ct);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // The database took the new password and Harbora could not write it down. Letting this
            // throw sent the operator to a generic error page, from which the only sane reading is
            // "nothing happened, my old password still works" — and it is the one reading that is
            // guaranteed wrong. The password is returned so the grant is still usable, and the
            // sentence says which credential is dead.
            logger.LogError(
                ex, "Grant {Grant} on {Service} was rotated on the database but not recorded.",
                grant.Id, service.Name);

            return (replacement.Password, NotRecorded);
        }

        return (replacement.Password, null);
    }

    /// <summary>
    /// What an operator reads when the <c>ALTER</c> landed and the row did not.
    ///
    /// <para>
    /// Written so the reader finishes the sentence certain which password is live, in the spirit of
    /// the backup module's <c>SafetyCopyRefused</c>: "rotation failed" is ambiguous at exactly the
    /// moment ambiguity is most expensive. It sits above the panel that shows the password, which is
    /// why it can say "below".
    /// </para>
    /// </summary>
    internal const string NotRecorded =
        "The password on this database was changed — the previous one no longer works — but Harbora " +
        "could not record the change. Copy the new password below now; it is kept nowhere else. " +
        "Then rotate this access again, so Harbora's record and the database agree.";

    /// <summary>What an operator reads when nothing ever reported back on the <c>ALTER</c>.</summary>
    internal const string MayBeLive =
        "The password below is the one the database was told to use: it may already be live, and the " +
        "one before it may already be dead. Copy it now — it is kept nowhere else — and rotate this " +
        "access again either way, so Harbora's record and the database agree.";

    /// <summary>Grants past their time, read without a session because the sweeper has none.</summary>
    public async Task<IReadOnlyList<DatabaseAccessGrant>> ExpiredAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;

        var candidates = await db.DatabaseAccessGrants.IgnoreQueryFilters()
            .Where(g => g.Kind == DatabaseAccessKind.Temporary && g.Status == DatabaseAccessStatus.Active)
            .ToListAsync(ct);

        return candidates.Where(g => DatabaseAccessPolicy.HasExpired(g, now)).ToList();
    }

    /// <summary>Bridges the node contract's result shape onto the local one.</summary>
    private static (bool Ok, string? Error) ToLocal(NodeResult result) => (result.Ok, result.Error);

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
