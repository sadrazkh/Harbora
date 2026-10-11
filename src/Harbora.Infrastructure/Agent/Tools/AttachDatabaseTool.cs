using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>attach_database</c> — gives an app a database's connection settings, through
/// <see cref="IDatabaseAttachmentService"/>, the service <c>DatabasesController.Attach</c> calls. Either side
/// may be something an earlier step of the plan creates; then only what can be judged without that row is
/// checked here — the other side's access, and whether the two will sit in the same environment — and the
/// service checks everything when the step runs.
/// </summary>
public sealed class AttachDatabaseTool(
    HarboraDbContext db,
    IDatabaseAttachmentService attachments,
    ProjectAccessService access,
    AgentResourceResolver resolver) : AgentWriteTool
{
    public override string Name => "attach_database";

    public override string Description =>
        "Attaches a database to an app in the same environment: the app receives the database's connection settings " +
        "as environment variables prefixed with an alias (the database's name in capitals by default) on its next " +
        "deploy. Either may be one an earlier step of the same plan creates. Does nothing by itself: it becomes a step " +
        "of a plan the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "database": { "type": "string", "description": "The database's name." },
            "app": { "type": "string", "description": "The app's slug." },
            "project": { "type": "string", "description": "The database's project slug, when its name is ambiguous." },
            "environment": { "type": "string", "description": "The database's environment slug, when its name is ambiguous." },
            "alias": { "type": "string", "description": "The variable prefix, for example 'SHOP_DB'. Omit to use the database's name." },
            "logicalDatabase": { "type": "string", "description": "A logical database inside the instance. Omit for its default one." }
          },
          "required": ["database", "app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    // DatabasesController.Attach's policy. The app side (AppsEnv) is asked per app by the service.
    public override string? Capability => Capabilities.DatabasesManage;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var a = Arguments.Of(args);
        var databaseLookup = await resolver.DatabaseAsync(context, a.Database, a.Project, a.Environment, ct);
        var appLookup = await resolver.AppAsync(context, a.App, ct);
        var plannedDatabase = databaseLookup.Found ? null : PlannedDatabase(scope, a.Database, a.Project, a.Environment);
        var plannedApp = appLookup.Found ? null : PlannedApp(scope, a.App);

        if (!databaseLookup.Found && plannedDatabase is null)
            return AgentValidation.Refused(databaseLookup.Error!, databaseLookup.ErrorFa!);
        if (!appLookup.Found && plannedApp is null)
            return AgentValidation.Refused(appLookup.Error!, appLookup.ErrorFa!);

        // Both exist: the service's own validation, exactly as the panel's attach asks it.
        if (databaseLookup.Value is { } service && appLookup.Value is { } app)
        {
            var (databaseId, logicalName, logicalRefusal) = await LogicalAsync(service, a.LogicalDatabase, ct);
            if (logicalRefusal is not null) return logicalRefusal;

            var refusals = await attachments.ValidateAttachAsync(service.Id, app.Id, a.Alias, databaseId, ct);
            if (refusals.Count > 0) return Refused(refusals);

            var taken = await db.AppManagedServices.AsNoTracking().Where(x => x.AppId == app.Id).Select(x => x.Alias).ToListAsync(ct);
            return Card(service.Name, app.Slug, AppManagedServiceAlias.Resolve(a.Alias, service.Name, taken), logicalName, "", "");
        }

        // The database is planned, the app is real: may this person change the app's environment, and will
        // the database land on the app's network?
        if (plannedDatabase is not null && appLookup.Value is { } realApp)
        {
            if (!await access.CanTouchAppAsync(realApp.Id, Capabilities.AppsEnv, ct))
                return Refused([DatabaseRefusals.AppNotFound]);

            if (ApartRefusal(realApp.Slug, AgentResourceResolver.Place(realApp), plannedDatabase.Name,
                    Place(plannedDatabase)) is { } apart)
                return apart;

            var taken = await db.AppManagedServices.AsNoTracking().Where(x => x.AppId == realApp.Id).Select(x => x.Alias).ToListAsync(ct);
            return Card(plannedDatabase.Name, realApp.Slug, AppManagedServiceAlias.Resolve(a.Alias, plannedDatabase.Name, taken),
                a.LogicalDatabase,
                $" Database '{plannedDatabase.Name}' is created by an earlier step of this plan; the attachment is checked in full when this step runs.",
                $" دیتابیس «{plannedDatabase.Name}» در یکی از مراحل قبلی همین برنامه ساخته می‌شود؛ اتصال هنگام اجرای این مرحله کامل بررسی می‌شود.");
        }

        // The app is planned, the database is real: may this person manage the database, is it one that can
        // be attached at all, and will the app land on its network?
        if (databaseLookup.Value is { } realService)
        {
            if (!await access.CanTouchServiceAsync(realService.Id, Capabilities.DatabasesManage, ct))
                return Refused([DatabaseRefusals.ServiceNotFound]);

            if (realService.PrimaryManagedServiceId is not null)
                return AgentValidation.Refused(
                    $"'{realService.Name}' is a read-only replica and cannot be attached to an app on its own. Attach its primary instead — REPLICA_URL becomes available to the app automatically.",
                    $"«{realService.Name}» یک رپلیکای فقط-خواندنی است و مستقیماً به اپی وصل نمی‌شود. دیتابیس اصلی آن را وصل کنید — REPLICA_URL به‌صورت خودکار در دسترس اپ قرار می‌گیرد.");

            var (_, logicalName, logicalRefusal) = await LogicalAsync(realService, a.LogicalDatabase, ct);
            if (logicalRefusal is not null) return logicalRefusal;

            if (ApartRefusal(plannedApp!.Name, Place(plannedApp), realService.Name,
                    AgentResourceResolver.Place(realService)) is { } apart)
                return apart;

            return Card(realService.Name, plannedApp.Name, AppManagedServiceAlias.Resolve(a.Alias, realService.Name, []),
                logicalName, PlannedAppNoteEn(plannedApp.Name), PlannedAppNoteFa(plannedApp.Name));
        }

        // Both planned.
        if (ApartRefusal(plannedApp!.Name, Place(plannedApp), plannedDatabase!.Name, Place(plannedDatabase)) is { } bothApart)
            return bothApart;

        return Card(plannedDatabase.Name, plannedApp.Name, AppManagedServiceAlias.Resolve(a.Alias, plannedDatabase.Name, []),
            a.LogicalDatabase,
            $" Both are created by earlier steps of this plan; the attachment is checked in full when this step runs.",
            $" هر دو در مراحل قبلی همین برنامه ساخته می‌شوند؛ اتصال هنگام اجرای این مرحله کامل بررسی می‌شود.");
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var a = Arguments.Of(args);
        var databaseLookup = await resolver.DatabaseAsync(context, a.Database, a.Project, a.Environment, ct);
        if (!databaseLookup.Found) return AgentToolResult.Failed(databaseLookup.Error!, databaseLookup.ErrorFa!);
        var appLookup = await resolver.AppAsync(context, a.App, ct);
        if (!appLookup.Found) return AgentToolResult.Failed(appLookup.Error!, appLookup.ErrorFa!);
        var service = databaseLookup.Value!;
        var app = appLookup.Value!;

        var (databaseId, _, logicalRefusal) = await LogicalAsync(service, a.LogicalDatabase, ct);
        if (logicalRefusal is not null) return FailedAs(logicalRefusal);

        var outcome = await attachments.AttachAsync(service.Id, app.Id, a.Alias, databaseId, ipAddress: null, ct);
        if (!outcome.Ok) return Failed(outcome.Refusals);
        var attached = outcome.Value!;

        var logicalName = attached.DatabaseId is { } id
            ? await db.ManagedServiceDatabases.AsNoTracking().Where(d => d.Id == id).Select(d => d.Name).FirstOrDefaultAsync(ct)
            : null;

        return new AgentToolResult(true,
            AgentJson.Serialize(new
            {
                database = service.Name,
                app = app.Slug,
                alias = attached.Alias,
                logicalDatabase = logicalName,
                takesEffect = "on the app's next deploy"
            }),
            $"Attached {service.Name} to {app.Slug} as {attached.Alias}_*; it takes effect on {app.Slug}'s next deploy.",
            $"دیتابیس {service.Name} با پیشوند {attached.Alias}_* به اپ {app.Slug} وصل شد؛ با استقرار بعدی {app.Slug} اعمال می‌شود.",
            Links: [DatabaseLink(service.Id, service.Name), AppLink(app.Id, app.Slug)]);
    }

    private sealed record Arguments(
        string? Database, string? App, string? Project, string? Environment, string? Alias, string? LogicalDatabase)
    {
        public static Arguments Of(JsonElement args) => new(
            AgentArgs.String(args, "database"), AgentArgs.String(args, "app"), AgentArgs.String(args, "project"),
            AgentArgs.String(args, "environment"), AgentArgs.String(args, "alias"), AgentArgs.String(args, "logicalDatabase"));
    }

    private static AgentValidation Card(
        string database, string app, string alias, string? logicalDatabase, string plannedEn, string plannedFa) =>
        new(true,
            TitleEn: $"Attach database {database} to {app}",
            TitleFa: $"اتصال دیتابیس {database} به اپ {app}",
            DetailEn: $"{app} gets the connection settings of {database} as variables prefixed {alias}_" +
                      (logicalDatabase is null ? "" : $", pointing at logical database {logicalDatabase}") + $".{plannedEn}",
            DetailFa: $"اپ {app} تنظیمات اتصال {database} را با متغیرهایی با پیشوند {alias}_ می‌گیرد" +
                      (logicalDatabase is null ? "" : $"، متصل به پایگاه‌دادهٔ منطقی {logicalDatabase}") + $".{plannedFa}",
            WarningsEn: [$"It takes effect on {app}'s next deploy; the running container is not changed until then."],
            WarningsFa: [$"با استقرار بعدی {app} اعمال می‌شود؛ کانتینر در حال اجرا تا آن زمان تغییری نمی‌کند."]);

    private static string PlannedAppNoteEn(string app) =>
        $" App '{app}' is created by an earlier step of this plan; the attachment is checked in full when this step runs.";

    private static string PlannedAppNoteFa(string app) =>
        $" اپ «{app}» در یکی از مراحل قبلی همین برنامه ساخته می‌شود؛ اتصال هنگام اجرای این مرحله کامل بررسی می‌شود.";

    /// <summary>"shop/production" for a planned resource, or null when the step that plans it did not say.</summary>
    private static string? Place(AgentPlannedResource planned) =>
        planned.Project is null || planned.Environment is null ? null : $"{planned.Project}/{planned.Environment}";

    /// <summary>
    /// An environment is a private network and so the wiring boundary (<c>NetworkWiring.CanAttach</c>); for
    /// something not created yet that is a comparison of places. Unknown places are left to the service.
    /// </summary>
    private static AgentValidation? ApartRefusal(string app, string? appPlace, string database, string? databasePlace) =>
        appPlace is null || databasePlace is null || Same(appPlace, databasePlace)
            ? null
            : AgentValidation.Refused(
                $"App {app} is in {appPlace}, but database {database} is in {databasePlace}. Each environment is its own private network, so the app could not reach it; attach a database in the app's own environment.",
                $"اپ {app} در {appPlace} است و دیتابیس {database} در {databasePlace}. هر محیط شبکهٔ خصوصی جداگانه‌ای است و اپ به آن دسترسی پیدا نمی‌کند؛ دیتابیسی از محیط خود اپ را وصل کنید.");

    /// <summary>A logical database of this instance by name; with none named, the default one (for the card).</summary>
    private async Task<(Guid? Id, string? Name, AgentValidation? Refusal)> LogicalAsync(
        ManagedService service, string? wanted, CancellationToken ct)
    {
        var logical = await db.ManagedServiceDatabases.AsNoTracking()
            .Where(d => d.ManagedServiceId == service.Id)
            .Select(d => new { d.Id, d.Name, d.IsDefault })
            .ToListAsync(ct);

        if (wanted is null) return (null, logical.FirstOrDefault(d => d.IsDefault)?.Name, null);

        var match = logical.FirstOrDefault(d => Same(d.Name, wanted));
        if (match is not null) return (match.Id, match.Name, null);

        var names = logical.Count == 0 ? "none" : string.Join(", ", logical.Select(d => d.Name));
        var namesFa = logical.Count == 0 ? "هیچ" : string.Join("، ", logical.Select(d => d.Name));
        return (null, null, AgentValidation.Refused(
            $"Database {service.Name} has no logical database '{wanted}'. It has: {names}.",
            $"دیتابیس {service.Name} پایگاه‌دادهٔ منطقی‌ای به نام «{wanted}» ندارد. موجودها: {namesFa}."));
    }
}
