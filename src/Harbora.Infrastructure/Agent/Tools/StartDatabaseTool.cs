using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>start_database</c> — starts a stopped database, through <see cref="IManagedServiceEngine.StartAsync"/>,
/// the call <c>DatabasesController.Start</c> makes after its <c>Guard</c>. The billing gate the engine asks is
/// asked here first too (<see cref="IBillingGate"/> only reads), so a workspace that may not start anything is
/// told so on the card's way in rather than after approval; the engine still asks again, and its refusal is
/// mapped in both languages.
/// </summary>
public sealed class StartDatabaseTool(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    IBillingGate billingGate,
    ProjectAccessService access,
    AgentResourceResolver resolver,
    IOptions<BillingOptions> billing) : AgentWriteTool
{
    public override string Name => "start_database";

    public override string Description =>
        "Starts a stopped database again, with its data as it was. Does nothing by itself: it becomes a step of a " +
        "plan the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "database": { "type": "string", "description": "The database's name." },
            "project": { "type": "string", "description": "The project's slug, when the name is ambiguous." },
            "environment": { "type": "string", "description": "The environment's slug, when the name is ambiguous." }
          },
          "required": ["database"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.DatabasesManage;

    /// <summary>How long the node may take to start it. Settable for tests.</summary>
    internal TimeSpan EngineTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return refusal;

        var billed = billing.Value.Enabled && !await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(w => w.Id == context.WorkspaceId && w.IsDefault, ct);

        return new AgentValidation(true,
            TitleEn: $"Start database {service!.Name}",
            TitleFa: $"راه‌اندازی دیتابیس {service.Name}",
            DetailEn: $"{AgentResourceResolver.Lower(service.Type)} {service.Version} in {AgentResourceResolver.Place(service)}, with its data as it was.",
            DetailFa: $"{AgentResourceResolver.Lower(service.Type)} نسخهٔ {service.Version} در {AgentResourceResolver.Place(service)}، با همان داده‌های قبلی.",
            WarningsEn: billed ? ["While it runs, its size is charged by the hour."] : null,
            WarningsFa: billed ? ["تا وقتی روشن است، هزینهٔ اندازهٔ آن ساعتی کسر می‌شود."] : null);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return FailedAs(refusal);
        var links = new[] { DatabaseLink(service!.Id, service.Name) };

        bool finished;
        try
        {
            finished = await WithinAsync(EngineTimeout, token => engine.StartAsync(service.Id, token), ct);
        }
        catch (QuotaRefusedException ex)
        {
            return QuotaFailed(ex);
        }

        if (!finished)
            return new AgentToolResult(false, AgentJson.Serialize(new { database = service.Name, started = false, timedOut = true }),
                Error: $"The server did not finish starting {service.Name} within {EngineTimeout.TotalSeconds:0} seconds. Check its page for its state.",
                ErrorFa: $"سرور راه‌اندازی {service.Name} را در {EngineTimeout.TotalSeconds:0} ثانیه تمام نکرد. وضعیتش را در صفحهٔ آن ببینید.",
                Links: links);

        var status = await db.ManagedServices.AsNoTracking()
            .Where(s => s.Id == service.Id).Select(s => (ServiceStatus?)s.Status).FirstOrDefaultAsync(ct);
        if (status != ServiceStatus.Running)
            return new AgentToolResult(false, AgentJson.Serialize(new { database = service.Name, started = false }),
                Error: $"{service.Name} was not started.", ErrorFa: $"{service.Name} راه‌اندازی نشد.", Links: links);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { database = service.Name, status = "running" }),
            $"Started {service.Name}.",
            $"دیتابیس {service.Name} راه‌اندازی شد.",
            Links: links);
    }

    private async Task<(Domain.Services.ManagedService? Service, AgentValidation? Refusal)> CheckAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await ManagedDatabaseAsync(resolver, access, context, args, ct);
        if (refusal is not null) return (null, refusal);

        if (service!.Status == ServiceStatus.Running)
            return (null, AgentValidation.Refused($"{service.Name} is already running.", $"{service.Name} از قبل روشن است."));

        var mayStart = await billingGate.CanStartAsync(context.WorkspaceId, BilledResourceType.Service, service.Id, ct);
        if (!mayStart.Allowed) return (null, QuotaRefused(mayStart));

        return (service, null);
    }
}
