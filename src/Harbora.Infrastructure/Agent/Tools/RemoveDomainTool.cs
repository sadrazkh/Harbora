using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>remove_domain</c> — detaches a host from an app through
/// <see cref="IAppConfigurationService.RemoveDomainAsync"/>, which also deletes its routes and promotes a
/// surviving domain when the primary goes. The card names which host that will be — or that none is
/// left — using the service's own succession rule (oldest surviving domain), because "my app has no
/// address any more" is the consequence a person most needs to see before approving.
/// </summary>
public sealed class RemoveDomainTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration) : AgentAppTool(resolver, access)
{
    public override string Name => "remove_domain";

    public override string Description =>
        "Removes a domain (host name) from an app and deletes its routes; visitors using that host can no longer reach " +
        "the app. When it was the primary address, the oldest remaining domain becomes primary. Always shown to the user " +
        "for approval.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "host": { "type": "string", "description": "The host name to remove, e.g. shop.example.com." }
          },
          "required": ["app", "host"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.DangerousWrite;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var host = Host(args);
        if (host.Length == 0) return Refused([AppRefusals.HostRequired]);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var refusals = await configuration.ValidateRemoveDomainAsync(ActorOf(context), app.Id, AppItemRef.ByName(host), ct);
        if (refusals.Count > 0) return Refused(refusals);

        // RemoveDomainAsync's succession, read without touching anything: a surviving primary keeps the
        // role; otherwise the oldest survivor takes it; otherwise there is none.
        var domains = await db.Domains.AsNoTracking()
            .Where(d => d.AppId == app.Id)
            .OrderBy(d => d.CreatedAt).ThenBy(d => d.Id)
            .Select(d => new { d.Host, d.IsPrimary })
            .ToListAsync(ct);
        var removed = domains.First(d => d.Host == host);
        var survivors = domains.Where(d => d.Host != host).ToList();

        string primaryEn, primaryFa;
        if (survivors.Count == 0)
        {
            primaryEn = $"It is the app's only domain: {app.Slug} is left with no address.";
            primaryFa = $"این تنها دامنهٔ اپ است: {app.Slug} بدون هیچ نشانی می‌ماند.";
        }
        else if (survivors.FirstOrDefault(d => d.IsPrimary) is { } stays)
        {
            primaryEn = $"{stays.Host} stays the primary address.";
            primaryFa = $"{stays.Host} همچنان نشانی اصلی می‌ماند.";
        }
        else
        {
            primaryEn = $"{(removed.IsPrimary ? "It is the primary address; " : "")}{survivors[0].Host} becomes the primary address.";
            primaryFa = $"{(removed.IsPrimary ? "این نشانی اصلی است؛ " : "")}{survivors[0].Host} نشانی اصلی می‌شود.";
        }

        return new AgentValidation(true,
            TitleEn: $"Remove domain {host} from {app.Slug}",
            TitleFa: $"حذف دامنهٔ {host} از {app.Slug}",
            DetailEn: $"{host} is detached from {app.Slug} and its routes are deleted. {primaryEn}",
            DetailFa: $"{host} از {app.Slug} جدا و مسیرهایش حذف می‌شود. {primaryFa}",
            WarningsEn: [$"Visitors using {host} can no longer reach the app."],
            WarningsFa: [$"بازدیدکنندگانی که از {host} استفاده می‌کنند دیگر به اپ نمی‌رسند."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var host = Host(args);
        if (host.Length == 0) return Failed([AppRefusals.HostRequired]);

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.RemoveDomainAsync(ActorOf(context), app.Id, AppItemRef.ByName(host), ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var removed = outcome.Value!;

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, removed = removed.Host, newPrimary = removed.NewPrimaryHost }),
            $"Removed {removed.Host} from '{app.Slug}'.{(removed.NewPrimaryHost is { } nextEn ? $" {nextEn} is now its primary address." : "")}",
            $"{removed.Host} از «{app.Slug}» حذف شد.{(removed.NewPrimaryHost is { } nextFa ? $" اکنون {nextFa} نشانی اصلی آن است." : "")}",
            Links: [AppLink(app)]);
    }

    private static string Host(JsonElement args) => (AgentArgs.String(args, "host") ?? "").ToLowerInvariant();
}
