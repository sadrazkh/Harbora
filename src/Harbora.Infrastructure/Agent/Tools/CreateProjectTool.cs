using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using static Harbora.Infrastructure.Agent.Tools.ProjectDatabaseToolSupport;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// <c>create_project</c> — a new project with its first environment, through
/// <see cref="ProjectService.CreateAsync"/>, the call <c>ProjectsController.Create</c> makes.
///
/// <para>
/// One deliberate difference from the form: a slug that is already taken is refused rather than quietly
/// suffixed ("shop" becoming "shop-2"). Later steps of the same plan name the project by the slug this step
/// promised, so a project that landed under another slug would send them into whichever project already
/// had it.
/// </para>
/// </summary>
public sealed class CreateProjectTool(
    HarboraDbContext db, ProjectService projects, ProjectAccessService access, IQuotaService quota) : AgentWriteTool
{
    public override string Name => "create_project";

    public override string Description =>
        "Creates a new project with one environment, 'production'. Give a display name and a slug (lowercase Latin " +
        "letters, digits and hyphens); later steps and other tools name the project by that slug. Does nothing by " +
        "itself: it becomes a step of a plan the user approves.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "name": { "type": "string", "description": "The project's display name; may be Persian." },
            "slug": { "type": "string", "description": "Lowercase Latin letters, digits and hyphens. Omit to derive it from a Latin name; required when the name is Persian." }
          },
          "required": ["name"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsCreate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (request, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return refusal;
        var (name, slug) = request!.Value;

        if (PlannedProject(scope, slug) is not null)
            return AgentValidation.Refused(
                $"An earlier step of this plan already creates project '{slug}'.",
                $"یکی از مراحل قبلی همین برنامه پروژهٔ «{slug}» را می‌سازد.");

        return new AgentValidation(true,
            TitleEn: $"Create project {name}",
            TitleFa: $"ساخت پروژهٔ {name}",
            DetailEn: $"Slug '{slug}', with one environment, '{ProjectService.DefaultEnvironmentSlug}' (Production). Apps and databases are added to it in later steps.",
            DetailFa: $"نامک «{slug}»، با یک محیط به نام «{ProjectService.DefaultEnvironmentSlug}» (Production). اپ‌ها و دیتابیس‌ها در مراحل بعد به آن افزوده می‌شوند.",
            // One planned resource per step: the project, carrying the environment it comes with, so a later
            // step that names "production" (or no environment) in this project is accepted as will-exist.
            Creates: new AgentPlannedResource(AgentResourceKinds.Project, slug,
                Environment: ProjectService.DefaultEnvironmentSlug));
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (request, refusal) = await CheckAsync(args, context, ct);
        if (refusal is not null) return FailedAs(refusal);
        var (name, slug) = request!.Value;

        Domain.Projects.Project project;
        Domain.Projects.Environment environment;
        try
        {
            (project, environment) = await projects.CreateAsync(context.WorkspaceId, name, slug, ct);
        }
        catch (QuotaRefusedException ex)
        {
            return QuotaFailed(ex);
        }

        var links = new[] { ProjectLink(project.Id, project.Slug) };

        // Taken between the check above and the save: the service suffixed it. The project exists, but not
        // under the slug the later steps name, so this step must fail and stop them.
        if (project.Slug != slug)
            return new AgentToolResult(false,
                AgentJson.Serialize(new { project = project.Slug, requested = slug, created = true }),
                Error: $"Project '{name}' was created as '{project.Slug}', because '{slug}' was taken in the meantime. The steps after this one name '{slug}', so they were not run.",
                ErrorFa: $"پروژهٔ «{name}» با نامک «{project.Slug}» ساخته شد، چون «{slug}» در همین فاصله گرفته شده بود. مراحل بعدی «{slug}» را نام می‌برند و به همین دلیل اجرا نشدند.",
                Links: links);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { project = project.Slug, name = project.Name, environment = environment.Slug, status = "created" }),
            $"Created project {project.Name} ({project.Slug}) with environment {environment.Slug}.",
            $"پروژهٔ {project.Name} ({project.Slug}) با محیط {environment.Slug} ساخته شد.",
            Links: links);
    }

    /// <summary>Everything both halves check: who may, the name and slug, the slug still free, the quota.</summary>
    private async Task<((string Name, string Slug)? Request, AgentValidation? Refusal)> CheckAsync(
        JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        // ProjectsController.Create: a project that does not exist yet has no placement any grant could name,
        // so only someone unscoped may make one.
        if (!await access.AllowsAsync(new ResourcePlacement(null, null), Capabilities.AppsCreate, ct))
            return (null, AgentValidation.Refused(
                "You are not allowed to create projects in this workspace.",
                "اجازهٔ ساختن پروژه در این فضای کاری را ندارید."));

        var name = AgentArgs.String(args, "name");
        if (name is null)
            return (null, AgentValidation.Refused("A project needs a name.", "پروژه به نام نیاز دارد."));

        var slug = ProjectService.Slugify(AgentArgs.String(args, "slug") ?? name);
        if (slug.Length == 0)
            return (null, AgentValidation.Refused(
                "Give the project a slug in lowercase Latin letters, digits and hyphens (a Persian name is fine as the display name; transliterate it for the slug).",
                "برای پروژه نامکی با حروف کوچک لاتین، رقم و خط تیره بدهید (نام فارسی برای نمایش اشکالی ندارد؛ برای نامک آن را به حروف لاتین بنویسید)."));

        // Whoever got past the check above sees every project, so saying this one exists tells them nothing new.
        if (await db.Projects.AsNoTracking().AnyAsync(p => p.WorkspaceId == context.WorkspaceId && p.Slug == slug, ct))
            return (null, AgentValidation.Refused(
                $"A project with the slug '{slug}' already exists. Use it, or choose another slug.",
                $"پروژه‌ای با نامک «{slug}» از قبل وجود دارد. از همان استفاده کنید یا نامک دیگری انتخاب کنید."));

        var check = await quota.CanAddGovernedResourcesAsync(context.WorkspaceId,
            new GovernanceQuotaDelta(Projects: 1, Environments: 1), ct);
        if (!check.Allowed) return (null, QuotaRefused(check));

        return ((name, slug), null);
    }
}
