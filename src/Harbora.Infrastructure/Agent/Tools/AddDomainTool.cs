using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>add_domain</c> — attaches a custom host name to an app through
/// <see cref="IAppConfigurationService.AddDomainAsync"/>, which refuses a host in use anywhere on the
/// platform, one of the platform's own host names, and one past the plan's domain ceiling.
///
/// <para>
/// For an app an earlier step of the same plan creates ("create web, then give it web.example.com"),
/// only what needs no app row is checked at validation — the host is given, not in use, and the plan
/// has room — and the card says the rest is checked when the step runs.
/// </para>
/// </summary>
public sealed class AddDomainTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppConfigurationService configuration,
    IQuotaService quota) : AgentAppTool(resolver, access)
{
    public override string Name => "add_domain";

    public override string Description =>
        "Adds a custom domain (host name, e.g. shop.example.com) to an app, with HTTPS by default. The host must not be " +
        "used by any app on the platform. It is routed on the app's next deploy; the user points the domain's DNS at the " +
        "server. Nothing changes until the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug (or one an earlier step of this plan creates)." },
            "host": { "type": "string", "description": "The host name, e.g. shop.example.com — no scheme, no path." },
            "ssl": { "type": "boolean", "description": "Serve it over HTTPS with an automatic certificate. Default true." }
          },
          "required": ["app", "host"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsEnv;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var host = Host(args);
        var ssl = AgentArgs.Bool(args, "ssl") ?? true;

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);

        bool becomesPrimary;
        string slug;
        if (target.App is { } app)
        {
            var refusals = await configuration.ValidateAddDomainAsync(ActorOf(context), app.Id, host, ct);
            if (refusals.Count > 0) return Refused(refusals);
            becomesPrimary = !await db.Domains.AnyAsync(d => d.AppId == app.Id, ct);
            slug = app.Slug;
        }
        else
        {
            // The checks AddDomainAsync makes that need no app row, in its order and its words.
            if (string.IsNullOrWhiteSpace(host)) return Refused([AppRefusals.HostRequired]);
            if (await db.Domains.AnyAsync(d => d.Host == host, ct)) return Refused([AppRefusals.DomainInUse]);
            var room = await quota.CanAddGovernedResourcesAsync(context.WorkspaceId, new GovernanceQuotaDelta(Domains: 1), ct);
            if (!room.Allowed) return Refused([AppRefusals.Quota(room, "Plan quota exceeded.", "سقف پلن پر شده است.")]);

            // A new app usually gets an address of its own under the platform's root domain, so this one
            // is not promised to be its primary.
            becomesPrimary = false;
            slug = target.Planned!.Name;
        }

        var detailEn = $"HTTPS {(ssl ? "on (a certificate is requested automatically)" : "off")}.";
        var detailFa = $"HTTPS {(ssl ? "روشن (گواهی خودکار گرفته می‌شود)" : "خاموش")}.";
        if (becomesPrimary)
        {
            detailEn += " It becomes the app's primary address.";
            detailFa += " نشانی اصلی اپ می‌شود.";
        }
        if (target.Planned is { } planned)
        {
            var (en, fa) = PlannedNote(planned);
            detailEn += " " + en;
            detailFa += " " + fa;
        }

        return new AgentValidation(true,
            TitleEn: $"Add domain {host} to {slug}",
            TitleFa: $"افزودن دامنهٔ {host} به {slug}",
            DetailEn: detailEn,
            DetailFa: detailFa,
            WarningsEn: ["It is routed on the app's next deploy; the domain's DNS has to point at this server."],
            WarningsFa: ["با استقرار بعدی اپ مسیردهی می‌شود؛ DNS دامنه باید به این سرور اشاره کند."]);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var host = Host(args);
        var ssl = AgentArgs.Bool(args, "ssl") ?? true;

        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsEnv, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var outcome = await configuration.AddDomainAsync(ActorOf(context), app.Id, host, ssl, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var added = outcome.Value!;

        return new AgentToolResult(true,
            AgentJson.Serialize(new { app = app.Slug, domain = added.Host, ssl = added.SslEnabled, primary = added.IsPrimary, routedOn = "next deploy" }),
            $"Added {added.Host} to '{app.Slug}'{(added.IsPrimary ? " as its primary address" : "")}. It is routed on the app's next deploy.",
            $"{added.Host} به «{app.Slug}» اضافه شد{(added.IsPrimary ? " و نشانی اصلی آن است" : "")}. با استقرار بعدی اپ مسیردهی می‌شود.",
            Links: [AppLink(app)]);
    }

    /// <summary>The host as the service stores it — trimmed and lower-cased — so the card, the in-use
    /// check and the row all name the same thing.</summary>
    private static string Host(JsonElement args) => (AgentArgs.String(args, "host") ?? "").ToLowerInvariant();
}
