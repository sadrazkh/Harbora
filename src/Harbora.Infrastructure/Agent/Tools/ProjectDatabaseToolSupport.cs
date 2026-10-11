using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Project = Harbora.Domain.Projects.Project;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Agent.Tools;

/// <summary>
/// Where a step means to put something: an environment that exists, or one an earlier step of the same
/// plan will create. <see cref="Environment"/> is null exactly when it is still only planned, and
/// <see cref="Project"/> when the whole project is.
/// </summary>
internal sealed record AgentTargetEnvironment(
    string ProjectSlug, string EnvironmentSlug, Project? Project, ProjectEnvironment? Environment)
{
    public bool ProjectPlanned => Project is null;

    public bool Planned => Environment is null;

    /// <summary>"shop/production", as the snapshot names places.</summary>
    public string Place => $"{ProjectSlug}/{EnvironmentSlug}";
}

/// <summary>
/// What the project and database tools (round six, wave B) share: the panel links of the pages a result
/// points at, finding what an earlier step of the plan will create, the target environment of a create,
/// and the catalogue lookups the model's words have to be checked against. Kept out of the tools so ten of
/// them cannot drift into ten slightly different answers to the same question.
/// </summary>
internal static class ProjectDatabaseToolSupport
{
    // ---- links: the real routes of the pages (DatabasesController, ProjectsController, AppsController) ----

    public static AgentResourceLink DatabaseLink(Guid id, string name) =>
        new(AgentResourceKinds.Database, id, name, $"/databases/{id}");

    public static AgentResourceLink AppLink(Guid id, string slug) =>
        new(AgentResourceKinds.App, id, slug, $"/apps/details/{id}");

    public static AgentResourceLink ProjectLink(Guid id, string label) =>
        new(AgentResourceKinds.Project, id, label, $"/projects/{id}");

    public static AgentResourceLink EnvironmentLink(Guid projectId, Guid environmentId, string label) =>
        new(AgentResourceKinds.Environment, environmentId, label, $"/projects/{projectId}?environmentId={environmentId}");

    // ---- what earlier steps of the plan create ----

    public static bool Same(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether the model's words for a project name the one a planned step creates — by the slug
    /// that step gave it, or by the words it was given before they became a slug ("Shop" for "shop").</summary>
    private static bool Names(string? planned, string? words) =>
        !string.IsNullOrWhiteSpace(words) && (Same(planned, words) || Same(planned, ProjectService.Slugify(words)));

    public static AgentPlannedResource? PlannedProject(AgentPlanScope? scope, string? name) =>
        scope?.Planned.LastOrDefault(p => p.Kind == AgentResourceKinds.Project && Names(p.Name, name));

    public static AgentPlannedResource? PlannedEnvironment(AgentPlanScope? scope, string projectSlug, string? name) =>
        scope?.Planned.LastOrDefault(p => p.Kind == AgentResourceKinds.Environment
                                          && Same(p.Project, projectSlug) && Names(p.Name, name));

    /// <summary>A database an earlier step creates. Project and environment narrow it when given, the way
    /// they narrow a real database's name.</summary>
    public static AgentPlannedResource? PlannedDatabase(AgentPlanScope? scope, string? name, string? project, string? environment) =>
        scope?.Planned.LastOrDefault(p => p.Kind == AgentResourceKinds.Database && Same(p.Name, name)
                                          && (string.IsNullOrWhiteSpace(project) || Names(p.Project, project))
                                          && (string.IsNullOrWhiteSpace(environment) || Names(p.Environment, environment)));

    public static AgentPlannedResource? PlannedApp(AgentPlanScope? scope, string? name) =>
        scope?.Planned.LastOrDefault(p => p.Kind == AgentResourceKinds.App && Names(p.Name, name));

    /// <summary>
    /// The project a step names: a real one the person can see, or one an earlier step of <paramref name="scope"/>
    /// creates. A planned project wins over a real one the words only matched by display name — slugs are the
    /// identifiers, so after "create project shop" in a workspace that already has a project displayed as
    /// "Shop", a later "shop" means the new one, exactly as it will once that step has run and the resolver
    /// finds the slug first.
    /// </summary>
    public static async Task<(Project? Real, AgentPlannedResource? Planned, string? Error, string? ErrorFa)> ProjectAsync(
        AgentResourceResolver resolver, AgentToolContext context, AgentPlanScope? scope, string? words, CancellationToken ct)
    {
        var lookup = await resolver.ProjectAsync(context, words, ct);
        var planned = PlannedProject(scope, words);
        if (planned is not null && (!lookup.Found || !Same(lookup.Value!.Slug, words)))
            return (null, planned, null, null);

        return lookup.Found ? (lookup.Value, null, null, null) : (null, null, lookup.Error, lookup.ErrorFa);
    }

    /// <summary>
    /// The environment a create should land in, named by project and environment. With no environment, the
    /// project's default. A project or environment an earlier step of <paramref name="scope"/> creates counts
    /// as will-exist; pass a null scope (as every <c>ExecuteAsync</c> does) to accept only what is real.
    /// </summary>
    public static async Task<(AgentTargetEnvironment? Target, string? Error, string? ErrorFa)> TargetAsync(
        AgentResourceResolver resolver, AgentToolContext context, AgentPlanScope? scope,
        string? project, string? environment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(project))
            return (null, "Say which project (its slug, as the snapshot shows it).",
                "بگویید کدام پروژه (نامک آن، همان‌طور که در فهرست منابع آمده است).");

        var (owner, plannedProject, ownerError, ownerErrorFa) = await ProjectAsync(resolver, context, scope, project, ct);
        if (owner is not null)
        {
            var place = await resolver.EnvironmentAsync(context, owner.Slug, environment, ct);
            if (place.Found) return (new(owner.Slug, place.Value!.Slug, owner, place.Value), null, null);

            if (!string.IsNullOrWhiteSpace(environment)
                && PlannedEnvironment(scope, owner.Slug, environment) is { } plannedEnvironment)
                return (new(owner.Slug, plannedEnvironment.Name, owner, null), null, null);

            return (null, place.Error, place.ErrorFa);
        }

        if (plannedProject is not null)
        {
            // create_project names the environment it comes with in Environment; "production" otherwise.
            var defaultSlug = plannedProject.Environment ?? ProjectService.DefaultEnvironmentSlug;
            if (string.IsNullOrWhiteSpace(environment) || Names(defaultSlug, environment))
                return (new(plannedProject.Name, defaultSlug, null, null), null, null);

            if (PlannedEnvironment(scope, plannedProject.Name, environment) is { } plannedEnvironment)
                return (new(plannedProject.Name, plannedEnvironment.Name, null, null), null, null);

            return (null,
                $"Project '{plannedProject.Name}' is created by an earlier step with one environment, '{defaultSlug}'; it has no environment '{environment}'.",
                $"پروژهٔ «{plannedProject.Name}» در یکی از مراحل قبلی فقط با محیط «{defaultSlug}» ساخته می‌شود و محیطی به نام «{environment}» ندارد.");
        }

        return (null, ownerError, ownerErrorFa);
    }

