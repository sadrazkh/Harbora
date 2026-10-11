using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>stop_database</c> — stops a database's container, through <see cref="IManagedServiceEngine.StopAsync"/>,
/// the call <c>DatabasesController.Stop</c> makes after its <c>Guard</c>. The engine reads the row unfiltered
/// and checks nothing itself, so the access check here is the only one.
/// </summary>
public sealed class StopDatabaseTool(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    ProjectAccessService access,
    AgentResourceResolver resolver,
    ServiceUsageService usage) : AgentWriteTool
{
    public override string Name => "stop_database";

    public override string Description =>
        "Stops a running database. Its data is kept and it can be started again with start_database, but every app " +
        "using it loses its database while it is stopped. Always shown to the user for confirmation before it runs.";

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

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.DatabasesManage;

    /// <summary>How long the node may take to stop it. Settable for tests.</summary>
    internal TimeSpan EngineTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return refusal;

        var users = await VisibleUsersAsync(usage, access, service!.Id, ct);
        var replicas = await VisibleReplicasAsync(db, access, service.Id, ct);
        var warningsEn = new List<string>
        {
            users.Count == 0
                ? "No app you can see uses it."
                : $"{DatabaseProvisioningService.NamedList(users, isFa: false)} — they lose their database while it is stopped."
        };
        var warningsFa = new List<string>
        {
            users.Count == 0
                ? "هیچ اپی که به آن دسترسی دارید از آن استفاده نمی‌کند."
                : $"{DatabaseProvisioningService.NamedList(users, isFa: true)} — تا وقتی متوقف است دیتابیس خود را از دست می‌دهند."
        };
        if (replicas.Count > 0)
        {
            warningsEn.Add($"Its replicas stop receiving changes: {string.Join(", ", replicas)}.");
            warningsFa.Add($"رپلیکاهای آن دیگر تغییری دریافت نمی‌کنند: {string.Join("، ", replicas)}.");
        }

        return new AgentValidation(true,
            TitleEn: $"Stop database {service.Name}",
            TitleFa: $"توقف دیتابیس {service.Name}",
            DetailEn: $"{AgentResourceResolver.Lower(service.Type)} {service.Version} in {AgentResourceResolver.Place(service)}. Its data is kept; it can be started again.",
            DetailFa: $"{AgentResourceResolver.Lower(service.Type)} نسخهٔ {service.Version} در {AgentResourceResolver.Place(service)}. داده‌هایش می‌ماند و می‌توان دوباره روشنش کرد.",
            WarningsEn: warningsEn,
            WarningsFa: warningsFa);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return FailedAs(refusal);
        var links = new[] { DatabaseLink(service!.Id, service.Name) };

        if (!await WithinAsync(EngineTimeout, token => engine.StopAsync(service.Id, token), ct))
            return new AgentToolResult(false, AgentJson.Serialize(new { database = service.Name, stopped = false, timedOut = true }),
                Error: $"The server did not finish stopping {service.Name} within {EngineTimeout.TotalSeconds:0} seconds. Check its page for its state.",
                ErrorFa: $"سرور توقف {service.Name} را در {EngineTimeout.TotalSeconds:0} ثانیه تمام نکرد. وضعیتش را در صفحهٔ آن ببینید.",
                Links: links);

        var status = await db.ManagedServices.AsNoTracking()
            .Where(s => s.Id == service.Id).Select(s => (ServiceStatus?)s.Status).FirstOrDefaultAsync(ct);
        if (status != ServiceStatus.Stopped)
            return new AgentToolResult(false, AgentJson.Serialize(new { database = service.Name, stopped = false }),
                Error: $"{service.Name} was not stopped.", ErrorFa: $"{service.Name} متوقف نشد.", Links: links);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { database = service.Name, status = "stopped" }),
            $"Stopped {service.Name}.",
            $"دیتابیس {service.Name} متوقف شد.",
            Links: links);
    }

    private async Task<(Domain.Services.ManagedService? Service, AgentValidation? Refusal)> CheckAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await ManagedDatabaseAsync(resolver, access, context, args, ct);
        if (refusal is not null) return (null, refusal);

        if (service!.Status == ServiceStatus.Stopped)
            return (null, AgentValidation.Refused($"{service.Name} is already stopped.", $"{service.Name} از قبل متوقف است."));

        return (service, null);
    }
}
