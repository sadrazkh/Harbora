using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Storage;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>add_volume</c> — gives an app a directory that survives redeploys, through
/// <see cref="IAppConfigurationService.AddVolumeAsync"/>. The volume is a row until the app's next
/// deployment mounts it, and the summary says so in the words the Volumes tab uses: a file uploaded
/// before then lands somewhere the running container cannot see.
/// </summary>
public sealed class AddVolumeTool(
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration,
    IQuotaService quota) : AgentAppTool(resolver, access)
{
    public override string Name => "add_volume";

    public override string Description =>
        "Adds a persistent volume to an app at an absolute mount path inside its container (e.g. /data), so files written " +
        "there survive redeploys. It is attached on the app's next deployment. Nothing changes until the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug (or one an earlier step of this plan creates)." },
            "mountPath": { "type": "string", "description": "An absolute path inside the container, e.g. /data." }
          },
          "required": ["app", "mountPath"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var mountPath = AgentArgs.String(args, "mountPath");

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);

        if (target.App is { } app)
        {
            var refusals = await configuration.ValidateAddVolumeAsync(ActorOf(context), app.Id, mountPath, ct);
            if (refusals.Count > 0) return Refused(refusals);
        }
        else
        {
            // AddVolumeAsync's checks that need no app row: the path's own rules and the plan's ceiling.
            // A new app has no volumes, so the duplicate check has nothing to compare with yet.
            var refusal = MountPath.Check(mountPath);
            if (refusal != MountPathRefusal.None) return Refused([AppRefusals.Mount(refusal)]);
            var room = await quota.CanAddGovernedResourcesAsync(context.WorkspaceId, new GovernanceQuotaDelta(Volumes: 1), ct);
            if (!room.Allowed) return Refused([AppRefusals.Quota(room, "Plan quota exceeded.", "سقف پلن پر شده است.")]);
        }

        var path = MountPath.Normalise(mountPath)!;
        var slug = target.App?.Slug ?? target.Planned!.Name;
        var detailEn = $"Files {slug} writes under {path} survive redeploys.";
        var detailFa = $"فایل‌هایی که {slug} زیر {path} می‌نویسد پس از استقرار دوباره هم می‌مانند.";
        if (target.Planned is { } planned)
        {
            var (en, fa) = PlannedNote(planned);
            detailEn += " " + en;
            detailFa += " " + fa;
        }

        return new AgentValidation(true,
            TitleEn: $"Add a volume at {path} to {slug}",
            TitleFa: $"افزودن والیوم در {path} به {slug}",
            DetailEn: detailEn,
            DetailFa: detailFa,
            WarningsEn: ["It is attached to the container on the app's next deployment."],
            WarningsFa: ["با استقرار بعدی اپ به کانتینر وصل می‌شود."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.AddVolumeAsync(ActorOf(context), app.Id, AgentArgs.String(args, "mountPath"), ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var added = outcome.Value!;

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, volume = added.MountPath, attachedOn = "next deployment" }),
            $"{added.MountPath} was added to '{app.Slug}'. It is attached to the container on the next deployment.",
            $"«{added.MountPath}» به «{app.Slug}» اضافه شد. با استقرار بعدی به کانتینر وصل می‌شود.",
            Links: [VolumesLink(app)]);
    }
}
