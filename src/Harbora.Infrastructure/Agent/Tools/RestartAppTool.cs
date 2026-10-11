using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Billing;
using Harbora.Infrastructure.Security;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>restart_app</c> — restarts an app's container through <see cref="IAppOperationsService.RestartAsync"/>,
/// with <c>AppsController.Restart</c>'s capability (AppsOperate) and its billing refusal shown in the
/// person's language.
///
/// <para>
/// An app that has never been deployed is refused here: <c>RestartAsync</c> finds no container for it,
/// restarts nothing, and still writes its status as running — which is not something this tool will
/// report as a restart.
/// </para>
/// </summary>
public sealed class RestartAppTool(
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppOperationsService operations,
    IBillingGate billing) : AgentAppTool(resolver, access)
{
    public override string Name => "restart_app";

    public override string Description =>
        "Restarts an app's running container (a stopped app is started again). It does not rebuild or redeploy anything; " +
        "configuration changes need deploy_app instead. Nothing happens until the user approves.";

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

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsOperate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsOperate, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        if (await RefusalAsync(app, context, ct) is { } refusal) return AgentValidation.Refused(refusal.En, refusal.Fa);

        return new AgentValidation(true,
            TitleEn: $"Restart {app.Slug}",
            TitleFa: $"راه‌اندازی دوبارهٔ {app.Slug}",
            DetailEn: $"Restarts the container of {app.Slug} in {AgentResourceResolver.Place(app)}. Nothing is rebuilt.",
            DetailFa: $"کانتینر {app.Slug} در {AgentResourceResolver.Place(app)} دوباره راه‌اندازی می‌شود. چیزی دوباره ساخته نمی‌شود.",
            WarningsEn: ["The app is briefly unavailable while its container restarts."],
            WarningsFa: ["تا کانتینر دوباره بالا بیاید، اپ چند لحظه در دسترس نیست."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsOperate, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        if (await RefusalAsync(app, context, ct) is { } refusal) return NotPossible(refusal.En, refusal.Fa);

        try
        {
            await operations.RestartAsync(app.Id, ct);
        }
        catch (QuotaRefusedException ex)
        {
            // The billing gate, asked again by the service itself: the workspace may have run dry since.
            return NotPossible(ex.Message, ex.ReasonFa ?? ex.Message);
        }

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, restarted = true }),
            $"Restarted '{app.Slug}'.",
            $"«{app.Slug}» دوباره راه‌اندازی شد.",
            Links: [AppLink(app)]);
    }

    private async Task<(string En, string Fa)?> RefusalAsync(Domain.Apps.App app, AgentToolContext context, CancellationToken ct)
    {
        if (app.ActiveDeploymentId is null)
            return ($"App '{app.Slug}' has not been deployed yet, so there is nothing to restart; deploy it instead.",
                $"اپ «{app.Slug}» هنوز مستقر نشده است، پس چیزی برای راه‌اندازی دوباره ندارد؛ آن را مستقر کنید.");

        var mayStart = await billing.CanStartAsync(context.WorkspaceId, BilledResourceType.App, app.Id, ct);
        return mayStart.Allowed
            ? null
            : (mayStart.Reason ?? "This workspace cannot start apps right now.",
                mayStart.ReasonFa ?? mayStart.Reason ?? "این فضای کاری فعلاً نمی‌تواند اپی را روشن کند.");
    }
}
