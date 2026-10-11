using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>get_deployment</c> — one deployment's status, timestamps and the last
/// <see cref="LogLines"/> lines of its log.
///
/// <para>
/// A deployment log is the most secret-dense thing a PaaS holds (see <see cref="AssistantRedaction"/>),
/// so this tool redacts by value before anything leaves it: every secret Harbora holds for the app
/// (<see cref="AgentSecrets.ForAppAsync"/>), decrypted, because a secret is only recognisable in a log
/// as its plaintext. Shape-based redaction then runs again on the whole result, as it does for every
/// tool.
/// </para>
/// </summary>
public sealed class GetDeploymentTool(HarboraDbContext db, AgentResourceResolver resolver, ISecretProtector protector)
    : AgentReadTool
{
    public const int LogLines = 40;

    public override string Name => "get_deployment";

    public override string Description =>
        "Shows one deployment of an app: its status, trigger, when it started and finished, the error if it failed, and " +
        "the last 40 lines of its log (secrets removed). Without a number, the app's latest deployment.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "number": { "type": "integer", "description": "The deployment number. Omit for the latest." }
          },
          "required": ["app"],
          "additionalProperties": false
        }
        """;

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var lookup = await resolver.AppAsync(context, AgentArgs.String(args, "app"), ct);
        if (!lookup.Found) return NotFound(lookup.Error!, lookup.ErrorFa!);
        var app = lookup.Value!;

        var number = AgentArgs.Int(args, "number");
        var deployments = db.Deployments.AsNoTracking().Where(d => d.AppId == app.Id && d.WorkspaceId == context.WorkspaceId);
        var deployment = number is { } n
            ? await deployments.FirstOrDefaultAsync(d => d.Number == n, ct)
            : await deployments.OrderByDescending(d => d.Number).FirstOrDefaultAsync(ct);

        if (deployment is null)
            return number is null
                ? NotFound($"App '{app.Slug}' has not been deployed yet.", $"اپ «{app.Slug}» هنوز دیپلوی نشده است.")
                : NotFound($"App '{app.Slug}' has no deployment #{number}.", $"اپ «{app.Slug}» دیپلوی شمارهٔ {number} ندارد.");

        var tail = await db.DeploymentLogs.AsNoTracking()
            .Where(l => l.DeploymentId == deployment.Id)
            .OrderByDescending(l => l.Sequence)
            .Take(LogLines)
            .Select(l => l.Message)
            .ToListAsync(ct);
        tail.Reverse();

        var secrets = await AgentSecrets.ForAppAsync(db, protector, app.Id, context.WorkspaceId, ct);
        var log = AssistantRedaction.Redact(string.Join('\n', tail), secrets).Text;
        var error = AssistantRedaction.Redact(deployment.ErrorMessage, secrets).Text;

        var payload = new
        {
            app = app.Slug,
            number = deployment.Number,
            status = AgentResourceResolver.Lower(deployment.Status),
            trigger = AgentResourceResolver.Lower(deployment.Trigger),
            createdAt = deployment.CreatedAt,
            startedAt = deployment.StartedAt,
            finishedAt = deployment.FinishedAt,
            commit = deployment.CommitSha,
            error = string.IsNullOrEmpty(error) ? null : Clip(error, 600),
            logTail = log.Length == 0 ? Array.Empty<string>() : log.Split('\n')
        };

        return Found(payload,
            $"Read deployment #{deployment.Number} of {app.Slug}",
            $"دیپلوی شمارهٔ {deployment.Number} از {app.Slug} بررسی شد");
    }
}
