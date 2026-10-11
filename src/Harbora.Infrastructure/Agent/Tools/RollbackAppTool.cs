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
/// <c>rollback_app</c> — re-releases an earlier deployment's image (ADR-006), through
/// <see cref="IRollbackPlanner.PrepareAsync"/> and then <see cref="IDeploymentEngine.QueueDeploymentAsync"/>
/// with <c>RollbackToDeploymentId</c> — <c>AppsController.Rollback</c>'s two calls, its capability
/// (AppsDeploy) and its <c>app.rollback</c> audit entry.
///
/// <para>
/// The planner is asked at validation, so the card names exactly what is restored (or why it cannot
/// be: the image was pruned, the server is unreachable), and again at execution, because retention can
/// prune the image in between. Its reasons are English only; the Persian says the rollback is not
/// possible and carries them. The deployment is recorded as started by the assistant; the pipeline
/// knows it is a rollback from <c>RolledBackFromId</c>, not from the trigger.
/// </para>
/// </summary>
public sealed class RollbackAppTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IRollbackPlanner planner,
    IDeploymentEngine deployments,
    IAuditLogger audit) : AgentAppTool(resolver, access)
{
    public override string Name => "rollback_app";

    public override string Description =>
        "Rolls an app back to an earlier successful deployment by re-releasing that deployment's stored image (nothing " +
        "is rebuilt). Without a number, the previous successful deployment before the live one. Only the app's code goes " +
        "back: environment variables, attached databases and data stay as they are now. Always shown to the user for " +
        "approval; returns the queued deployment, it does not wait for it.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "number": { "type": "integer", "description": "The deployment number to roll back to. Omit for the previous successful one." }
          },
          "required": ["app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.AppsDeploy;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDeploy, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var (to, plan, error) = await PrepareAsync(app, AgentArgs.Int(args, "number"), ct);
        if (error is { } refused) return AgentValidation.Refused(refused.En, refused.Fa);

        var commit = plan!.CommitSha is { Length: > 0 } sha ? sha[..Math.Min(8, sha.Length)] : null;
        var message = plan.CommitMessage?.Split('\n')[0] is { Length: > 0 } line ? (line.Length > 80 ? line[..80] + "…" : line) : null;
        var whatEn = commit is null ? $"the image of #{to}" : $"the image of #{to} (commit {commit}{(message is null ? "" : $" “{message}”")})";
        var whatFa = commit is null ? $"ایمیج استقرار شمارهٔ {to}" : $"ایمیج استقرار شمارهٔ {to} (کامیت {commit}{(message is null ? "" : $" «{message}»")})";
        var liveEn = plan.CurrentNumber is { } live ? $", replacing #{live}, which is live now" : "";
        var liveFa = plan.CurrentNumber is { } liveNumber ? $" و جای استقرار شمارهٔ {liveNumber} را که الان فعال است می‌گیرد" : "";

        var warningsEn = new List<string>
        {
            "This replaces the app's running containers with the older version.",
            "Only the code goes back: environment variables, attached databases and their data stay as they are now."
        };
        var warningsFa = new List<string>
        {
            "کانتینرهای در حال اجرای اپ با نسخهٔ قدیمی‌تر جایگزین می‌شوند.",
            "فقط کد برمی‌گردد: متغیرهای محیطی، دیتابیس‌های وصل‌شده و داده‌هایشان همان‌طور که الان هستند می‌مانند."
        };
        if (app.Environment is { IsProtected: true } guarded)
        {
            var (en, fa) = ProtectedEnvironmentWarning(guarded.Slug);
            warningsEn.Add(en);
            warningsFa.Add(fa);
        }

        return new AgentValidation(true,
            TitleEn: $"Roll back {app.Slug} to deployment #{to}",
            TitleFa: $"بازگردانی {app.Slug} به استقرار شمارهٔ {to}",
            DetailEn: $"Re-releases {whatEn}{liveEn}, in {AgentResourceResolver.Place(app)}. Nothing is rebuilt.",
            DetailFa: $"{whatFa} دوباره منتشر می‌شود{liveFa}، در {AgentResourceResolver.Place(app)}. چیزی دوباره ساخته نمی‌شود.",
            WarningsEn: warningsEn,
            WarningsFa: warningsFa);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDeploy, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        // Asked again rather than trusted from the card: retention may have pruned the image since.
        var (to, plan, error) = await PrepareAsync(app, AgentArgs.Int(args, "number"), ct);
        if (error is { } refused) return NotPossible(refused.En, refused.Fa);

        Guid deploymentId;
        try
        {
            deploymentId = await deployments.QueueDeploymentAsync(
                new DeploymentRequest(app.Id, DeploymentTrigger.Assistant, context.UserId, RollbackToDeploymentId: plan!.TargetId), ct);
        }
        catch (Exception ex) when (DeployRefusal(ex) is { } refusal)
        {
            // A deploy that started between the check above and now: the engine refuses to merge them.
            return NotPossible(refusal.En, refusal.Fa);
        }

        await audit.LogAsync("app.rollback", "app", app.Id.ToString(),
            metadataJson: JsonSerializer.Serialize(new { toDeploymentId = plan.TargetId }),
            workspaceId: context.WorkspaceId, ct: ct);

        var deployment = await ReadDeploymentAsync(db, deploymentId, ct);
        var state = deployment is null ? ("a deployment is queued", "استقراری در صف است", "queued") : Describe(deployment);
        var links = new List<AgentResourceLink> { AppLink(app) };
        if (deployment is not null) links.Insert(0, DeploymentLink(deployment.Id, deployment.Number));

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                app = app.Slug,
                rollbackTo = to,
                deployment = deployment is null ? null : new { number = deployment.Number, status = state.Item3 }
            }),
            $"Rollback of '{app.Slug}' to #{to} requested: {state.Item1}.",
            $"بازگردانی «{app.Slug}» به استقرار شمارهٔ {to} درخواست شد: {state.Item2}.",
            Links: links);
    }

    private sealed record Prepared(Guid TargetId, string? CommitSha, string? CommitMessage, int? CurrentNumber);

    /// <summary>
    /// The deployment to go back to — by number, or the newest successful one older than the live one —
    /// and the planner's verdict on it.
    /// </summary>
    private async Task<(int Number, Prepared? Plan, (string En, string Fa)? Error)> PrepareAsync(
        App app, int? number, CancellationToken ct)
    {
        // Refused outright while anything is in flight. The engine would refuse a rollback onto a deploy,
        // but join it onto a rollback already running — to whatever version that one restores, which is
        // not necessarily the one this card names.
        if (await UnsettledDeploymentAsync(db, app.Id, ct) is { } inFlight)
            return (0, null, (
                $"Deployment #{inFlight.Number} of '{app.Slug}' is still {AgentResourceResolver.Lower(inFlight.Status)}. Wait for it to finish (or cancel it), then roll back.",
                $"استقرار شمارهٔ {inFlight.Number} از «{app.Slug}» هنوز تمام نشده است ({AgentResourceResolver.Lower(inFlight.Status)}). صبر کنید تمام شود (یا لغوش کنید)، بعد بازگردانی کنید."));

        var history = db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.WorkspaceId == app.WorkspaceId);

        Guid? targetId;
        int targetNumber;
        if (number is { } wanted)
        {
            targetId = await history.Where(d => d.Number == wanted).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
            if (targetId is null)
                return (wanted, null, ($"App '{app.Slug}' has no deployment #{wanted}.", $"اپ «{app.Slug}» استقرار شمارهٔ {wanted} ندارد."));
            targetNumber = wanted;
        }
        else
        {
            var liveNumber = app.ActiveDeploymentId is { } activeId
                ? await history.Where(d => d.Id == activeId).Select(d => (int?)d.Number).FirstOrDefaultAsync(ct)
                : null;
            var previous = await history
                .Where(d => (d.Status == DeploymentStatus.Succeeded || d.Status == DeploymentStatus.RolledBack)
                            && d.Id != app.ActiveDeploymentId
                            && (liveNumber == null || d.Number < liveNumber))
                .OrderByDescending(d => d.Number)
                .Select(d => new { d.Id, d.Number })
                .FirstOrDefaultAsync(ct);
            if (previous is null)
                return (0, null, ($"App '{app.Slug}' has no earlier successful deployment to roll back to.",
                    $"اپ «{app.Slug}» استقرار موفق قبلی‌ای برای بازگشت به آن ندارد."));
            targetId = previous.Id;
            targetNumber = previous.Number;
        }

        var plan = await planner.PrepareAsync(app.Id, targetId.Value, ct);
        if (!plan.CanRollback)
        {
            var reason = plan.Reason ?? $"Deployment #{targetNumber} cannot be rolled back to.";
            return (targetNumber, null, (reason, $"بازگردانی ممکن نیست: {reason}"));
        }

        return (targetNumber, new Prepared(targetId.Value, plan.CommitSha, plan.CommitMessage, plan.CurrentNumber), null);
    }
}
