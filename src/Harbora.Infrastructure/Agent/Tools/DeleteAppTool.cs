using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>delete_app</c> — deletes an app through <see cref="IAppOperationsService.DeleteAsync"/>, the
/// choke point the panel's delete, the preview sweeper and project deletion all go through, with
/// <c>AppsController.Delete</c>'s capability (AppsDelete) and its <c>app.delete</c> audit entry.
///
/// <para>
/// The typed phrase is the app's slug. The card lists what goes with it — domains, volumes and whether
/// their data is deleted — and the attached databases, which stay: they are only detached, and saying
/// so prevents both a wrong fear and a wrong assumption. A Protected volume refuses
/// <c>removeVolumes</c> (HARBORA-0033) at the card already, and the service refuses it again. Success is
/// reported only once the app row is really gone: <c>DeleteAsync</c> returns quietly for an app it
/// cannot find.
/// </para>
/// </summary>
public sealed class DeleteAppTool(
    HarboraDbContext db,
    AgentResourceResolver resolver,
    ProjectAccessService access,
    IAppOperationsService operations,
    IAuditLogger audit) : AgentAppTool(resolver, access)
{
    public override string Name => "delete_app";

    public override string Description =>
        "Deletes an app: its container, deployment history, environment variables, domains and volume mounts. Volume " +
        "data is kept on the server unless removeVolumes is true (refused when a volume is Protected). Attached databases " +
        "are not deleted, only detached. The user must type the app's slug to approve.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "app": { "type": "string", "description": "The app's slug." },
            "removeVolumes": { "type": "boolean", "description": "Also delete the data in the app's volumes. Default false." }
          },
          "required": ["app"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.Destructive;

    public override string? Capability => Capabilities.AppsDelete;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var removeVolumes = AgentArgs.Bool(args, "removeVolumes") ?? false;
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDelete, scope: null, ct);
        if (target.Refused) return AgentValidation.Refused(target.Error!, target.ErrorFa!);
        var app = target.App!;

        var volumes = await db.Volumes.AsNoTracking().Where(v => v.AppId == app.Id).OrderBy(v => v.MountPath).ToListAsync(ct);
        if (removeVolumes && Protection(volumes) is { } protectedVolumes)
            return AgentValidation.Refused(protectedVolumes.Message, protectedVolumes.ReasonFa);

        var domains = await db.Domains.AsNoTracking().Where(d => d.AppId == app.Id)
            .OrderByDescending(d => d.IsPrimary).ThenBy(d => d.Host).Select(d => d.Host).ToListAsync(ct);
        var databases = await db.AppManagedServices.AsNoTracking()
            .Where(x => x.AppId == app.Id && x.ManagedService!.WorkspaceId == context.WorkspaceId)
            .Select(x => x.ManagedService!.Name).OrderBy(n => n).ToListAsync(ct);

        var goesEn = new List<string> { "its container", "its deployment history", "its environment variables" };
        var goesFa = new List<string> { "کانتینرش", "تاریخچهٔ استقرارهایش", "متغیرهای محیطی‌اش" };
        if (domains.Count > 0) { goesEn.Add($"domains {string.Join(", ", domains)}"); goesFa.Add($"دامنه‌های {string.Join("، ", domains)}"); }
        if (volumes.Count > 0)
        {
            var paths = volumes.Select(v => v.MountPath).ToList();
            goesEn.Add($"volumes {string.Join(", ", paths)}");
            goesFa.Add($"والیوم‌های {string.Join("، ", paths)}");
        }

        var detailEn = $"Deletes {app.Slug} in {AgentResourceResolver.Place(app)}: {string.Join(", ", goesEn)}.";
        var detailFa = $"{app.Slug} در {AgentResourceResolver.Place(app)} حذف می‌شود: {string.Join("، ", goesFa)}.";
        if (volumes.Count > 0)
        {
            detailEn += removeVolumes ? " The volumes' data is deleted from the server." : " The volumes' data stays on the server.";
            detailFa += removeVolumes ? " داده‌های والیوم‌ها از سرور حذف می‌شود." : " داده‌های والیوم‌ها روی سرور می‌ماند.";
        }
        if (databases.Count > 0)
        {
            detailEn += $" Attached databases are not deleted, only detached: {string.Join(", ", databases)}.";
            detailFa += $" دیتابیس‌های وصل‌شده حذف نمی‌شوند و فقط جدا می‌شوند: {string.Join("، ", databases)}.";
        }

        var warningsEn = new List<string> { "This cannot be undone." };
        var warningsFa = new List<string> { "این کار برگشت‌پذیر نیست." };
        if (removeVolumes && volumes.Count > 0)
        {
            warningsEn.Add("The data in its volumes is deleted for good.");
            warningsFa.Add("داده‌های والیوم‌هایش برای همیشه پاک می‌شود.");
        }
        if (domains.Count > 0)
        {
            warningsEn.Add($"Its addresses stop answering: {string.Join(", ", domains)}.");
            warningsFa.Add($"نشانی‌هایش دیگر پاسخ نمی‌دهند: {string.Join("، ", domains)}.");
        }

        return new AgentValidation(true,
            TitleEn: $"Delete app {app.Slug}",
            TitleFa: $"حذف اپ {app.Slug}",
            DetailEn: detailEn,
            DetailFa: detailFa,
            WarningsEn: warningsEn,
            WarningsFa: warningsFa,
            ConfirmationPhrase: app.Slug);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var removeVolumes = AgentArgs.Bool(args, "removeVolumes") ?? false;
        var target = await TargetAsync(context, AgentArgs.String(args, "app"), Capabilities.AppsDelete, scope: null, ct);
        if (target.Refused) return NotPossible(target.Error!, target.ErrorFa!);
        var app = target.App!;

        // Read before the row goes: everything after DeleteAsync describes an app that no longer exists.
        var (id, slug, place) = (app.Id, app.Slug, AgentResourceResolver.Place(app));
        var project = app.Environment?.Project;

        // Asked again here, not trusted from the card: a volume can be made Protected in between.
        if (removeVolumes
            && Protection(await db.Volumes.AsNoTracking().Where(v => v.AppId == id).ToListAsync(ct)) is { } protectedVolumes)
            return NotPossible(protectedVolumes.Message, protectedVolumes.ReasonFa);

        try
        {
            await operations.DeleteAsync(id, removeVolumes, ct);
        }
        catch (VolumeProtectedException ex)
        {
            // Refused before anything was touched — the app, its container and its volumes are as they were.
            return NotPossible(ex.Message, ex.ReasonFa);
        }

        if (await db.Apps.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.Id == id, ct))
            return NotPossible(
                $"App '{slug}' is still there: the delete did not remove it. Nothing is reported as deleted.",
                $"اپ «{slug}» هنوز هست: حذف آن را پاک نکرد. چیزی حذف‌شده گزارش نمی‌شود.");

        await audit.LogAsync("app.delete", "app", id.ToString(),
            metadataJson: JsonSerializer.Serialize(new { removeVolumes }),
            workspaceId: context.WorkspaceId, ct: ct);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { deleted = "app", app = slug, place, volumeDataDeleted = removeVolumes }),
            removeVolumes
                ? $"Deleted app '{slug}' from {place}, with its volume data."
                : $"Deleted app '{slug}' from {place}. Its volume data, if any, is still on the server.",
            removeVolumes
                ? $"اپ «{slug}» و داده‌های والیوم‌هایش از {place} حذف شدند."
                : $"اپ «{slug}» از {place} حذف شد. داده‌های والیوم‌هایش، اگر داشت، روی سرور ماندند.",
            Links: project is null ? null : [ProjectLink(project.Id, project.Slug)]);
    }

    /// <summary>HARBORA-0033's own guard, asked without deleting anything.</summary>
    private static VolumeProtectedException? Protection(IReadOnlyList<Volume> volumes)
    {
        try
        {
            VolumeProtection.GuardAgainstDestroying(volumes);
            return null;
        }
        catch (VolumeProtectedException ex)
        {
            return ex;
        }
    }
}
