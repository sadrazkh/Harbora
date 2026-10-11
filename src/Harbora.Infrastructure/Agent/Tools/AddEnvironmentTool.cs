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
/// <c>add_environment</c> — another environment in a project, through
/// <see cref="ProjectService.AddEnvironmentAsync"/>, the call <c>ProjectsController.AddEnvironment</c> makes.
/// The project may be one an earlier step creates. As with projects, a slug already taken in the project is
/// refused rather than suffixed ("staging-2"), because later steps name the environment by the slug this
/// step promised.
/// </summary>
public sealed class AddEnvironmentTool(
    HarboraDbContext db, ProjectService projects, ProjectAccessService access, IQuotaService quota,
    AgentResourceResolver resolver) : AgentWriteTool
{
    public override string Name => "add_environment";

    public override string Description =>
        "Adds another environment (for example staging) to a project. The environment's slug is made from its name, " +
        "so give the name in Latin letters. Each environment is its own private network: an app reaches only the " +
        "databases in its own environment. The project may be one an earlier step of the same plan creates.";

    public override string ParametersSchema =>
        """
        {
          "type": "object",
          "properties": {
            "project": { "type": "string", "description": "The project's slug." },
            "name": { "type": "string", "description": "The environment's name in Latin letters, for example 'staging'; its slug is made from it." }
          },
          "required": ["project", "name"],
          "additionalProperties": false
        }
        """;

    public override AgentToolKind Kind => AgentToolKind.SafeWrite;

    public override string? Capability => Capabilities.AppsCreate;

    public override async Task<AgentValidation> ValidateAsync(
        JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct)
    {
        var (request, refusal) = await CheckAsync(args, context, scope, ct);
        if (refusal is not null) return refusal;
        var (projectSlug, project, name, slug) = request!.Value;

        if (PlannedEnvironment(scope, projectSlug, slug) is not null)
            return AgentValidation.Refused(
                $"An earlier step of this plan already adds environment '{slug}' to project '{projectSlug}'.",
                $"یکی از مراحل قبلی همین برنامه محیط «{slug}» را به پروژهٔ «{projectSlug}» اضافه می‌کند.");

        var plannedEn = project is null
            ? $" Project '{projectSlug}' is created by an earlier step of this plan; it is checked again when this step runs."
            : "";
        var plannedFa = project is null
            ? $" پروژهٔ «{projectSlug}» در یکی از مراحل قبلی همین برنامه ساخته می‌شود و هنگام اجرای این مرحله دوباره بررسی می‌شود."
            : "";

        return new AgentValidation(true,
            TitleEn: $"Add environment {slug} to {projectSlug}",
            TitleFa: $"افزودن محیط {slug} به پروژهٔ {projectSlug}",
            DetailEn: $"A new environment '{name}' (slug '{slug}') with its own private network: apps and databases in the project's other environments cannot reach it by name. It does not become the project's default.{plannedEn}",
            DetailFa: $"محیط تازهٔ «{name}» (نامک «{slug}») با شبکهٔ خصوصی خودش: اپ‌ها و دیتابیس‌های محیط‌های دیگر پروژه با نام به آن دسترسی ندارند. محیط پیش‌فرض پروژه نمی‌شود.{plannedFa}",
            Creates: new AgentPlannedResource(AgentResourceKinds.Environment, slug, Project: projectSlug));
    }

    public override async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
    {
        var (request, refusal) = await CheckAsync(args, context, scope: null, ct);
        if (refusal is not null) return FailedAs(refusal);
        var (projectSlug, project, name, slug) = request!.Value;

        Domain.Projects.Environment environment;
        try
        {
            environment = await projects.AddEnvironmentAsync(context.WorkspaceId, project!.Id, name, ct);
        }
        catch (QuotaRefusedException ex)
        {
            return QuotaFailed(ex);
        }

        var links = new[] { EnvironmentLink(project.Id, environment.Id, $"{projectSlug}/{environment.Slug}") };

        if (environment.Slug != slug)
            return new AgentToolResult(false,
                AgentJson.Serialize(new { project = projectSlug, environment = environment.Slug, requested = slug, created = true }),
                Error: $"The environment was added as '{environment.Slug}', because '{slug}' was taken in the meantime. The steps after this one name '{slug}', so they were not run.",
                ErrorFa: $"محیط با نامک «{environment.Slug}» اضافه شد، چون «{slug}» در همین فاصله گرفته شده بود. مراحل بعدی «{slug}» را نام می‌برند و به همین دلیل اجرا نشدند.",
                Links: links);

        return new AgentToolResult(true,
            AgentJson.Serialize(new { project = projectSlug, environment = environment.Slug, name = environment.Name, status = "created" }),
            $"Added environment {environment.Slug} to project {projectSlug}.",
            $"محیط {environment.Slug} به پروژهٔ {projectSlug} اضافه شد.",
            Links: links);
    }

    /// <summary>
    /// The checks both halves make. With a <paramref name="scope"/>, a project an earlier step creates is
    /// accepted (and <c>Project</c> is null); without one, only a real project is.
    /// </summary>
    private async Task<((string ProjectSlug, Domain.Projects.Project? Project, string Name, string Slug)? Request, AgentValidation? Refusal)>
        CheckAsync(JsonElement args, AgentToolContext context, AgentPlanScope? scope, CancellationToken ct)
    {
        var name = AgentArgs.String(args, "name");
        if (name is null)
            return (null, AgentValidation.Refused("Say what the environment is called (for example 'staging').",
                "بگویید نام محیط چیست (مثلاً «staging»)."));

        var slug = ProjectService.Slugify(name);
        if (slug.Length == 0)
            return (null, AgentValidation.Refused(
                "Give the environment a name in Latin letters; its slug is made from it (for example 'staging').",
                "نام محیط را با حروف لاتین بدهید؛ نامک آن از همین نام ساخته می‌شود (مثلاً «staging»)."));

        var (project, planned, error, errorFa) =
            await ProjectAsync(resolver, context, scope, AgentArgs.String(args, "project"), ct);
        string projectSlug;
        ResourcePlacement placement;

        if (project is not null)
        {
            projectSlug = project.Slug;
            placement = new ResourcePlacement(project.Id, null);
        }
        else if (planned is not null)
        {
            projectSlug = planned.Name;
            // Whoever may create the project may shape it; asked the way create_project asks.
            placement = new ResourcePlacement(null, null);

            if (Same(slug, planned.Environment ?? ProjectService.DefaultEnvironmentSlug))
                return (null, AgentValidation.Refused(
                    $"Project '{projectSlug}' already comes with an environment '{slug}'.",
                    $"پروژهٔ «{projectSlug}» از قبل محیطی به نام «{slug}» دارد."));
        }
        else
        {
            return (null, AgentValidation.Refused(error!, errorFa!));
        }

        // ProjectsController.AddEnvironment: a member scoped to other projects may not add one here.
        if (!await access.AllowsAsync(placement, Capabilities.AppsCreate, ct))
            return (null, AgentValidation.Refused(
                $"You are not allowed to add environments to project '{projectSlug}'.",
                $"اجازهٔ افزودن محیط به پروژهٔ «{projectSlug}» را ندارید."));

        if (project is not null && await db.Environments.AsNoTracking()
                .AnyAsync(e => e.ProjectId == project.Id && e.Slug == slug, ct))
            return (null, AgentValidation.Refused(
                $"Project '{projectSlug}' already has an environment '{slug}'.",
                $"پروژهٔ «{projectSlug}» از قبل محیطی به نام «{slug}» دارد."));

        var check = await quota.CanAddGovernedResourcesAsync(context.WorkspaceId, new GovernanceQuotaDelta(Environments: 1), ct);
        if (!check.Allowed) return (null, QuotaRefused(check));

        return ((projectSlug, project, name, slug), null);
    }
}
