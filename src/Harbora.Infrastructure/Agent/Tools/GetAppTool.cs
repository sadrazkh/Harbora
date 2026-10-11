using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Assistant;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>get_app</c> — one app in detail: status, domains, environment variables, attached databases and
/// its last five deployments.
///
/// <para>
/// A secret environment variable is reported as set, never with its value — not even redacted: the
/// model has no use for it and the boundary is simplest when the value is never read. A plain variable
/// carries its value, because "what is PORT set to?" is a fair question. Free text — plain values and
/// deployment errors — is redacted by value against every secret the app holds
/// (<see cref="AgentSecrets"/>): an error message that echoes a rejected key carries it in no shape a
/// pattern would recognise. The shape-based redaction every tool result passes through runs on top.
/// </para>
/// </summary>
public sealed class GetAppTool(HarboraDbContext db, AgentResourceResolver resolver, ISecretProtector protector) : AgentReadTool
{
    private const int MaxValueLength = 200;

    public override string Name => "get_app";

    public override string Description =>
        "Shows one app in detail: project and environment, status, kind, source, size, domains, environment variables " +
        "(secret values are never shown, only that they are set), attached databases and the last five deployments.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug, as the snapshot shows it." }
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

        var domains = await db.Domains.AsNoTracking()
            .Where(d => d.AppId == app.Id)
            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Host)
            .Select(d => new { host = d.Host, primary = d.IsPrimary, ssl = d.SslEnabled })
            .ToListAsync(ct);

        var variables = await db.EnvironmentVariables.AsNoTracking()
            .Where(v => v.AppId == app.Id)
            .OrderBy(v => v.Key)
            .Select(v => new { v.Key, v.IsSecret, v.Value, v.AvailableAtBuild })
            .ToListAsync(ct);

        var attachments = await db.AppManagedServices.AsNoTracking()
            .Where(x => x.AppId == app.Id && x.ManagedService!.WorkspaceId == context.WorkspaceId)
            .OrderBy(x => x.AttachOrder)
            .Select(x => new
            {
                database = x.ManagedService!.Name,
                engine = x.ManagedService.Type,
                alias = x.Alias,
                logicalDatabase = x.Database != null ? x.Database.Name : null
            })
            .ToListAsync(ct);

        var deployments = await db.Deployments.AsNoTracking()
            .Where(d => d.AppId == app.Id && d.WorkspaceId == context.WorkspaceId)
            .OrderByDescending(d => d.Number)
            .Take(5)
            .Select(d => new
            {
                d.Number, d.Status, d.Trigger, d.CreatedAt, d.StartedAt, d.FinishedAt,
                d.CommitSha, d.CommitMessage, d.ErrorMessage
            })
            .ToListAsync(ct);

        var secrets = await AgentSecrets.ForAppAsync(db, protector, app.Id, context.WorkspaceId, ct);
        string? Scrub(string? text, int max) =>
            text is null ? null : Clip(AssistantRedaction.Redact(text, secrets).Text, max);

        var payload = new
        {
            slug = app.Slug,
            name = app.Name,
            project = app.Environment?.Project?.Slug,
            environment = app.Environment?.Slug,
            status = AgentResourceResolver.Lower(app.Status),
            kind = AgentResourceResolver.Lower(app.Kind),
            source = AgentResourceResolver.Lower(app.SourceType),
            image = app.PrebuiltImage,
            gitRef = app.GitRef,
            size = app.InstanceSizeKey,
            replicas = app.DesiredReplicas,
            maintenanceMode = app.MaintenanceMode,
            domains,
            environmentVariables = variables.Select(v => v.IsSecret
                ? (object)new { key = v.Key, secret = "set", availableAtBuild = v.AvailableAtBuild }
                : new { key = v.Key, value = Scrub(v.Value, MaxValueLength), availableAtBuild = v.AvailableAtBuild }).ToList(),
            attachedDatabases = attachments.Select(a => new
            {
                a.database, engine = AgentResourceResolver.Lower(a.engine), a.alias, a.logicalDatabase
            }).ToList(),
            lastDeployments = deployments.Select(d => new
            {
                number = d.Number,
                status = AgentResourceResolver.Lower(d.Status),
                trigger = AgentResourceResolver.Lower(d.Trigger),
                createdAt = d.CreatedAt,
                startedAt = d.StartedAt,
                finishedAt = d.FinishedAt,
                commit = d.CommitSha is null ? null : d.CommitSha[..Math.Min(8, d.CommitSha.Length)],
                commitMessage = Scrub(d.CommitMessage?.Split('\n')[0], 120),
                error = Scrub(d.ErrorMessage, 300)
            }).ToList()
        };

        return Found(payload, $"Read app {app.Slug}", $"اپ {app.Slug} بررسی شد");
    }
}
