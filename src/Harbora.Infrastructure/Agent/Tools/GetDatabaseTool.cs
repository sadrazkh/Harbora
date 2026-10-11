using System.Text.Json;
using Harbora.Data;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>get_database</c> — one managed database (or cache, or broker): engine, version, status, what is
/// attached to it, its logical databases and its measured size. Never a username, password or
/// connection string; an attached app the person cannot see is left out rather than named.
/// </summary>
public sealed class GetDatabaseTool(HarboraDbContext db, AgentResourceResolver resolver, ProjectAccessService access)
    : AgentReadTool
{
    public override string Name => "get_database";

    public override string Description =>
        "Shows one database (or cache or message broker) in detail: engine, version, status, the apps attached to it, " +
        "its logical databases and its measured size. Never returns credentials. Pass project and environment when " +
        "the same name exists in more than one place.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "database": { "type": "string", "description": "The database's name, as the snapshot shows it." },
            "project": { "type": "string", "description": "The project's slug, when the name is ambiguous." },
            "environment": { "type": "string", "description": "The environment's slug, when the name is ambiguous." }
          },
          "required": ["database"],
          "additionalProperties": false
        }
        """;

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var lookup = await resolver.DatabaseAsync(context,
            AgentArgs.String(args, "database"), AgentArgs.String(args, "project"), AgentArgs.String(args, "environment"), ct);
        if (!lookup.Found) return NotFound(lookup.Error!, lookup.ErrorFa!);
        var service = lookup.Value!;

        var attachments = await db.AppManagedServices.AsNoTracking()
            .Where(x => x.ManagedServiceId == service.Id && x.App!.WorkspaceId == context.WorkspaceId)
            .OrderBy(x => x.App!.Slug)
            .Select(x => new { x.AppId, app = x.App!.Slug, alias = x.Alias, logicalDatabase = x.Database != null ? x.Database.Name : null })
            .ToListAsync(ct);

        var attachedApps = new List<object>();
        foreach (var attachment in attachments)
            if (await access.CanSeeAppAsync(attachment.AppId, ct))
                attachedApps.Add(new { attachment.app, attachment.alias, attachment.logicalDatabase });

        var logicalDatabases = await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => d.ManagedServiceId == service.Id)
            .OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name)
            .Select(d => new { name = d.Name, isDefault = d.IsDefault })
            .ToListAsync(ct);

        var primary = service.PrimaryManagedServiceId is { } primaryId
            ? await db.ManagedServices.AsNoTracking()
                .Where(s => s.Id == primaryId && s.WorkspaceId == context.WorkspaceId)
                .Select(s => s.Name)
                .FirstOrDefaultAsync(ct)
            : null;

        var payload = new
        {
            name = service.Name,
            project = service.Environment?.Project?.Slug,
            environment = service.Environment?.Slug,
            engine = AgentResourceResolver.Lower(service.Type),
            version = service.Version,
            status = AgentResourceResolver.Lower(service.Status),
            error = Clip(service.ErrorMessage, 300),
            size = service.InstanceSizeKey,
            storageBytes = service.StorageBytes,
            storageMeasuredAt = service.StorageMeasuredAt,
            tls = service.TlsEnabled,
            pointInTimeRecovery = service.PitrEnabled,
            replicaOf = primary,
            attachedApps,
            logicalDatabases
        };

        return Found(payload, $"Read database {service.Name}", $"دیتابیس {service.Name} بررسی شد");
    }
}
