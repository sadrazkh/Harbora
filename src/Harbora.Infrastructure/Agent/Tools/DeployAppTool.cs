using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>deploy_app</c> — queues a deployment of an app through <see cref="IDeploymentEngine"/>, with the
/// same quota pre-check <c>AppsController.Deploy</c> makes first and the same <c>app.deploy</c> audit
/// entry it writes after. The deployment is recorded as started by the assistant, for this person.
///
/// <para>
/// A tool never waits for a deploy: the result is the deployment as the engine left it — queued, or
/// waiting for approval in a protected environment — with a link to follow it, never "running" or
/// "done". When a deployment of the app is already in flight the engine joins the request to it rather
/// than starting another; the result says that too instead of passing the old deployment off as new.
/// </para>
/// </summary>
public sealed class DeployAppTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IDeploymentEngine deployments,
    IQuotaService quota,
    IAuditLogger audit) : AgentAppTool(resolver, access)
{
    public override string Name => "deploy_app";

    public override string Description =>
        "Queues a deployment of an app: builds it again from Git (optionally another branch or tag) or pulls its image " +
        "again, then replaces the running containers with the new version. Use it after changing environment variables, " +
        "domains or volumes, which take effect on the next deploy. Returns the deployment's number and status (queued, " +
        "or waiting for approval in a protected environment) — it does not wait for the deploy to finish; use " +
        "get_deployment to follow it. Nothing is queued until the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug (or one an earlier step of this plan creates)." },
            "gitRef": { "type": "string", "description": "Branch or tag to deploy, for apps built from Git. Default: the app's own branch." }
          },
          "required": ["app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsDeploy;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var gitRef = AgentArgs.String(args, "gitRef");
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDeploy, scope, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);

        var warningsEn = new List<string> { "A deploy replaces the app's running containers with the new version." };
        var warningsFa = new List<string> { "استقرار، کانتینرهای در حال اجرای اپ را با نسخهٔ تازه جایگزین می‌کند." };

        if (target.Planned is { } planned)
        {
            var place = await Resolver.EnvironmentAsync(context, planned.Project, planned.Environment, ct);
            if (place.Value is { IsProtected: true } guarded) Add(ProtectedEnvironmentWarning(guarded.Slug));

            var (en, fa) = PlannedNote(planned);
            return new AgentValidation(true,
                TitleEn: $"Deploy {planned.Name}{(gitRef is null ? "" : $" at {gitRef}")}",
                TitleFa: $"استقرار {planned.Name}{(gitRef is null ? "" : $" از {gitRef}")}",
                DetailEn: $"Queues a deployment of {planned.Name} in {PlaceOf(planned)}. {en}",
                DetailFa: $"استقراری از {planned.Name} در {PlaceOf(planned)} در صف قرار می‌گیرد. {fa}",
                WarningsEn: warningsEn, WarningsFa: warningsFa);
        }

        var app = target.App!;
        if (Refusal(app, gitRef) is { } wrongRef) return AgentValidation.Refused(wrongRef.En, wrongRef.Fa);

        var room = await quota.CanAddAppAsync(context.WorkspaceId, app.InstanceSizeKey, excludeAppId: app.Id, ct);
        if (!room.Allowed)
            return AgentValidation.Refused(room.Reason ?? "Plan quota exceeded.", room.ReasonFa ?? room.Reason ?? "سقف پلن پر شده است.");

        if (await UnsettledDeploymentAsync(db, app.Id, ct) is { } inFlight)
            Add(($"Deployment #{inFlight.Number} is still in progress; this request joins it instead of starting another.",
                $"استقرار شمارهٔ {inFlight.Number} هنوز تمام نشده است؛ این درخواست به همان می‌پیوندد و استقرار تازه‌ای شروع نمی‌شود."));
        if (app.Environment is { IsProtected: true } protectedEnvironment) Add(ProtectedEnvironmentWarning(protectedEnvironment.Slug));

        var (sourceEn, sourceFa) = Source(app, gitRef);
        return new AgentValidation(true,
            TitleEn: $"Deploy {app.Slug}{(gitRef is null ? "" : $" at {gitRef}")}",
            TitleFa: $"استقرار {app.Slug}{(gitRef is null ? "" : $" از {gitRef}")}",
            DetailEn: $"{sourceEn} and releases {app.Slug} in {AgentResourceResolver.Place(app)}.",
            DetailFa: $"{sourceFa} و {app.Slug} را در {AgentResourceResolver.Place(app)} منتشر می‌کند.",
            WarningsEn: warningsEn, WarningsFa: warningsFa);

        void Add((string En, string Fa) warning)
        {
            warningsEn.Add(warning.En);
            warningsFa.Add(warning.Fa);
        }
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var gitRef = AgentArgs.String(args, "gitRef");
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDeploy, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;
        if (Refusal(app, gitRef) is { } wrongRef) return NotPossible(wrongRef.En, wrongRef.Fa);

        // AppsController.Deploy's own gate, kept: a suspended or over-quota workspace is refused before
        // the engine is asked anything.
        var room = await quota.CanAddAppAsync(context.WorkspaceId, app.InstanceSizeKey, excludeAppId: app.Id, ct);
        if (!room.Allowed)
            return NotPossible(room.Reason ?? "Plan quota exceeded.", room.ReasonFa ?? room.Reason ?? "سقف پلن پر شده است.");

        var inFlight = await UnsettledDeploymentAsync(db, app.Id, ct);
        Guid deploymentId;
        try
        {
            deploymentId = await deployments.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Assistant, context.UserId, gitRef ?? app.GitRef), ct);
        }
        catch (Exception ex) when (DeployRefusal(ex) is { } refusal)
        {
            return NotPossible(refusal.En, refusal.Fa);
        }

        await audit.LogAsync("app.deploy", "app", app.Id.ToString(), workspaceId: context.WorkspaceId, ct: ct);

        var deployment = await ReadDeploymentAsync(db, deploymentId, ct);
        var joined = inFlight is not null && inFlight.Id == deploymentId;
        var links = new List<AgentResourceLink> { AppLink(app) };
        if (deployment is not null) links.Insert(0, DeploymentLink(deployment.Id, deployment.Number));

        var state = deployment is null ? ("a deployment is queued", "استقراری در صف است", "queued") : Describe(deployment);
        var result = AgentJson.Serialize(new
        {
            app = app.Slug,
            deployment = deployment is null ? null : new { number = deployment.Number, status = state.Item3 },
            joinedExisting = joined
        });

        return joined
            ? new AgentToolResult(true, result,
                $"A deployment of '{app.Slug}' was already in progress, so nothing new was queued: {state.Item1}. Changes made after it started may need another deploy once it finishes.",
                $"استقراری از «{app.Slug}» از قبل در جریان بود، پس استقرار تازه‌ای در صف قرار نگرفت: {state.Item2}. تغییراتی که پس از شروع آن داده شده ممکن است پس از پایانش استقرار دیگری لازم داشته باشد.",
                Links: links)
            : new AgentToolResult(true, result,
                $"Deploy of '{app.Slug}' requested: {state.Item1}.",
                $"استقرار «{app.Slug}» درخواست شد: {state.Item2}.",
                Links: links);
    }

    /// <summary>A ref only means something for an app that is built from a repository.</summary>
    private static (string En, string Fa)? Refusal(App app, string? gitRef) =>
        gitRef is not null && app.GitRepositoryId is null
            ? ($"App '{app.Slug}' is not built from a Git repository, so there is no branch or tag to deploy; leave gitRef out.",
                $"اپ «{app.Slug}» از مخزن Git ساخته نمی‌شود، پس شاخه یا تگی برای استقرار ندارد؛ gitRef را ندهید.")
            : null;

    private static (string En, string Fa) Source(App app, string? gitRef) => app.SourceType switch
    {
        AppSourceType.PrebuiltImage or AppSourceType.Template when app.GitRepositoryId is null =>
            ($"Pulls {app.PrebuiltImage ?? "its image"} again", $"{app.PrebuiltImage ?? "ایمیج آن"} را دوباره می‌گیرد"),
        _ when app.GitRepositoryId is not null =>
            ($"Builds {gitRef ?? app.GitRef ?? "its branch"} again", $"{gitRef ?? app.GitRef ?? "شاخهٔ آن"} را دوباره می‌سازد"),
        _ => ("Builds it again from its last source", "آن را دوباره از آخرین سورسش می‌سازد")
    };
}
