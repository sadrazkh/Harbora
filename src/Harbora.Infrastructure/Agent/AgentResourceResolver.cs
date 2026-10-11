using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using ProjectEnvironment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Infrastructure.Agent;

/// <summary>A name resolved to a row, or why it could not be — in both languages, ready for the model
/// (<see cref="Error"/>) and the person (<see cref="ErrorFa"/>).</summary>
public sealed record AgentLookup<T>(T? Value, string? Error, string? ErrorFa) where T : class
{
    public bool Found => Value is not null;

    public static AgentLookup<T> Of(T value) => new(value, null, null);

    public static AgentLookup<T> Missing(string error, string errorFa) => new(null, error, errorFa);
}

/// <summary>What one person can see of a workspace, as a tree — the snapshot and <c>list_resources</c>
/// both read it, so the model is never told about something a tool would then call "not found".</summary>
/// <param name="Omitted">Resources (apps and databases) left out because of the cap.</param>
public sealed record AgentResourceTree(IReadOnlyList<AgentProjectNode> Projects, int Omitted);

public sealed record AgentProjectNode(string Slug, string Name, IReadOnlyList<AgentEnvironmentNode> Environments);

public sealed record AgentEnvironmentNode(
    string Slug, string Name, bool Protected,
    IReadOnlyList<AgentAppNode> Apps, IReadOnlyList<AgentDatabaseNode> Databases);

public sealed record AgentAppNode(string Slug, string Name, string Status, string Kind);

public sealed record AgentDatabaseNode(string Name, string Engine, string Version, string Status);

/// <summary>
/// Turns the names a model uses into rows the person may see — the one place every agent tool resolves
/// "shop" or "shop-db", so that "invisible" and "not there" are the same answer everywhere.
///
/// <para>
/// Reads go through the request's workspace filter <i>and</i> pin the workspace explicitly, and
/// visibility is <see cref="ProjectAccessService"/>'s own <c>CanSee…</c> rule — the same one the list
/// pages use. A resource in a project this person is not granted is reported exactly like a resource
/// that does not exist: saying which of the two it is would tell them it exists.
/// </para>
///
/// <para>
/// <see cref="ProjectAccessService"/> judges the request's <c>ICurrentUser</c>. Tools run inside that
/// same request, so it is the person in <see cref="AgentToolContext"/>; a caller constructing this by
/// hand (a test) must give both the same person.
/// </para>
/// </summary>
public sealed class AgentResourceResolver(HarboraDbContext db, ProjectAccessService access)
{
    /// <summary>
    /// An app by slug (exact) or, failing that, by display name (case-insensitive). App slugs are
    /// unique across the platform, so a slug never needs a project to disambiguate it.
    /// </summary>
    public async Task<AgentLookup<App>> AppAsync(AgentToolContext context, string? name, CancellationToken ct)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return AgentLookup<App>.Missing("Say which app (its slug).", "بگویید کدام اپ (نامک آن).");

        var lowered = wanted.ToLowerInvariant();
        var candidates = await db.Apps
            .Include(a => a.Environment).ThenInclude(e => e!.Project)
            .Where(a => a.WorkspaceId == context.WorkspaceId && (a.Slug == lowered || a.Name.ToLower() == lowered))
            .ToListAsync(ct);

        // Only what this person can see takes part at all — an invisible slug match must neither win nor
        // hide a visible app that carries the name as its display name.
        var visible = new List<App>();
        foreach (var app in candidates)
            if (await access.CanSeeAppAsync(app.Id, ct)) visible.Add(app);

        // A slug match wins outright: it is the identifier the snapshot shows.
        if (visible.FirstOrDefault(a => a.Slug == lowered) is { } bySlug)
            return AgentLookup<App>.Of(bySlug);