    /// <summary>The words a card uses for a step whose project or environment does not exist yet.</summary>
    public static (string En, string Fa) PlannedNote(AgentTargetEnvironment target) => target.ProjectPlanned
        ? ($" Project '{target.ProjectSlug}' is created by an earlier step of this plan, so where this lands and who may create there are checked again when it runs.",
            $" پروژهٔ «{target.ProjectSlug}» در یکی از مراحل قبلی همین برنامه ساخته می‌شود؛ محل و مجوز هنگام اجرا دوباره بررسی می‌شوند.")
        : ($" Environment '{target.Place}' is created by an earlier step of this plan, so where this lands and who may create there are checked again when it runs.",
            $" محیط «{target.Place}» در یکی از مراحل قبلی همین برنامه ساخته می‌شود؛ محل و مجوز هنگام اجرا دوباره بررسی می‌شوند.");

    // ---- the catalogue ----

    /// <summary>
    /// An engine from the effective catalogue (<see cref="ServiceCatalogReader"/>, the list the create form
    /// offers) by the name <c>get_catalog</c> gives it, its display name, or the short names people use.
    /// </summary>
    public static ServiceCatalogEntry? Engine(IReadOnlyList<ServiceCatalogEntry> catalog, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var wanted = text.Trim().ToLowerInvariant().Replace(" ", "");
        wanted = wanted switch
        {
            "postgres" or "pg" or "psql" => "postgresql",
            "mongo" => "mongodb",
            "maria" => "mariadb",
            "rabbit" => "rabbitmq",
            "meili" => "meilisearch",
            _ => wanted
        };
        return catalog.FirstOrDefault(e => e.Type.ToString().ToLowerInvariant() == wanted
                                           || e.DisplayName.ToLowerInvariant().Replace(" ", "") == wanted);
    }

    public static string Engines(IReadOnlyList<ServiceCatalogEntry> catalog) =>
        string.Join(", ", catalog.Select(e => AgentResourceResolver.Lower(e.Type)));

