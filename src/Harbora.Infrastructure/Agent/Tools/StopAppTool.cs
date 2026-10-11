using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Security;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>stop_app</c> — stops an app's container through <see cref="IAppOperationsService.StopAsync"/>, with
/// <c>AppsController.Stop</c>'s capability (AppsOperate). Dangerous, not safe: the app stops serving
/// the moment it runs, which is never something to do on a remembered "don't ask again".
/// </summary>
public sealed class StopAppTool(
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppOperationsService operations) : AgentAppTool(resolver, access)
{
    public override string Name => "stop_app";

    public override string Description =>
        "Stops an app's container: the app stops serving requests until it is started again (restart_app, or Start in " +
        "the panel). Its configuration, volumes and domains are kept. Always shown to the user for approval.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." }
          },
          "required": ["app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.AppsOperate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsOperate, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        if (NotRunning(app) is { } notRunning) return notRunning;

        return new AgentValidation(true,
            TitleEn: $"Stop {app.Slug}",
            TitleFa: $"توقف {app.Slug}",
            DetailEn: $"Stops the container of {app.Slug} in {AgentResourceResolver.Place(app)}. Configuration, volumes and domains are kept.",
            DetailFa: $"کانتینر {app.Slug} در {AgentResourceResolver.Place(app)} متوقف می‌شود. تنظیمات، والیوم‌ها و دامنه‌ها می‌مانند.",
            WarningsEn: [$"{app.Slug} stops serving requests until it is started again."],
            WarningsFa: [$"{app.Slug} تا وقتی دوباره روشن نشود به هیچ درخواستی پاسخ نمی‌دهد."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsOperate, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        if (NotRunning(app) is { } notRunning) return NotPossible(notRunning.Error!, notRunning.ErrorFa!);

        await operations.StopAsync(app.Id, ct);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, stopped = true }),
            $"Stopped '{app.Slug}'. It no longer serves requests; start it again from the panel, or ask me to restart it.",
            $"«{app.Slug}» متوقف شد و دیگر به درخواست‌ها پاسخ نمی‌دهد؛ برای روشن‌کردن دوباره از پنل استفاده کنید یا از من بخواهید آن را راه‌اندازی کنم.",
            Links: [AppLink(app)]);
    }

    /// <summary>
    /// Nothing to stop: already stopped, or never deployed — <c>StopAsync</c> would find no container
    /// for the second, stop nothing and still write it down as stopped, and "stopped, no longer serving"
    /// is not something to say about an app that never served.
    /// </summary>
    private static AgentValidation? NotRunning(Domain.Apps.App app) =>
        app.Status == AppStatus.Stopped
            ? AgentValidation.Refused($"App '{app.Slug}' is already stopped.", $"اپ «{app.Slug}» از قبل متوقف است.")
            : app.ActiveDeploymentId is null
                ? AgentValidation.Refused(
                    $"App '{app.Slug}' has not been deployed yet, so there is nothing running to stop.",
                    $"اپ «{app.Slug}» هنوز مستقر نشده است، پس چیزی در حال اجرا نیست که متوقف شود.")
                : null;
}
