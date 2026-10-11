using System.Text;
using Harbora.Data;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The compact picture of the workspace the model gets with every turn: what this person can see —
/// projects, environments, apps and databases, by name and state — and where in the panel they are.
///
/// <para>
/// Capped at <see cref="MaxResources"/> apps and databases, with "and N more", because the snapshot is
/// paid for on every model call of every turn. Anything past the cap is one <c>list_resources</c> call
/// away. It is built from <see cref="AgentResourceResolver.TreeAsync"/>, the same visibility the tools
/// use, so the model is never told about something a tool would then call "not found".
/// </para>
/// </summary>
public sealed class AgentWorkspaceSnapshot(HarboraDbContext db, AgentResourceResolver resolver, ProjectAccessService access)
{
    public const int MaxResources = 60;

    private const int MaxPathLength = 200;

    /// <summary>The resources part of the system message.</summary>
    public async Task<string> DescribeAsync(AgentToolContext context, CancellationToken ct)
    {
        var tree = await resolver.TreeAsync(context, MaxResources, projectFilter: null, ct);
        var text = new StringBuilder("Workspace snapshot — what this user can see (projects → environments → apps and databases):\n");

        if (tree.Projects.Count == 0)
            return text.Append("(No projects yet.)").ToString();

        foreach (var project in tree.Projects)
        {
            text.Append($"- project \"{project.Slug}\" ({project.Name})\n");
            foreach (var environment in project.Environments)
            {
                text.Append($"  - environment \"{environment.Slug}\" ({environment.Name}{(environment.Protected ? ", protected" : "")})");
                if (environment.Apps.Count == 0 && environment.Databases.Count == 0)
                {
                    text.Append(": no apps or databases\n");
                    continue;
                }

                text.Append('\n');
                foreach (var app in environment.Apps)
                    text.Append($"    - app \"{app.Slug}\": {app.Status}, {app.Kind}\n");
                foreach (var database in environment.Databases)
                    text.Append($"    - database \"{database.Name}\": {database.Engine} {database.Version}, {database.Status}\n");
            }
        }

        if (tree.Omitted > 0)
            text.Append($"- …and {tree.Omitted} more apps and databases not listed here; call list_resources to see them.\n");

        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// "The user is looking at …" — the page context resolved to names, so "restart this app" has a
    /// referent. Ids the person cannot see are dropped silently: naming them would leak that they
    /// exist. Null when there is nothing to say.
    /// </summary>
    public async Task<string?> DescribePageAsync(AgentToolContext context, CancellationToken ct)
    {
        var page = context.Page;
        if (page is null) return null;

        var parts = new List<string>();

        if (page.AppId is { } appId && await access.CanSeeAppAsync(appId, ct))
        {
            var app = await db.Apps.AsNoTracking()
                .Where(a => a.Id == appId && a.WorkspaceId == context.WorkspaceId)
                .Select(a => new { a.Slug, Environment = a.Environment!.Slug, Project = a.Environment.Project!.Slug })
                .FirstOrDefaultAsync(ct);
            if (app is not null) parts.Add($"app \"{app.Slug}\" ({app.Project}/{app.Environment})");
        }

        if (page.ServiceId is { } serviceId && await access.CanSeeServiceAsync(serviceId, ct))
        {
            var service = await db.ManagedServices.AsNoTracking()
                .Where(s => s.Id == serviceId && s.WorkspaceId == context.WorkspaceId)
                .Select(s => new { s.Name, Environment = s.Environment!.Slug, Project = s.Environment.Project!.Slug })
                .FirstOrDefaultAsync(ct);
            if (service is not null) parts.Add($"database \"{service.Name}\" ({service.Project}/{service.Environment})");
        }

        var visibleProjects = await access.VisibleProjectIdsAsync(ct);

        if (page.EnvironmentId is { } environmentId)
        {
            var environment = await db.Environments.AsNoTracking()
                .Where(e => e.Id == environmentId && e.WorkspaceId == context.WorkspaceId)
                .Select(e => new { e.Slug, e.ProjectId, Project = e.Project!.Slug })
                .FirstOrDefaultAsync(ct);
            if (environment is not null && (visibleProjects is null || visibleProjects.Contains(environment.ProjectId)))
                parts.Add($"environment \"{environment.Slug}\" of project \"{environment.Project}\"");
        }
        else if (page.ProjectId is { } projectId && (visibleProjects is null || visibleProjects.Contains(projectId)))
        {
            var project = await db.Projects.AsNoTracking()
                .Where(p => p.Id == projectId && p.WorkspaceId == context.WorkspaceId)
                .Select(p => p.Slug)
                .FirstOrDefaultAsync(ct);
            if (project is not null) parts.Add($"project \"{project}\"");
        }

        var path = Clean(page.Path);
        if (parts.Count == 0 && path is null) return null;

        var where = path is null ? "" : $" the page {path}";
        var what = parts.Count == 0 ? "" : $" — {string.Join(", ", parts)}";
        return $"The user is currently looking at{where}{what}. \"This app\" or \"this database\" means that one.";
    }

    /// <summary>
    /// The path, or null. It arrives from the browser and is written into the system message, so only
    /// something shaped like a URL path is kept — no spaces, no line breaks, nothing that reads as a
    /// sentence of instructions — and anything else is dropped whole rather than cleaned up.
    /// </summary>
    private static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.Trim();
        return trimmed.Length <= MaxPathLength && PathShape.IsMatch(trimmed) ? trimmed : null;
    }

    private static readonly System.Text.RegularExpressions.Regex PathShape =
        new(@"^/[A-Za-z0-9/_\-.~%]*$", System.Text.RegularExpressions.RegexOptions.Compiled);
}
