using System.Text.Json;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>delete_database</c> — removes a database, through <see cref="IDatabaseProvisioningService"/>, the service
/// <c>DatabasesController.Remove</c> calls. The structural refusals — apps still attached, replicas still
/// streaming — are the service's. The data volume goes only when <c>deleteData</c> is true, and it defaults to
/// false; the card says plainly which of the two will happen. Confirmed by typing the database's exact name,
/// always — the panel asks for the name only when the data goes, but every destructive step of the agent is
/// typed.
/// </summary>
public sealed class DeleteDatabaseTool(
    IDatabaseProvisioningService provisioning, AgentResourceResolver resolver) : AgentWriteTool
{
    public override string Name => "delete_database";

    public override string Description =>
        "Deletes a database. Its data volume is deleted too only when deleteData is true (default false: the data stays " +
        "on the server). Refused while any app is attached to it or a replica streams from it. The user must confirm " +
        "by typing the database's name.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "database": { "type": "string", "description": "The database's name." },
            "project": { "type": "string", "description": "The project's slug, when the name is ambiguous." },
            "environment": { "type": "string", "description": "The environment's slug, when the name is ambiguous." },
            "deleteData": { "type": "boolean", "description": "Also delete its data volume, for good. Default false." }
          },
          "required": ["database"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.Destructive;

    public override string? Capability => Capabilities.DatabasesManage;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var lookup = await resolver.DatabaseAsync(context,
            AgentArgs.String(args, "database"), AgentArgs.String(args, "project"), AgentArgs.String(args, "environment"), ct);
        if (!lookup.Found) return AgentValidation.Refused(lookup.Error!, lookup.ErrorFa!);
        var service = lookup.Value!;

        var refusals = await provisioning.ValidateRemoveAsync(service.Id, ct);
        if (refusals.Count > 0) return Refused(refusals);

        var deleteData = AgentArgs.Bool(args, "deleteData") ?? false;
        var what = $"{AgentResourceResolver.Lower(service.Type)} {service.Version} in {AgentResourceResolver.Place(service)}.";
        var whatFa = $"{AgentResourceResolver.Lower(service.Type)} نسخهٔ {service.Version} در {AgentResourceResolver.Place(service)}.";

        return deleteData
            ? new AgentValidation(true,
                TitleEn: $"Delete database {service.Name} and its data",
                TitleFa: $"حذف دیتابیس {service.Name} همراه با داده‌هایش",
                DetailEn: $"{what} The container is removed and its data volume {service.VolumeName} is deleted with it.",
                DetailFa: $"{whatFa} کانتینر حذف می‌شود و والیوم داده‌اش، {service.VolumeName}، هم همراه آن پاک می‌شود.",
                WarningsEn: ["All of its data is deleted for good. This cannot be undone; take a backup first if it may be needed."],
                WarningsFa: ["همهٔ داده‌هایش برای همیشه پاک می‌شود و برگشت‌پذیر نیست؛ اگر ممکن است لازم شود، اول پشتیبان بگیرید."],
                ConfirmationPhrase: service.Name)
            : new AgentValidation(true,
                TitleEn: $"Delete database {service.Name}, keeping its data",
                TitleFa: $"حذف دیتابیس {service.Name} با نگه‌داشتن داده‌هایش",
                DetailEn: $"{what} The container is removed; its data volume {service.VolumeName} is kept on the server.",
                DetailFa: $"{whatFa} کانتینر حذف می‌شود؛ والیوم داده‌اش، {service.VolumeName}، روی سرور می‌ماند.",
                WarningsEn: [$"The database is gone from the panel. Its data stays on the server's disk in {service.VolumeName} and keeps using space until someone removes it."],
                WarningsFa: [$"دیتابیس از پنل حذف می‌شود. داده‌هایش در {service.VolumeName} روی دیسک سرور می‌ماند و تا وقتی کسی آن را پاک نکند فضا می‌گیرد."],
                ConfirmationPhrase: service.Name);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var lookup = await resolver.DatabaseAsync(context,
            AgentArgs.String(args, "database"), AgentArgs.String(args, "project"), AgentArgs.String(args, "environment"), ct);
        if (!lookup.Found) return AgentToolResult.Failed(lookup.Error!, lookup.ErrorFa!);

        var deleteData = AgentArgs.Bool(args, "deleteData") ?? false;
        var outcome = await provisioning.RemoveAsync(lookup.Value!.Id, deleteData, ipAddress: null, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var removed = outcome.Value!;

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                database = removed.Name,
                deleted = true,
                dataDeleted = removed.DataDeleted,
                dataKeptInVolume = removed.DataDeleted ? null : removed.VolumeName
            }),
            removed.DataDeleted
                ? $"Deleted {removed.Name} and its data."
                : $"Deleted {removed.Name}; its data is kept on the server in volume {removed.VolumeName}.",
            removed.DataDeleted
                ? $"دیتابیس {removed.Name} همراه با داده‌هایش حذف شد."
                : $"دیتابیس {removed.Name} حذف شد؛ داده‌هایش در والیوم {removed.VolumeName} روی سرور مانده است.");
    }
}