    /// <summary>
    /// An enabled instance size by key. Checked by the tool because the creation path treats a key it cannot
    /// find as "no size" — a database with no ceiling at all — where the form could only ever post one of
    /// the sizes it listed. Whether the plan allows it is the quota's question, asked by the service.
    /// </summary>
    public static async Task<InstanceSize?> SizeAsync(HarboraDbContext db, string key, CancellationToken ct)
    {
        var lowered = key.Trim().ToLowerInvariant();
        return await db.InstanceSizes.AsNoTracking()
            .FirstOrDefaultAsync(s => s.IsEnabled && s.Key.ToLower() == lowered, ct);
    }

    public static async Task<string> SizeKeysAsync(HarboraDbContext db, CancellationToken ct) =>
        string.Join(", ", await db.InstanceSizes.AsNoTracking().Where(s => s.IsEnabled)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.CpuCores).Select(s => s.Key).ToListAsync(ct));

    public static (string En, string Fa) DescribeSize(InstanceSize? size) => size is null
        ? ("no size limit", "بدون سقف منابع")
        : ($"size {size.Key} ({size.CpuCores:0.##} CPU, {size.MemoryBytes / (1024 * 1024)} MB memory)",
            $"اندازهٔ {size.Key} ({size.CpuCores:0.##} پردازنده، {size.MemoryBytes / (1024 * 1024)} مگابایت حافظه)");

    // ---- refusals ----

    public static AgentValidation QuotaRefused(QuotaCheck check) => AgentValidation.Refused(
        check.Reason ?? "Plan quota exceeded.", check.ReasonFa ?? check.Reason ?? "سهمیهٔ پلن پر شده است.");

    public static AgentToolResult QuotaFailed(QuotaRefusedException refused) =>
        AgentToolResult.Failed(refused.Message, refused.ReasonFa ?? refused.Message);

    public static AgentToolResult FailedAs(AgentValidation refused) =>
        AgentToolResult.Failed(refused.Error ?? "Refused.", refused.ErrorFa ?? "پذیرفته نشد.");

    /// <summary>
    /// A database by its <c>database</c>/<c>project</c>/<c>environment</c> arguments that this person may also
    /// manage — the <c>Guard</c> every <c>DatabasesController</c> action runs. Seeing it is not enough; one they
    /// may see but not manage is refused in the words a missing one gets.
    /// </summary>
    public static async Task<(Domain.Services.ManagedService? Service, AgentValidation? Refusal)> ManagedDatabaseAsync(
        AgentResourceResolver resolver, ProjectAccessService access, AgentToolContext context,
        System.Text.Json.JsonElement args, CancellationToken ct)
    {
        var lookup = await resolver.DatabaseAsync(context,
            AgentArgs.String(args, "database"), AgentArgs.String(args, "project"), AgentArgs.String(args, "environment"), ct);
        if (!lookup.Found) return (null, AgentValidation.Refused(lookup.Error!, lookup.ErrorFa!));

        if (!await access.CanTouchServiceAsync(lookup.Value!.Id, Domain.Authorization.Capabilities.DatabasesManage, ct))
            return (null, AgentValidation.Refused(DatabaseRefusals.ServiceNotFound.Message, DatabaseRefusals.ServiceNotFound.MessageFa));

        return (lookup.Value, null);
    }

    /// <summary>
    /// Runs one engine call under its own deadline. A plan runs after its claim with no cancellation of its own
    /// (a half-run plan abandoned because a browser closed is the worst outcome), so a node that never answers
    /// would otherwise hold the request for ever. False when the deadline, not the caller, cancelled it.
    /// </summary>
    public static async Task<bool> WithinAsync(TimeSpan deadline, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        using var timed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timed.CancelAfter(deadline);
        try
        {
            await work(timed.Token);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>The replicas streaming from a database, by name, that this person can see.</summary>
    public static async Task<IReadOnlyList<string>> VisibleReplicasAsync(
        HarboraDbContext db, ProjectAccessService access, Guid serviceId, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var replica in await db.ManagedServices.AsNoTracking()
                     .Where(r => r.PrimaryManagedServiceId == serviceId).OrderBy(r => r.Name)
                     .Select(r => new { r.Id, r.Name }).ToListAsync(ct))
            if (await access.CanSeeServiceAsync(replica.Id, ct)) names.Add(replica.Name);
        return names;
    }

    /// <summary>"web, api" — the apps using a database that this person can see. One they cannot see is
    /// left out rather than counted: a number would tell them it exists.</summary>
    public static async Task<IReadOnlyList<string>> VisibleUsersAsync(
        ServiceUsageService usage, ProjectAccessService access, Guid serviceId, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var app in await usage.AppsUsingAsync(serviceId, ct))
            if (await access.CanSeeAppAsync(app.Id, ct)) names.Add(app.Slug);
        names.Sort(StringComparer.Ordinal);
        return names;
    }
}
