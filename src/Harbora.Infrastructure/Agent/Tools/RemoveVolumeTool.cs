using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>remove_volume</c> — stops mounting a volume, and deletes its data only when asked, through
/// <see cref="IAppConfigurationService.RemoveVolumeAsync"/>. Destructive either way: an app that loses
/// its mount on the next deploy starts with an empty directory where its data was, whether or not the
/// data still sits on the server. The typed phrase is the app's slug.
///
/// <para>
/// A Protected volume's data cannot be deleted (HARBORA-0033) — the service refuses, and so does the
/// card. When the data was asked to be deleted and Docker did not delete it, the step fails and says
/// where the data still is: the person asked for both, and only one happened.
/// </para>
/// </summary>
public sealed class RemoveVolumeTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration) : AgentAppTool(resolver, access)
{
    public override string Name => "remove_volume";

    public override string Description =>
        "Removes a volume from an app by its mount path. By default the data stays on the server and only the mount goes; " +
        "with deleteData true the data is deleted for good (refused for a Protected volume). The user must type the app's " +
        "slug to approve.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "mountPath": { "type": "string", "description": "The volume's mount path, e.g. /data." },
            "deleteData": { "type": "boolean", "description": "Also delete the data on the server. Default false." }
          },
          "required": ["app", "mountPath"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.Destructive;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var mountPath = AgentArgs.String(args, "mountPath");
        var deleteData = AgentArgs.Bool(args, "deleteData") ?? false;

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var refusals = await configuration.ValidateRemoveVolumeAsync(
            ActorOf(context), app.Id, AppItemRef.ByName(mountPath ?? ""), deleteData, ct);
        if (refusals.Count > 0) return Refused(refusals);

        var path = MountPath.Normalise(mountPath)!;
        var volume = await db.Volumes.AsNoTracking()
            .Where(v => v.AppId == app.Id && v.MountPath == path).Select(v => v.Name).FirstAsync(ct);

        return new AgentValidation(true,
            TitleEn: deleteData
                ? $"Remove the volume at {path} from {app.Slug} and delete its data"
                : $"Remove the volume at {path} from {app.Slug}",
            TitleFa: deleteData
                ? $"برداشتن والیوم {path} از {app.Slug} و حذف داده‌هایش"
                : $"برداشتن والیوم {path} از {app.Slug}",
            DetailEn: deleteData
                ? $"The data in volume {volume} is deleted from the server and cannot be recovered."
                : $"{app.Slug} stops mounting {path}; the data stays on the server in volume {volume}.",
            DetailFa: deleteData
                ? $"داده‌های والیوم {volume} از سرور حذف می‌شود و برنمی‌گردد."
                : $"{app.Slug} دیگر {path} را وصل نمی‌کند؛ داده‌ها روی سرور در والیوم {volume} می‌مانند.",
            WarningsEn: deleteData
                ? ["This cannot be undone.", $"From its next deployment the app sees an empty {path}."]
                : [$"From its next deployment the app sees an empty {path}."],
            WarningsFa: deleteData
                ? ["این کار برگشت‌پذیر نیست.", $"از استقرار بعدی، اپ {path} را خالی می‌بیند."]
                : [$"از استقرار بعدی، اپ {path} را خالی می‌بیند."],
            ConfirmationPhrase: app.Slug);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var mountPath = AgentArgs.String(args, "mountPath");
        var deleteData = AgentArgs.Bool(args, "deleteData") ?? false;

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.RemoveVolumeAsync(
            ActorOf(context), app.Id, AppItemRef.ByName(mountPath ?? ""), deleteData, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var (path, name, requested, deleted) = outcome.Value!;

        var result = AgentJson.Serialize(new { app = app.Slug, removed = path, volume = name, dataDeleteRequested = requested, dataDeleted = deleted });

        // Asked for, and not done: what Docker decided, not what was asked, is what is reported.
        if (requested && !deleted)
            return new AgentToolResult(false, result, null, null,
                $"{path} was unmounted from '{app.Slug}', but deleting its data failed. The data is still on the server in volume \"{name}\".",
                $"«{path}» از «{app.Slug}» برداشته شد، اما حذف دادهٔ آن ناموفق بود. داده‌اش هنوز روی سرور در والیوم «{name}» است.",
                [VolumesLink(app)]);

        return new AgentToolResult(true, result,
            deleted
                ? $"{path} and its data were deleted from '{app.Slug}'."
                : $"{path} is no longer mounted on '{app.Slug}'. The data is still on the server in volume \"{name}\".",
            deleted
                ? $"«{path}» و داده‌هایش از «{app.Slug}» حذف شد."
                : $"«{path}» دیگر روی «{app.Slug}» وصل نمی‌شود. داده‌ها روی سرور در والیوم «{name}» ماندند.",
            Links: [VolumesLink(app)]);
    }
}
