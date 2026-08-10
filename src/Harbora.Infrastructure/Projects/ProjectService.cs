using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Microsoft.EntityFrameworkCore;
using Environment = Harbora.Domain.Projects.Environment;
using Project = Harbora.Domain.Projects.Project;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// Creating and finding projects and their environments.
///
/// The invariant everything else leans on: <b>a workspace always has at least one project, and a
/// project always has at least one environment</b>. The migration backfilled that for existing data;
/// this keeps it true for workspaces created afterwards, so no screen ever has to handle "belongs to
/// nothing" — the state that would otherwise appear months later, in production, on somebody's
/// dashboard.
/// </summary>
public sealed partial class ProjectService(HarboraDbContext db, ISystemClock clock, IQuotaService? quota = null)
{
    /// <summary>The slug the migration used, so old and new workspaces look identical.</summary>
    public const string DefaultProjectSlug = "default";
    public const string DefaultEnvironmentSlug = "production";

    /// <summary>
    /// Returns the workspace's default environment, creating the project and environment if this
    /// workspace has none. Safe to call on every request that needs one.
    /// </summary>
    public async Task<Environment> EnsureDefaultEnvironmentAsync(Guid workspaceId, CancellationToken ct)
    {
        var existing = await db.Environments
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var name = await db.Workspaces.Where(w => w.Id == workspaceId).Select(w => w.Name)
                       .FirstOrDefaultAsync(ct) ?? "Default";

        var (_, environment) = await CreateAsync(workspaceId, name, DefaultProjectSlug, ct);
        return environment;
    }

    /// <summary>
    /// Resolves the environment a new resource should be created in.
    ///
    /// The id arrives from a form field or a query string, which is the most obvious thing on the page
    /// to change by hand. It is honoured only after it is shown to belong to this workspace; anything
    /// else — another tenant's id, a stale bookmark, nothing at all — falls back to this workspace's
    /// own default rather than failing, or far worse, succeeding somewhere it should not.
    ///
    /// This lives here rather than in the controller so the guarantee is one testable thing, not a
    /// pattern each caller has to remember to repeat.
    /// </summary>
    public async Task<Environment> ResolveEnvironmentAsync(Guid workspaceId, Guid? requested, CancellationToken ct)
    {
        if (requested is { } wanted)
        {
            var owned = await db.Environments
                .FirstOrDefaultAsync(e => e.Id == wanted && e.WorkspaceId == workspaceId, ct);
            if (owned is not null) return owned;
        }

        return await EnsureDefaultEnvironmentAsync(workspaceId, ct);
    }

    /// <summary>
    /// Creates a project with its first environment. The two are made together because a project
    /// with nowhere to deploy is not a state worth being able to represent.
    /// </summary>
    public async Task<(Project Project, Environment Environment)> CreateAsync(
        Guid workspaceId, string name, string? slug, CancellationToken ct) =>
        await CreateCoreAsync(workspaceId, name, slug, save: true, ct);

    /// <summary>
    /// Builds a project and its first environment in the current unit of work without saving it.
    /// Stack deployment uses this so its project, resources and first-hour debit either all commit
    /// or none do.
    /// </summary>
    public Task<(Project Project, Environment Environment)> PrepareAsync(
        Guid workspaceId, string name, string? slug, CancellationToken ct) =>
        CreateCoreAsync(workspaceId, name, slug, save: false, ct);

    private async Task<(Project Project, Environment Environment)> CreateCoreAsync(
        Guid workspaceId, string name, string? slug, bool save, CancellationToken ct)
    {
        if (quota is not null)
        {
            var check = await quota.CanAddGovernedResourcesAsync(workspaceId,
                new GovernanceQuotaDelta(Projects: 1, Environments: 1), ct);
            if (!check.Allowed) throw new QuotaRefusedException(check);
        }

        var project = new Project
        {
            WorkspaceId = workspaceId,
            Name = string.IsNullOrWhiteSpace(name) ? "Project" : name.Trim(),
            Slug = await UniqueSlugAsync(workspaceId, slug ?? name, ct),
            CreatedAt = clock.UtcNow
        };

        var environment = new Environment
        {
            WorkspaceId = workspaceId,
            Project = project,
            Name = "Production",
            Slug = DefaultEnvironmentSlug,
            IsDefault = true,
            CreatedAt = clock.UtcNow
        };

        db.Projects.Add(project);
        db.Environments.Add(environment);
        if (save) await db.SaveChangesAsync(ct);
        return (project, environment);
    }

    /// <summary>Adds another environment (staging, preview) to an existing project.</summary>
    public async Task<Environment> AddEnvironmentAsync(
        Guid workspaceId, Guid projectId, string name, CancellationToken ct)
    {
        if (quota is not null)
        {
            var check = await quota.CanAddGovernedResourcesAsync(workspaceId,
                new GovernanceQuotaDelta(Environments: 1), ct);
            if (!check.Allowed) throw new QuotaRefusedException(check);
        }

        var slug = Slugify(name);
        if (slug.Length == 0) slug = "environment";

        // Unique per project, so two projects can each have a "staging".
        var taken = await db.Environments
            .Where(e => e.ProjectId == projectId).Select(e => e.Slug).ToListAsync(ct);
        var candidate = slug;
        for (var n = 2; taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
            candidate = $"{slug}-{n}";

        var environment = new Environment
        {
            WorkspaceId = workspaceId,
            ProjectId = projectId,
            Name = string.IsNullOrWhiteSpace(name) ? candidate : name.Trim(),
            Slug = candidate,
            // Never the default: promoting an environment is a deliberate act, not a side effect of
            // adding one.
            IsDefault = false,
            CreatedAt = clock.UtcNow
        };
        db.Environments.Add(environment);
        await db.SaveChangesAsync(ct);
        return environment;
    }

    private async Task<string> UniqueSlugAsync(Guid workspaceId, string source, CancellationToken ct)
    {
        var slug = Slugify(source);
        if (slug.Length == 0) slug = "project";

        var taken = await db.Projects
            .Where(p => p.WorkspaceId == workspaceId).Select(p => p.Slug).ToListAsync(ct);

        var candidate = slug;
        for (var n = 2; taken.Contains(candidate, StringComparer.OrdinalIgnoreCase); n++)
            candidate = $"{slug}-{n}";
        return candidate;
    }

    /// <summary>
    /// URL- and DNS-safe. The slug ends up in the private network name and in internal hostnames, so
    /// it has to survive DNS, not merely a URL.
    /// </summary>
    public static string Slugify(string value)
    {
        var lowered = (value ?? "").Trim().ToLowerInvariant();
        var slug = NonSlug().Replace(lowered, "-").Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