        return visible.Count switch
        {
            1 => AgentLookup<App>.Of(visible[0]),
            0 => NoApp(wanted),
            _ => AgentLookup<App>.Missing(
                $"Several apps are called '{wanted}': {string.Join(", ", visible.Select(a => a.Slug))}. Use the slug.",
                $"چند اپ به نام «{wanted}» هست: {string.Join("، ", visible.Select(a => a.Slug))}. نامک را بگویید.")
        };
    }

    /// <summary>
    /// A database (any managed service) by name, case-insensitive. Names are only unique within an
    /// environment, so when more than one visible database matches, the caller is told the places it
    /// could mean and asked to pass <paramref name="project"/>/<paramref name="environment"/>.
    /// </summary>
    public async Task<AgentLookup<ManagedService>> DatabaseAsync(
        AgentToolContext context, string? name, string? project, string? environment, CancellationToken ct)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return AgentLookup<ManagedService>.Missing("Say which database (its name).", "بگویید کدام دیتابیس (نام آن).");

        var lowered = wanted.ToLowerInvariant();
        var projectSlug = project?.Trim().ToLowerInvariant();
        var environmentSlug = environment?.Trim().ToLowerInvariant();

        var query = db.ManagedServices
            .Include(s => s.Environment).ThenInclude(e => e!.Project)
            .Where(s => s.WorkspaceId == context.WorkspaceId && s.Name.ToLower() == lowered);
        if (!string.IsNullOrEmpty(projectSlug))
            query = query.Where(s => s.Environment!.Project!.Slug == projectSlug);
        if (!string.IsNullOrEmpty(environmentSlug))
            query = query.Where(s => s.Environment!.Slug == environmentSlug);

        var visible = new List<ManagedService>();
        foreach (var service in await query.ToListAsync(ct))
            if (await access.CanSeeServiceAsync(service.Id, ct)) visible.Add(service);

        return visible.Count switch
        {
            1 => AgentLookup<ManagedService>.Of(visible[0]),
            0 => AgentLookup<ManagedService>.Missing(
                $"No database named '{wanted}' that you can see.",
                $"دیتابیسی به نام «{wanted}» که به آن دسترسی داشته باشید پیدا نشد."),
            _ => AgentLookup<ManagedService>.Missing(
                $"Several databases are called '{wanted}': {string.Join(", ", visible.Select(Place))}. Pass project and environment.",
                $"چند دیتابیس به نام «{wanted}» هست: {string.Join("، ", visible.Select(Place))}. پروژه و محیط را هم بگویید.")
        };
    }

    /// <summary>A project by slug or name, case-insensitive, that this person can see.</summary>
    public async Task<AgentLookup<Project>> ProjectAsync(AgentToolContext context, string? name, CancellationToken ct)
    {
        var wanted = name?.Trim();
        if (string.IsNullOrEmpty(wanted))
            return AgentLookup<Project>.Missing("Say which project.", "بگویید کدام پروژه.");

        var lowered = wanted.ToLowerInvariant();
        var candidates = await db.Projects
            .Where(p => p.WorkspaceId == context.WorkspaceId && (p.Slug == lowered || p.Name.ToLower() == lowered))
            .ToListAsync(ct);

        var visibleIds = await access.VisibleProjectIdsAsync(ct);
        var visible = candidates.Where(p => visibleIds is null || visibleIds.Contains(p.Id))
            .OrderByDescending(p => p.Slug == lowered)
            .ToList();

        if (visible.Count == 0)
            return AgentLookup<Project>.Missing(
                $"No project named '{wanted}' that you can see.",
                $"پروژه‌ای به نام «{wanted}» که به آن دسترسی داشته باشید پیدا نشد.");

        if (visible.Count > 1 && visible[0].Slug != lowered)
            return AgentLookup<Project>.Missing(
                $"Several projects are called '{wanted}': {string.Join(", ", visible.Select(p => p.Slug))}. Use the slug.",
                $"چند پروژه به نام «{wanted}» هست: {string.Join("، ", visible.Select(p => p.Slug))}. نامک را بگویید.");

        return AgentLookup<Project>.Of(visible[0]);
    }

    /// <summary>
    /// An environment of a project by slug or name. With no <paramref name="environment"/>, the
    /// project's default environment — the one the panel's own forms preselect.
    /// </summary>
    public async Task<AgentLookup<ProjectEnvironment>> EnvironmentAsync(
        AgentToolContext context, string? project, string? environment, CancellationToken ct)
    {
        var owner = await ProjectAsync(context, project, ct);
        if (!owner.Found) return AgentLookup<ProjectEnvironment>.Missing(owner.Error!, owner.ErrorFa!);

        var environments = await db.Environments
            .Where(e => e.WorkspaceId == context.WorkspaceId && e.ProjectId == owner.Value!.Id)
            .ToListAsync(ct);

        var wanted = environment?.Trim();
        ProjectEnvironment? match;
        if (string.IsNullOrEmpty(wanted))
        {
            match = environments.FirstOrDefault(e => e.IsDefault) ?? (environments.Count == 1 ? environments[0] : null);
            if (match is null)
                return AgentLookup<ProjectEnvironment>.Missing(
                    $"Project '{owner.Value!.Slug}' has several environments ({string.Join(", ", environments.Select(e => e.Slug))}); say which.",
                    $"پروژهٔ «{owner.Value!.Slug}» چند محیط دارد ({string.Join("، ", environments.Select(e => e.Slug))})؛ بگویید کدام.");
        }
        else
        {
            var lowered = wanted.ToLowerInvariant();
            match = environments.FirstOrDefault(e => e.Slug == lowered)
                    ?? environments.FirstOrDefault(e => string.Equals(e.Name, wanted, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                return AgentLookup<ProjectEnvironment>.Missing(
                    $"Project '{owner.Value!.Slug}' has no environment '{wanted}'.",
                    $"پروژهٔ «{owner.Value!.Slug}» محیطی به نام «{wanted}» ندارد.");
        }

        return AgentLookup<ProjectEnvironment>.Of(match);
    }

    /// <summary>
    /// Everything this person can see, projects → environments → apps and databases, cutting off after
    /// <paramref name="maxResources"/> apps and databases (the rest are counted in
    /// <see cref="AgentResourceTree.Omitted"/>). <paramref name="projectFilter"/> narrows it to one
    /// project, by slug or name.
    /// </summary>
    public async Task<AgentResourceTree> TreeAsync(
        AgentToolContext context, int maxResources, string? projectFilter, CancellationToken ct)
    {
        var visibleProjects = await access.VisibleProjectIdsAsync(ct);
        var grantedApps = await access.GrantedAppIdsAsync(ct);
        var grantedServices = await access.GrantedServiceIdsAsync(ct);

        var projects = await db.Projects.AsNoTracking()
            .Where(p => p.WorkspaceId == context.WorkspaceId)
            .Select(p => new { p.Id, p.Slug, p.Name })
            .ToListAsync(ct);
        var environments = await db.Environments.AsNoTracking()
            .Where(e => e.WorkspaceId == context.WorkspaceId)
            .Select(e => new { e.Id, e.ProjectId, e.Slug, e.Name, e.IsDefault, e.IsProtected })
            .ToListAsync(ct);
        var apps = await db.Apps.AsNoTracking()
            .Where(a => a.WorkspaceId == context.WorkspaceId)
            .Select(a => new { a.Id, a.EnvironmentId, a.Slug, a.Name, a.Status, a.Kind })
            .ToListAsync(ct);
        var services = await db.ManagedServices.AsNoTracking()
            .Where(s => s.WorkspaceId == context.WorkspaceId)
            .Select(s => new { s.Id, s.EnvironmentId, s.Name, s.Type, s.Version, s.Status })
            .ToListAsync(ct);

        var projectOfEnvironment = environments.ToDictionary(e => e.Id, e => e.ProjectId);
        bool ProjectVisible(Guid projectId) => visibleProjects is null || visibleProjects.Contains(projectId);

        var visibleApps = apps.Where(a => projectOfEnvironment.TryGetValue(a.EnvironmentId, out var p)
                                          && (ProjectVisible(p) || grantedApps.Contains(a.Id))).ToList();
        var visibleServices = services.Where(s => projectOfEnvironment.TryGetValue(s.EnvironmentId, out var p)
                                                  && (ProjectVisible(p) || grantedServices.Contains(s.Id))).ToList();

        // A project appears when the person can see it, or holds a grant on something inside it — in
        // which case only that granted resource is listed under it.
        var shownProjects = projects
            .Where(p => ProjectVisible(p.Id)
                        || visibleApps.Any(a => projectOfEnvironment[a.EnvironmentId] == p.Id)
                        || visibleServices.Any(s => projectOfEnvironment[s.EnvironmentId] == p.Id))
            .Where(p => string.IsNullOrWhiteSpace(projectFilter)
                        || string.Equals(p.Slug, projectFilter.Trim(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(p.Name, projectFilter.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Slug, StringComparer.Ordinal)
            .ToList();

        var budget = maxResources;
        var omitted = 0;
        var projectNodes = new List<AgentProjectNode>();

        foreach (var project in shownProjects)
        {
            var environmentNodes = new List<AgentEnvironmentNode>();
            foreach (var environment in environments.Where(e => e.ProjectId == project.Id)
                         .OrderByDescending(e => e.IsDefault).ThenBy(e => e.Slug, StringComparer.Ordinal))
            {
                var envApps = visibleApps.Where(a => a.EnvironmentId == environment.Id)
                    .OrderBy(a => a.Slug, StringComparer.Ordinal).ToList();
                var envServices = visibleServices.Where(s => s.EnvironmentId == environment.Id)
                    .OrderBy(s => s.Name, StringComparer.Ordinal).ToList();

                // An environment of a project the person only reaches through one granted resource is
                // not theirs to see unless that resource lives in it.
                if (!ProjectVisible(project.Id) && envApps.Count == 0 && envServices.Count == 0) continue;

                var appNodes = new List<AgentAppNode>();
                foreach (var app in envApps)
                {
                    if (budget <= 0) { omitted++; continue; }
                    budget--;
                    appNodes.Add(new AgentAppNode(app.Slug, app.Name, Lower(app.Status), Lower(app.Kind)));
                }

                var databaseNodes = new List<AgentDatabaseNode>();
                foreach (var service in envServices)
                {
                    if (budget <= 0) { omitted++; continue; }
                    budget--;
                    databaseNodes.Add(new AgentDatabaseNode(service.Name, Lower(service.Type), service.Version, Lower(service.Status)));
                }

                environmentNodes.Add(new AgentEnvironmentNode(
                    environment.Slug, environment.Name, environment.IsProtected, appNodes, databaseNodes));
            }

            projectNodes.Add(new AgentProjectNode(project.Slug, project.Name, environmentNodes));
        }

        return new AgentResourceTree(projectNodes, omitted);
    }

    /// <summary>"shop/production" — where a resource lives, as the snapshot names it.</summary>
    public static string Place(ManagedService service) =>
        $"{service.Environment?.Project?.Slug ?? "?"}/{service.Environment?.Slug ?? "?"}";

    /// <summary>The same for an app.</summary>
    public static string Place(App app) =>
        $"{app.Environment?.Project?.Slug ?? "?"}/{app.Environment?.Slug ?? "?"}";

    /// <summary>An enum value the way every agent payload spells it: lower case.</summary>
    public static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();

    private static AgentLookup<App> NoApp(string wanted) => AgentLookup<App>.Missing(
        $"No app named '{wanted}' that you can see.",
        $"اپی به نام «{wanted}» که به آن دسترسی داشته باشید پیدا نشد.");
}
