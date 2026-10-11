using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Assistant;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>rotate_database_password</c> — a new password for a database, through
/// <see cref="IManagedServiceEngine.RotatePasswordAsync"/>, the call <c>DatabasesController.Rotate</c> makes.
/// The password itself never leaves the engine: what comes back is the list of apps that must be redeployed
/// before they use it — the ones whose stored variables the engine rewrote, and the ones attached to it,
/// which read it at their next deploy.
///
/// <para>
/// Deliberately not given a timeout of its own, unlike start and stop. Cancelling between the database
/// accepting the new password and the engine storing it would leave a password nobody knows, and every app
/// locked out; the engine's one-off container is the same call the panel's button makes, inside a request,
/// with no timeout either.
/// </para>
/// </summary>
public sealed class RotateDatabasePasswordTool(
    HarboraDbContext db,
    IManagedServiceEngine engine,
    ProjectAccessService access,
    AgentResourceResolver resolver,
    ServiceUsageService usage,
    ISecretProtector protector,
    ILogger<RotateDatabasePasswordTool> logger) : AgentWriteTool
{
    public override string Name => "rotate_database_password";

    public override string Description =>
        "Replaces a database's password with a new generated one and writes it into the apps that use it. The " +
        "password is never shown. Returns the apps that must be redeployed before they use it. Always shown to the " +
        "user for confirmation before it runs.";

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

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return refusal;

        var users = await VisibleUsersAsync(usage, access, service!.Id, ct);
        var warningsEn = new List<string>
        {
            users.Count == 0
                ? "No app you can see uses it."
                : $"{DatabaseProvisioningService.NamedList(users, isFa: false)} — they cannot connect after the change until each is redeployed."
        };
        var warningsFa = new List<string>
        {
            users.Count == 0
                ? "هیچ اپی که به آن دسترسی دارید از آن استفاده نمی‌کند."
                : $"{DatabaseProvisioningService.NamedList(users, isFa: true)} — پس از تعویض، تا هر کدام دوباره مستقر نشود نمی‌تواند وصل شود."
        };
        if (CredentialRotationPlan.RequiresRecreate(service.Type))
        {
            warningsEn.Add("The container is recreated to apply it: a short interruption for everything using it.");
            warningsFa.Add("برای اعمال آن کانتینر از نو ساخته می‌شود: وقفه‌ای کوتاه برای همهٔ استفاده‌کنندگان.");
        }

        return new AgentValidation(true,
            TitleEn: $"Rotate the password of database {service.Name}",
            TitleFa: $"تعویض رمز دیتابیس {service.Name}",
            DetailEn: $"{AgentResourceResolver.Lower(service.Type)} {service.Version} in {AgentResourceResolver.Place(service)}. Harbora generates a new password and writes it into the apps that use it; it is never shown.",
            DetailFa: $"{AgentResourceResolver.Lower(service.Type)} نسخهٔ {service.Version} در {AgentResourceResolver.Place(service)}. Harbora رمز تازه‌ای می‌سازد و در اپ‌های استفاده‌کننده می‌نویسد؛ رمز هرگز نمایش داده نمی‌شود.",
            WarningsEn: warningsEn,
            WarningsFa: warningsFa);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return FailedAs(refusal);

        var before = await StoredPasswordAsync(service!.Id, ct);
        var secrets = await AgentSecrets.ForDatabaseAsync(db, protector, service, ct);

        IReadOnlyList<RotatedApp> rewritten;
        try
        {
            rewritten = await engine.RotatePasswordAsync(service.Id, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Every failure but the caller's own cancellation: the engine's refusals (an engine it cannot rotate,
            // the database refusing the new password — after which nothing was stored), and anything a node or
            // Docker throws, possibly after the new password was stored (recreating a Redis container, say).
            logger.LogWarning(ex, "Rotating the password of database {Service} failed.", service.Id);

            // Asked, not assumed: a failure after the new password was stored must not be reported as a password
            // that did not change.
            var stored = await db.ManagedServices.AsNoTracking().FirstOrDefaultAsync(s => s.Id == service.Id, ct);
            var changed = stored is not null && stored.EncryptedPassword != before;

            // The engine's words can carry a one-off container's output — its command line too, which names the
            // new password in a form no shape rule matches — so once it changed, its new value is redacted as well.
            if (changed)
                secrets = [.. secrets, .. await AgentSecrets.ForDatabaseAsync(db, protector, stored!, ct)];
            var (reason, reasonFa) = Reasons(ex, secrets);

            if (changed)
                return new AgentToolResult(false,
                    AgentJson.Serialize(new { database = service.Name, rotated = true, completed = false }),
                    Error: $"The password of {service.Name} was changed, but the rest of the rotation failed: {reason} Check the database's page; the apps that use it need a redeploy.",
                    ErrorFa: $"رمز {service.Name} عوض شد، ولی بقیهٔ کار ناموفق ماند: {reasonFa} صفحهٔ دیتابیس را بررسی کنید؛ اپ‌های استفاده‌کننده باید دوباره مستقر شوند.",
                    Links: [DatabaseLink(service.Id, service.Name)]);

            return AgentToolResult.Failed(
                $"The password was not changed. {reason}",
                $"رمز عوض نشد. {reasonFa}");
        }

        // The engine returns quietly when its read finds nothing; a rotation that did not happen is not one.
        if (await StoredPasswordAsync(service.Id, ct) == before)
            return AgentToolResult.Failed(
                $"The password of {service.Name} was not changed.",
                $"رمز دیتابیس {service.Name} عوض نشد.");

        // Apps whose stored variables were rewritten, and apps attached to it (their connection string is built
        // from the database at their next deploy) — both run with the old password until then.
        var attached = await db.AppManagedServices.AsNoTracking()
            .Where(x => x.ManagedServiceId == service.Id).Select(x => x.AppId).ToListAsync(ct);
        var ids = rewritten.Select(r => r.AppId).Concat(attached).Distinct().ToList();
        var apps = await db.Apps.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.WorkspaceId == context.WorkspaceId)
            .Select(a => new { a.Id, a.Slug }).ToListAsync(ct);
        var redeploy = new List<(Guid Id, string Slug)>();
        foreach (var app in apps.OrderBy(a => a.Slug, StringComparer.Ordinal))
            if (await access.CanSeeAppAsync(app.Id, ct)) redeploy.Add((app.Id, app.Slug));

        var names = redeploy.Select(a => a.Slug).ToList();
        var links = new List<AgentResourceLink> { DatabaseLink(service.Id, service.Name) };
        links.AddRange(redeploy.Select(a => AppLink(a.Id, a.Slug)));

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                database = service.Name,
                rotated = true,
                redeploy = names,
                note = "The new password is not shown. The apps in 'redeploy' use it only after their next deploy."
            }),
            names.Count == 0
                ? $"Rotated the password of {service.Name}. No app you can see needs a redeploy."
                : $"Rotated the password of {service.Name}. Redeploy {string.Join(", ", names)} so they use it.",
            names.Count == 0
                ? $"رمز دیتابیس {service.Name} عوض شد. هیچ اپی که به آن دسترسی دارید به استقرار دوباره نیاز ندارد."
                : $"رمز دیتابیس {service.Name} عوض شد. {string.Join("، ", names)} را دوباره مستقر کنید تا از رمز تازه استفاده کنند.",
            Links: links);
    }

    /// <summary>
    /// What went wrong, in each language: redacted by value against <paramref name="secrets"/>, then by shape, and
    /// cut. A <see cref="QuotaRefusedException"/> carries its own Persian; anything else has only the English it
    /// was thrown with.
    /// </summary>
    private static (string En, string Fa) Reasons(Exception ex, IReadOnlyList<string> secrets)
    {
        static string Clean(string text, IReadOnlyList<string> known)
        {
            var redacted = AssistantRedaction.Redact(text, known).Text;
            return redacted.Length > 400 ? redacted[..400] + "…" : redacted;
        }

        var en = Clean(ex.Message, secrets);
        return (en, ex is QuotaRefusedException { ReasonFa: { Length: > 0 } fa } ? Clean(fa, secrets) : en);
    }

    private Task<string?> StoredPasswordAsync(Guid serviceId, CancellationToken ct) =>
        db.ManagedServices.AsNoTracking().Where(s => s.Id == serviceId).Select(s => s.EncryptedPassword).FirstOrDefaultAsync(ct);

    private async Task<(Domain.Services.ManagedService? Service, AgentValidation? Refusal)> CheckAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (service, refusal) = await ManagedDatabaseAsync(resolver, access, context, args, ct);
        if (refusal is not null) return (null, refusal);

        // A replica's password is its primary's: replication carries the change, and the engine copies the
        // stored value onto every replica when the primary is rotated.
        if (service!.PrimaryManagedServiceId is { } primaryId)
        {
            var primary = await db.ManagedServices.AsNoTracking()
                .Where(s => s.Id == primaryId).Select(s => s.Name).FirstOrDefaultAsync(ct) ?? "its primary";
            return (null, AgentValidation.Refused(
                $"{service.Name} is a read replica; rotate the password of {primary} instead — its replicas follow it.",
                $"{service.Name} یک رپلیکای فقط-خواندنی است؛ رمز {primary} را عوض کنید — رپلیکاها از آن پیروی می‌کنند."));
        }

        if (CredentialRotationPlan.WhyUnsupported(service.Type) is { } unsupported)
            return (null, AgentValidation.Refused(unsupported, service.Type switch
            {
                ManagedServiceType.MongoDb =>
                    "پوستهٔ MongoDB میان نسخه‌هایی که Harbora ارائه می‌دهد تغییر نام داده است، به همین دلیل تعویض رمز آن هنوز خودکار نیست. رمز را در خود دیتابیس عوض کنید و سرویس‌های متصل را دستی به‌روز کنید.",
                _ =>
                    "پیام‌رسان اعتبارنامه‌اش را هنگام راه‌اندازی می‌گیرد، پس تغییر رمز یعنی بازسازی کانتینر. پس از تغییر رمز در اینجا آن را بازسازی کنید."
            }));

        return (service, null);
    }
}
