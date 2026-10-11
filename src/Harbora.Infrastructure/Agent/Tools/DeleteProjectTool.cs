using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>delete_project</c> — a project and everything in it, through <see cref="ProjectDeletionService"/>, the
/// service <c>ProjectsController.Delete</c> calls once the project's name has been typed back. The phrase is
/// the project's name exactly as <see cref="ProjectRemovalPlan.IsConfirmed"/> compares it, and the card names
/// every app and database that goes with it. The <c>project.deleted</c> audit entry the controller writes is
/// written here too, so a delete through the assistant leaves the same trace.
///
/// <para>
/// One refusal the panel does not make: a project holding an app with a protected volume. The deletion
/// service skips such an app and deletes the rest, so the project would end half-deleted — every other app and
/// database and their data gone, the project still there. The panel lets that happen and then reports what is
/// left; the assistant refuses up front and says which volume to unprotect.
/// </para>
/// </summary>
public sealed class DeleteProjectTool(
    ProjectDeletionService deletion,
    ProjectAccessService access,
    AgentResourceResolver resolver,
    IAuditLogger audit) : AgentWriteTool
{
    public override string Name => "delete_project";

    public override string Description =>
        "Deletes a project and everything in it — every app and database in every environment, with their data, " +
        "volumes and domains. The user must confirm by typing the project's name.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "project": { "type": "string", "description": "The project's slug." }
          },
          "required": ["project"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.Destructive;

    // ProjectsController.Delete's policy — the delete capability, not the create one the rest of that
    // controller uses.
    public override string? Capability => Capabilities.AppsDelete;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (found, refusal) = await PlanAsync(args, context, ct);
        if (refusal is not null) return refusal;
        var (project, plan) = found!.Value;

        var phrase = plan.ProjectName.Trim();
        var environments = string.Join(", ", project.Environments.OrderByDescending(e => e.IsDefault).ThenBy(e => e.Slug).Select(e => e.Slug));

        if (plan.IsEmpty)
            return new AgentValidation(true,
                TitleEn: $"Delete project {plan.ProjectName}",
                TitleFa: $"حذف پروژهٔ {plan.ProjectName}",
                DetailEn: $"Project {plan.ProjectName} ({project.Slug}) holds no apps or databases; only its environments ({environments}) are removed.",
                DetailFa: $"پروژهٔ {plan.ProjectName} ({project.Slug}) هیچ اپ یا دیتابیسی ندارد؛ فقط محیط‌هایش ({environments}) حذف می‌شوند.",
                ConfirmationPhrase: phrase);

        var apps = string.Join(", ", plan.Apps.Select(a =>
            $"{a.Name} [{a.EnvironmentName}{(a.VolumeCount > 0 ? $", {a.VolumeCount} volume{(a.VolumeCount == 1 ? "" : "s")}" : "")}]"));
        var appsFa = string.Join("، ", plan.Apps.Select(a =>
            $"{a.Name} [{a.EnvironmentName}{(a.VolumeCount > 0 ? $"، {a.VolumeCount} والیوم" : "")}]"));
        var databases = string.Join(", ", plan.Databases.Select(d => $"{d.Name} [{d.EnvironmentName}]"));
        var databasesFa = string.Join("، ", plan.Databases.Select(d => $"{d.Name} [{d.EnvironmentName}]"));

        var detailEn = $"Everything in project {plan.ProjectName} ({project.Slug}) is deleted, in every environment ({environments}).";
        var detailFa = $"همه‌چیز در پروژهٔ {plan.ProjectName} ({project.Slug})، در همهٔ محیط‌ها ({environments})، حذف می‌شود.";
        if (plan.Apps.Count > 0) { detailEn += $" Apps ({plan.Apps.Count}): {apps}."; detailFa += $" اپ‌ها ({plan.Apps.Count}): {appsFa}."; }
        if (plan.Databases.Count > 0) { detailEn += $" Databases ({plan.Databases.Count}): {databases}."; detailFa += $" دیتابیس‌ها ({plan.Databases.Count}): {databasesFa}."; }
        if (plan.ScheduledFunctionCount > 0)
        {
            detailEn += $" Scheduled functions: {plan.ScheduledFunctionCount}.";
            detailFa += $" تابع‌های زمان‌بندی‌شده: {plan.ScheduledFunctionCount}.";
        }

        var warningsEn = new List<string>
        {
            $"Deletes {plan.Apps.Count} app(s) and {plan.Databases.Count} database(s) with all their data: app volumes and database data volumes are deleted, not kept. This cannot be undone."
        };
        var warningsFa = new List<string>
        {
            $"{plan.Apps.Count} اپ و {plan.Databases.Count} دیتابیس با همهٔ داده‌هایشان حذف می‌شوند: والیوم‌های اپ‌ها و داده‌های دیتابیس‌ها پاک می‌شوند، نه نگه‌داری. برگشت‌پذیر نیست."
        };
        if (plan.DomainHosts.Count > 0)
        {
            warningsEn.Add($"These domains stop serving: {string.Join(", ", plan.DomainHosts)}.");
            warningsFa.Add($"این دامنه‌ها دیگر پاسخ نمی‌دهند: {string.Join("، ", plan.DomainHosts)}.");
        }

        return new AgentValidation(true,
            TitleEn: $"Delete project {plan.ProjectName}",
            TitleFa: $"حذف پروژهٔ {plan.ProjectName}",
            DetailEn: detailEn,
            DetailFa: detailFa,
            WarningsEn: warningsEn,
            WarningsFa: warningsFa,
            ConfirmationPhrase: phrase);
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (found, refusal) = await PlanAsync(args, context, ct);
        if (refusal is not null) return AgentToolResult.Failed(refusal.Error!, refusal.ErrorFa!);
        var (project, plan) = found!.Value;

        var outcome = await deletion.DeleteAsync(context.WorkspaceId, project.Id, ct);
        if (!outcome.FullyDeleted)
        {
            var left = string.Join(", ", outcome.RemainingApps.Concat(outcome.RemainingDatabases));
            var leftFa = string.Join("، ", outcome.RemainingApps.Concat(outcome.RemainingDatabases));
            return new AgentToolResult(false,
                AgentJson.Serialize(new
                {
                    project = project.Slug,
                    fullyDeleted = false,
                    remainingApps = outcome.RemainingApps,
                    remainingDatabases = outcome.RemainingDatabases
                }),
                Error: $"The project could not be fully deleted: {left} are still there. Everything else in it was removed.",
                ErrorFa: $"پروژه به‌طور کامل حذف نشد: {leftFa} هنوز باقی مانده‌اند. بقیهٔ محتوای آن حذف شد.",
                Links: [ProjectDatabaseToolSupport.ProjectLink(project.Id, project.Slug)]);
        }

        await audit.LogAsync("project.deleted", "project", project.Id.ToString(), ipAddress: null,
            workspaceId: context.WorkspaceId,
            metadataJson: JsonSerializer.Serialize(new { apps = plan.Apps.Count, databases = plan.Databases.Count }),
            ct: ct);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { project = project.Slug, deleted = true, apps = plan.Apps.Count, databases = plan.Databases.Count }),
            $"Deleted project {plan.ProjectName} with {plan.Apps.Count} app(s) and {plan.Databases.Count} database(s).",
            $"پروژهٔ {plan.ProjectName} همراه با {plan.Apps.Count} اپ و {plan.Databases.Count} دیتابیس حذف شد.");
    }

    private async Task<((Domain.Projects.Project Project, ProjectRemovalPlan Plan)? Found, AgentValidation? Refusal)> PlanAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var lookup = await resolver.ProjectAsync(context, AgentArgs.String(args, "project"), ct);
        if (!lookup.Found) return (null, AgentValidation.Refused(lookup.Error!, lookup.ErrorFa!));
        var project = lookup.Value!;

        if (!await access.AllowsAsync(new ResourcePlacement(project.Id, null), Capabilities.AppsDelete, ct))
            return (null, AgentValidation.Refused(
                $"You are not allowed to delete project '{project.Slug}'.",
                $"اجازهٔ حذف پروژهٔ «{project.Slug}» را ندارید."));

        if (await deletion.PlanAsync(context.WorkspaceId, project.Id, ct) is not { } plan)
            return (null, AgentValidation.Refused(
                $"No project named '{project.Slug}' that you can see.",
                $"پروژه‌ای به نام «{project.Slug}» که به آن دسترسی داشته باشید پیدا نشد."));

        var guarded = plan.Apps.Where(a => a.HasProtectedVolume).Select(a => a.Name).ToList();
        if (guarded.Count > 0)
            return (null, AgentValidation.Refused(
                $"{string.Join(", ", guarded)} in this project has a protected volume, so deleting the project would stop part-way and leave it half-deleted. Unprotect the volume first, or delete the rest one by one.",
                $"{string.Join("، ", guarded)} در این پروژه والیوم محافظت‌شده دارد و حذف پروژه نیمه‌کاره می‌ماند. اول محافظت والیوم را بردارید، یا بقیه را یکی‌یکی حذف کنید."));

        // The panel's own rule: the typed text must satisfy the plan's own comparison.
        if (!plan.IsConfirmed(plan.ProjectName.Trim()))
            return (null, AgentValidation.Refused("This project cannot be confirmed by name.", "این پروژه با نام قابل تأیید نیست."));

        return ((project, plan), null);
    }
}
