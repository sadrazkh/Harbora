using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>detach_database</c> — removes an app's attachment to a database, through
/// <see cref="IDatabaseAttachmentService"/>, the service <c>DatabasesController.Detach</c> calls. Dangerous
/// rather than safe: nothing is deleted, but on its next deploy the app loses the connection settings it may
/// still depend on.
/// </summary>
public sealed class DetachDatabaseTool(
    HarboraDbContext db,
    IDatabaseAttachmentService attachments,
    AgentResourceResolver resolver) : AgentWriteTool
{
    public override string Name => "detach_database";

    public override string Description =>
        "Detaches a database from an app: on its next deploy the app no longer receives that database's connection " +
        "settings. No data is deleted. Always shown to the user for confirmation before it runs.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "database": { "type": "string", "description": "The database's name." },
            "app": { "type": "string", "description": "The app's slug." },
            "project": { "type": "string", "description": "The database's project slug, when its name is ambiguous." },
            "environment": { "type": "string", "description": "The database's environment slug, when its name is ambiguous." },
            "logicalDatabase": { "type": "string", "description": "When the app is attached to two logical databases of one instance: which one." }
          },
          "required": ["database", "app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.DatabasesManage;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (found, refusal) = await ResolveAsync(args, context, ct);
        if (refusal is not null) return refusal;
        var (service, app, databaseId) = found!.Value;

        var refusals = await attachments.ValidateDetachAsync(service.Id, app.Id, databaseId, ct);
        if (refusals.Count > 0) return Refused(refusals);

        var alias = await AttachmentQuery(service.Id, app.Id, databaseId).Select(x => x.Alias).FirstOrDefaultAsync(ct);
        var prefix = string.IsNullOrWhiteSpace(alias) ? "" : $" ({alias}_*)";

        return new AgentValidation(true,
            TitleEn: $"Detach database {service.Name} from {app.Slug}",
            TitleFa: $"جدا کردن دیتابیس {service.Name} از اپ {app.Slug}",
            DetailEn: $"{app.Slug} stops receiving the connection settings of {service.Name}{prefix}. No data is deleted; the database keeps running.",
            DetailFa: $"اپ {app.Slug} دیگر تنظیمات اتصال {service.Name}{prefix} را نمی‌گیرد. هیچ داده‌ای حذف نمی‌شود و دیتابیس روشن می‌ماند.",
            WarningsEn: [$"On its next deploy {app.Slug} loses these settings; if it still uses this database it will fail to connect. Until then the running container keeps them."],
            WarningsFa: [$"با استقرار بعدی، {app.Slug} این تنظیمات را از دست می‌دهد؛ اگر هنوز از این دیتابیس استفاده کند، اتصالش قطع می‌شود. تا آن زمان کانتینر در حال اجرا آن‌ها را نگه می‌دارد."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (found, refusal) = await ResolveAsync(args, context, ct);
        if (refusal is not null) return FailedAs(refusal);
        var (service, app, databaseId) = found!.Value;

        var outcome = await attachments.DetachAsync(service.Id, app.Id, databaseId, ipAddress: null, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                database = service.Name,
                app = app.Slug,
                alias = outcome.Value!.Alias,
                detached = true,
                takesEffect = "on the app's next deploy"
            }),
            $"Detached {service.Name} from {app.Slug}; {app.Slug} loses its connection settings on its next deploy.",
            $"دیتابیس {service.Name} از اپ {app.Slug} جدا شد؛ {app.Slug} با استقرار بعدی تنظیمات اتصال آن را از دست می‌دهد.",
            Links: [DatabaseLink(service.Id, service.Name), AppLink(app.Id, app.Slug)]);
    }

    private IQueryable<AppManagedService> AttachmentQuery(Guid serviceId, Guid appId, Guid? databaseId)
    {
        var query = db.AppManagedServices.AsNoTracking().Where(x => x.AppId == appId && x.ManagedServiceId == serviceId);
        return databaseId is { } id ? query.Where(x => x.ManagedServiceDatabaseId == id) : query;
    }

    /// <summary>Both sides by name (nothing planned: detaching what an earlier step creates means nothing), and
    /// the logical database when one is named.</summary>
    private async Task<((ManagedService Service, Domain.Apps.App App, Guid? DatabaseId)? Found, AgentValidation? Refusal)> ResolveAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var databaseLookup = await resolver.DatabaseAsync(context,
            AgentArgs.String(args, "database"), AgentArgs.String(args, "project"), AgentArgs.String(args, "environment"), ct);
        if (!databaseLookup.Found) return (null, AgentValidation.Refused(databaseLookup.Error!, databaseLookup.ErrorFa!));

        var appLookup = await resolver.AppAsync(context, AgentArgs.String(args, "app"), ct);
        if (!appLookup.Found) return (null, AgentValidation.Refused(appLookup.Error!, appLookup.ErrorFa!));

        var service = databaseLookup.Value!;
        Guid? databaseId = null;
        if (AgentArgs.String(args, "logicalDatabase") is { } wanted)
        {
            var lowered = wanted.ToLowerInvariant();
            databaseId = await db.ManagedServiceDatabases.AsNoTracking()
                .Where(d => d.ManagedServiceId == service.Id && d.Name.ToLower() == lowered)
                .Select(d => (Guid?)d.Id)
                .FirstOrDefaultAsync(ct);
            if (databaseId is null)
                return (null, AgentValidation.Refused(
                    $"Database {service.Name} has no logical database '{wanted}'.",
                    $"دیتابیس {service.Name} پایگاه‌دادهٔ منطقی‌ای به نام «{wanted}» ندارد."));
        }

        return ((service, appLookup.Value!, databaseId), null);
    }
}
