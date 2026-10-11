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

    /// <summary>The longest a single log line is sent; a minified bundle printed on one line says nothing
    /// in its 5,001st character that its 200th did not.</summary>
    public const int MaxLineLength = 200;

    /// <summary>The most the whole result may serialise to — under the cut, with room for the redaction
    /// every result passes through on the way to lengthen it.</summary>
    public const int ResultBudget = AgentJson.MaxToolResultLength - 300;

    public override string Name => "get_deployment";

    public override string Description =>
        "Shows one deployment of an app: its status, trigger, when it started and finished, the error if it failed, and " +
        "the end of its log — up to the last 40 lines, long lines shortened, secrets removed. Without a number, the " +
        "app's latest deployment.";

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

        var logs = db.DeploymentLogs.AsNoTracking().Where(l => l.DeploymentId == deployment.Id);
        var totalRows = await logs.CountAsync(ct);
        var tail = await logs
            .OrderByDescending(l => l.Sequence)
            .Take(LogLines)
            .Select(l => l.Message)
            .ToListAsync(ct);
        tail.Reverse();

        var secrets = await AgentSecrets.ForAppAsync(db, protector, app.Id, context.WorkspaceId, ct);
        var error = AssistantRedaction.Redact(deployment.ErrorMessage, secrets).Text;

        // Redacted as one text, then split, then clipped and trimmed — in that order. A secret can span
        // lines: a private key is a BEGIN line, a body and an END line, and a multi-line secret value is
        // matched whole. Redacting row by row would match neither, and dropping the oldest lines afterwards
        // could then keep a key's body without the BEGIN line the backstop pattern needs.
        var redacted = AssistantRedaction.Redact(string.Join('\n', tail), secrets).Text;
        List<string> lines = redacted.Length == 0
            ? []
            : redacted.Split('\n').Select(line => Clip(line, MaxLineLength) ?? "").ToList();
        var olderRows = totalRows - tail.Count;

        object Payload(int dropped) => new
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
            // Everything earlier that is not shown: the log's lines before the last LogLines, and the
            // oldest of those that were dropped so the result fits.
            earlierLinesLeftOut = olderRows + dropped,
            logTail = lines.Skip(dropped).ToList()
        };

        // The result is cut to MaxToolResultLength on its way to the model, and the cut keeps the head —
        // but the line that says why a deployment failed is nearly always the last one. So the oldest
        // lines are dropped here until the whole result fits, and the tail is what arrives.
        var droppedLines = 0;
        while (droppedLines < lines.Count && AgentJson.Serialize(Payload(droppedLines)).Length > ResultBudget)
            droppedLines++;

        return Found(Payload(droppedLines),
            $"Read deployment #{deployment.Number} of {app.Slug}",
            $"دیپلوی شمارهٔ {deployment.Number} از {app.Slug} بررسی شد");
    }
}
