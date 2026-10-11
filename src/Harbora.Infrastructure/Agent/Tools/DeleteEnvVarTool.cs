using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>delete_env_var</c> — removes one environment variable from an app through
/// <see cref="IAppConfigurationService.DeleteEnvAsync"/>. Dangerous rather than safe: the running
/// containers keep the old value, so nothing breaks now — the app's next deploy is where a missing
/// setting turns into an app that does not start, often long after anybody remembers removing it.
/// </summary>
public sealed class DeleteEnvVarTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration) : AgentAppTool(resolver, access)
{
    public override string Name => "delete_env_var";

    public override string Description =>
        "Deletes one environment variable from an app, by its exact name (case-sensitive). The app keeps running with " +
        "the old value until its next deploy, which starts without it. Always shown to the user for approval.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "key": { "type": "string", "description": "The variable's exact name." }
          },
          "required": ["app", "key"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var key = AgentArgs.String(args, "key");
        if (key is null) return AgentValidation.Refused("Say which variable (its key).", "بگویید کدام متغیر (نام آن).");

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var refusals = await configuration.ValidateDeleteEnvAsync(ActorOf(context), app.Id, AppItemRef.ByName(key), ct);
        if (refusals.Count > 0) return Refused(refusals);

        var secret = await db.EnvironmentVariables.AsNoTracking()
            .Where(e => e.AppId == app.Id && e.Key == key).Select(e => e.IsSecret).FirstOrDefaultAsync(ct);

        return new AgentValidation(true,
            TitleEn: $"Delete environment variable {key} from {app.Slug}",
            TitleFa: $"حذف متغیر محیطی {key} از {app.Slug}",
            DetailEn: $"{key}{(secret ? " (secret)" : "")} is removed from app {app.Slug} in {AgentResourceResolver.Place(app)}. The running app keeps it until its next deploy.",
            DetailFa: $"{key}{(secret ? " (محرمانه)" : "")} از اپ {app.Slug} در {AgentResourceResolver.Place(app)} حذف می‌شود. اپِ در حال اجرا تا استقرار بعدی آن را نگه می‌دارد.",
            WarningsEn: ["Removing configuration can break the app on its next deploy."],
            WarningsFa: ["حذف تنظیمات ممکن است اپ را در استقرار بعدی از کار بیندازد."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var key = AgentArgs.String(args, "key");
        if (key is null) return NotPossible("Say which variable (its key).", "بگویید کدام متغیر (نام آن).");

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.DeleteEnvAsync(ActorOf(context), app.Id, AppItemRef.ByName(key), ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, deleted = outcome.Value, appliesOn = "next deploy" }),
            $"Deleted {outcome.Value} from '{app.Slug}'. It takes effect on the app's next deploy.",
            $"{outcome.Value} از «{app.Slug}» حذف شد. با استقرار بعدی اپ اعمال می‌شود.",
            Links: [AppLink(app)]);
    }
}
