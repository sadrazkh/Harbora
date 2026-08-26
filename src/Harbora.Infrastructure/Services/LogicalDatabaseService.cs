using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Creates and removes logical databases inside a <see cref="ManagedService"/> instance (D1,
/// 2026-08-25 shared-databases plan).
///
/// <para>
/// Optional <see cref="DatabaseGrantExecutor"/>/<see cref="ManagedServiceEngine"/> dependencies,
/// exactly the shape <see cref="DatabaseAccessService"/> already uses for the same reason: on a
/// single-server install the control plane talks to the same Docker daemon the database runs on, and
/// <see cref="CanCreateLocally"/> is true; the node contract for a remote node has not shipped this
/// operation yet (see HARBORA-0059/D4), so there it is refused by name instead of pretending to work.
/// </para>
///
/// <para>
/// A row is written only after the engine has confirmed the database exists — never before, and
/// never on a failed or uncertain attempt. A row in Harbora that the engine does not have, or the
/// reverse, is this codebase's defining defect class, and the order here is chosen to make it
/// impossible: create against the engine first, persist second; drop against the engine first, delete
/// the row second.
/// </para>
/// </summary>
public sealed class LogicalDatabaseService(
    HarboraDbContext db,
    ISecretProtector protector,
    ILogger<LogicalDatabaseService> logger,
    DatabaseGrantExecutor? grants = null,
    ManagedServiceEngine? services = null)
{
    /// <summary>Whether this installation can really open the instance's engine, rather than simulate
    /// it. See the type doc — the same gate <see cref="DatabaseAccessService.CanOpenLocally"/> uses.</summary>
    public bool CanCreateLocally => grants is not null && services is not null;

    /// <summary>
    /// Creates a new logical database. Returns the row on success, or an error naming which engine
    /// refused and why — never both, and never a row unless the engine actually has the database.
    /// </summary>
    public async Task<(ManagedServiceDatabase? Database, string? Error)> CreateAsync(
        Guid managedServiceId, string? requestedName, CancellationToken ct)
    {
        var service = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == managedServiceId, ct);
        if (service is null) return (null, "That database instance no longer exists.");

        if (!DatabaseGrantSql.Supports(service.Type))
            return (null, DatabaseGrantSql.UnsupportedReason(service.Type));

        if (!CanCreateLocally)
            return (null,
                "This installation cannot reach that database instance's own engine, so a new " +
                "logical database cannot be created here yet.");

        var existingNames = await db.ManagedServiceDatabases
            .Where(d => d.ManagedServiceId == service.Id)
            .Select(d => d.Name)
            .ToListAsync(ct);

        var name = LogicalDatabaseName.Resolve(requestedName, existingNames);
        var seed = DatabaseCredentialManager.Create(name);
        var password = ServiceCredentials.Generate();

        var network = await services!.NetworkForAsync(service, ct);
        var created = await grants!.CreateDatabaseAsync(service, network, name, seed.Username, password, ct);
        if (!created.Ok)
        {
            logger.LogWarning(
                "{Engine} refused to create logical database {Name} on {Service}: {Error}",
                service.Type, name, service.Name, created.Error);
            return (null, created.Error ?? $"{service.Type} refused to create the database.");
        }

        // Written only now that the engine has confirmed the database and its login both exist.
        var logical = new ManagedServiceDatabase
        {
            WorkspaceId = service.WorkspaceId,
            ManagedServiceId = service.Id,
            Name = name,
            Username = seed.Username,
            EncryptedPassword = protector.Protect(password)
        };

        db.ManagedServiceDatabases.Add(logical);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Created logical database {Name} on {Service}.", name, service.Name);
        return (logical, null);
    }

    /// <summary>
    /// Removes a logical database. Refuses by name — never a raw constraint violation — when apps are
    /// still attached (the <c>ProjectsController.Delete</c> idiom this platform already reuses for
    /// buckets and whole database instances) or when this is the instance's own default database,
    /// which cannot be removed without removing the whole instance.
    ///
    /// Returns null on success, or the sentence to show. Idempotent on an already-missing row, the
    /// same as <see cref="DatabaseGrantExecutor.DropAsync"/> — a caller and a sweeper racing on the
    /// same delete must both see success.
    /// </summary>
    public async Task<string?> DeleteAsync(Guid databaseId, CancellationToken ct)
    {
        var logical = await db.ManagedServiceDatabases
            .Include(d => d.ManagedService)
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (logical is null) return null;

        if (logical.IsDefault)
            return "This is the instance's own default database and cannot be deleted on its own — " +
                   "remove the whole database instance instead.";

        var attachedTo = await db.AppManagedServices.AsNoTracking()
            .Where(a => a.ManagedServiceDatabaseId == databaseId)
            .Select(a => a.App!.Name)
            .ToListAsync(ct);
        if (attachedTo.Count > 0)
            return $"Still attached to {NamedList(attachedTo)}. Detach it from every app first, then delete it.";

        var service = logical.ManagedService;
        if (service is null)
        {
            // The instance itself is already gone — nothing left to tell, so the row goes too.
            db.ManagedServiceDatabases.Remove(logical);
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (!DatabaseGrantSql.Supports(service.Type))
            return DatabaseGrantSql.UnsupportedReason(service.Type);

        if (!CanCreateLocally)
            return "This installation cannot reach that database instance's own engine.";

        var network = await services!.NetworkForAsync(service, ct);
        var dropped = await grants!.DropDatabaseAsync(service, network, logical.Name, logical.Username, ct);
        if (!dropped.Ok)
        {
            logger.LogWarning(
                "{Engine} refused to remove logical database {Name} on {Service}: {Error}",
                service.Type, logical.Name, service.Name, dropped.Error);
            return dropped.Error ?? $"{service.Type} refused to remove the database.";
        }

        db.ManagedServiceDatabases.Remove(logical);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Removed logical database {Name} on {Service}.", logical.Name, service.Name);
        return null;
    }

    /// <summary>
    /// Renames a logical database in place (D3, 2026-08-25 shared-databases plan). Refused — never
    /// attempted — for the instance's own default database, for the same reason
    /// <see cref="DeleteAsync"/> refuses to remove it on its own: every one-off container this
    /// instance still runs against itself (provisioning, testing the connection, creating another
    /// logical database) connects as <see cref="ManagedService.DatabaseName"/>, so renaming that one
    /// row out from under it would leave every one of those operations reaching for a name the engine
    /// no longer has.
    ///
    /// <para>
    /// Returns null on success, or the sentence to show — the same shape <see cref="DeleteAsync"/>
    /// already uses. A no-op rename (the resolved name is unchanged) succeeds without touching the
    /// engine at all, the same way saving a form with nothing edited should.
    /// </para>
    /// </summary>
    public async Task<string?> RenameAsync(Guid databaseId, string? requestedName, CancellationToken ct)
    {
        var logical = await db.ManagedServiceDatabases
            .Include(d => d.ManagedService)
            .FirstOrDefaultAsync(d => d.Id == databaseId, ct);
        if (logical is null) return "That database no longer exists.";

        if (logical.IsDefault)
            return "This is the instance's own default database and cannot be renamed on its own — " +
                   "every operation this instance still runs against itself depends on that name staying put.";

        var service = logical.ManagedService;
        if (service is null) return "That database instance no longer exists.";

        if (!DatabaseGrantSql.SupportsRename(service.Type))
            return DatabaseGrantSql.UnsupportedRenameReason(service.Type);

        if (!CanCreateLocally)
            return "This installation cannot reach that database instance's own engine.";

        var existingNames = await db.ManagedServiceDatabases
            .Where(d => d.ManagedServiceId == service.Id && d.Id != databaseId)
            .Select(d => d.Name)
            .ToListAsync(ct);

        var newName = LogicalDatabaseName.Resolve(requestedName, existingNames);
        var oldName = logical.Name;
        if (string.Equals(newName, oldName, StringComparison.Ordinal)) return null;

        var network = await services!.NetworkForAsync(service, ct);
        var renamed = await grants!.RenameDatabaseAsync(service, network, oldName, newName, ct);
        if (!renamed.Ok)
        {
            logger.LogWarning(
                "{Engine} refused to rename logical database {Name} to {NewName} on {Service}: {Error}",
                service.Type, oldName, newName, service.Name, renamed.Error);
            return renamed.Error ?? $"{service.Type} refused to rename the database.";
        }

        logical.Name = newName;

        // C1's idiom, exactly as ManagedServiceEngine.RotatePasswordAsync applies it: every app
        // attached to this logical database now has a stale connection string in its running
        // container — the database name it was given no longer resolves — so each is marked
        // unconditionally, not compared-and-skipped, and cleared only by that app's own next deploy.
        var attachments = await db.AppManagedServices
            .Where(a => a.ManagedServiceDatabaseId == databaseId).ToListAsync(ct);
        foreach (var a in attachments) a.HasUnpublishedChanges = true;

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Renamed logical database {OldName} to {NewName} on {Service}.", oldName, newName, service.Name);
        return null;
    }

    /// <summary>"2 apps: api, worker" — the same idiom <c>DatabasesController.NamedList</c> already
    /// uses for a whole instance, one level down.</summary>
    private static string NamedList(IReadOnlyList<string> names)
    {
        const int shown = 3;
        var listed = names.Count > shown
            ? string.Join(", ", names.Take(shown)) + $" and {names.Count - shown} more"
            : string.Join(", ", names);

        return $"{names.Count} app{(names.Count == 1 ? "" : "s")}: {listed}";
    }
}
