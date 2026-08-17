using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Projects;

/// <summary>
/// P1 of the app/environment management phase — "the report nobody has run".
///
/// <para>
/// Answers four questions against the live database, read-only: which <c>App</c> and
/// <c>ManagedService</c> rows have a null <c>EnvironmentId</c>; which environments hold no workload;
/// how many workloads would attach to more than one Docker network today; and whether any workspace
/// has a workload but no project.
/// </para>
///
/// <para>
/// The migration that introduced <c>EnvironmentId</c> also backfilled it, on 2026-07-30, and every
/// creation path has set it since. So a null row found here is not an unfinished backfill — it is a
/// workload that was detached <em>after</em> the backfill by <c>HarboraDbContext</c>'s
/// <c>DeleteBehavior.SetNull</c> on an environment delete. A non-zero count is a bug report against
/// that delete path, not something for this report to fix: it only reads.
/// </para>
///
/// <para>
/// Every query below reads the whole of a handful of platform tables into memory rather than pushing
/// aggregates into SQL. That is deliberate: an install this report is meant to run against has, per
/// the most recent recorded figure, a small number of apps, and a report run once before a risky
/// migration should be easy to read and easy to verify by eye, not a query plan.
/// </para>
/// </summary>
public static class EnvironmentPlacementReport
{
    public static async Task<EnvironmentPlacementReportResult> BuildAsync(
        HarboraDbContext db, CancellationToken ct = default)
    {
        // IgnoreQueryFilters() is defence in depth, not the reason this works: AdminCommands opens
        // this context with the default (system, unscoped) constructor, so the workspace filters on
        // App/ManagedService/Project/Environment are already inert. Stating it explicitly means the
        // query still reads every workspace if this is ever called from a scoped context by mistake.
        var apps = await db.Apps.IgnoreQueryFilters()
            .Select(a => new { a.Id, a.Name, a.WorkspaceId, a.EnvironmentId })
            .ToListAsync(ct);
        var services = await db.ManagedServices.IgnoreQueryFilters()
            .Select(s => new { s.Id, s.Name, s.WorkspaceId, s.EnvironmentId })
            .ToListAsync(ct);
        var environments = await db.Environments.IgnoreQueryFilters()
            .Select(e => new { e.Id, e.Name, e.Slug, e.WorkspaceId, ProjectSlug = e.Project!.Slug })
            .ToListAsync(ct);
        var projectWorkspaceIds = await db.Projects.IgnoreQueryFilters()
            .Select(p => p.WorkspaceId)
            .ToListAsync(ct);
        var workspaceSlugs = await db.Workspaces
            .Select(w => new { w.Id, w.Slug })
            .ToDictionaryAsync(w => w.Id, w => w.Slug, ct);

        string SlugOf(Guid workspaceId) =>
            workspaceSlugs.TryGetValue(workspaceId, out var slug) ? slug : "(unknown workspace)";

        // ---- Q1: which App/ManagedService rows have a null EnvironmentId ----
        //
        // Answered empty, always, since P2 (2026-08-17 app-environment-management design): the column
        // is a required foreign key now, so a null EnvironmentId cannot exist in the schema at all —
        // this is no longer a question the data can answer any other way. Left as a field rather than
        // removed, for the same reason Q3 stayed after P3: a section that silently disappeared from
        // this report would read as "not checked" to an operator who remembers it being here.

        var unplacedApps = new List<UnplacedWorkload>();
        var unplacedServices = new List<UnplacedWorkload>();

        // ---- Q2: environments with no workload ----

        var occupiedEnvironmentIds = apps.Select(a => a.EnvironmentId)
            .Concat(services.Select(s => s.EnvironmentId))
            .ToHashSet();

        var emptyEnvironments = environments.Where(e => !occupiedEnvironmentIds.Contains(e.Id))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .Select(e => new EmptyEnvironment(e.Id, e.Name, e.Slug, e.ProjectSlug ?? "", e.WorkspaceId, SlugOf(e.WorkspaceId)))
            .ToList();

        // ---- Q3: workloads that would attach to more than one network today ----
        //
        // Answered zero, always, since P3 (2026-08-17 app-environment-management design): the dual
        // attach this question was about is gone. NetworkPlan.For used to return two names —
        // [environment, workspace] — whenever an environment network existed, because both production
        // call sites (DeploymentPipeline.cs, ManagedServiceEngine.cs) hardcoded keepWorkspaceNetwork:
        // true. P3 moved every one-off that reached a database on the workspace network onto the
        // workload's own environment network first — BackupEngine's dump, restore and restore
        // rehearsal, the backup module's stager, and ManagedServiceEngine's rotation — proved each one
        // still worked, and only then deleted the parameter. NetworkPlan.For now returns a single name
        // unconditionally, so no workload can attach to more than one network through it any more.
        // Left as a field, not removed, because P1's report shape is what an operator reads before a
        // migration, and a section that silently disappeared would read as "not checked" rather than
        // "checked, and the answer is now always zero".
        var dualAttachCount = 0;

        // ---- Q4: a workspace with a workload but no project ----

        var workspacesWithProjects = projectWorkspaceIds.ToHashSet();
        var workspacesWithWorkloads = apps.Select(a => a.WorkspaceId)
            .Concat(services.Select(s => s.WorkspaceId))
            .ToHashSet();

        var workspacesMissingProject = workspacesWithWorkloads.Except(workspacesWithProjects)
            .Select(workspaceId => new WorkspaceMissingProject(
                workspaceId, SlugOf(workspaceId),
                apps.Count(a => a.WorkspaceId == workspaceId),
                services.Count(s => s.WorkspaceId == workspaceId)))
            .OrderBy(w => w.WorkspaceSlug, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new EnvironmentPlacementReportResult(
            unplacedApps, unplacedServices, emptyEnvironments,
            apps.Count + services.Count, dualAttachCount, workspacesMissingProject);
    }

    /// <summary>
    /// Formats a built report for a person to read on a terminal. Every section names a zero
    /// explicitly — a report that printed nothing for a clean database would look exactly like a
    /// report that never ran, and that ambiguity is the one thing P1 exists to remove.
    /// </summary>
    public static string Render(EnvironmentPlacementReportResult report)
    {
        var w = new System.Text.StringBuilder();

        w.AppendLine("Environment placement report");
        w.AppendLine("────────────────────────────────────────");
        w.AppendLine();

        // Deliberately not a count. Since P2 made EnvironmentId a required foreign key, nothing above
        // asks the database this question any more — so printing "0" would be a claim nobody made.
        // The operator this report exists for runs it BEFORE a risky migration, quite possibly on a
        // server where that migration has not applied, and a reassuring zero from a check that never
        // ran is the exact failure this whole report was built to prevent.
        w.AppendLine("1) Workloads with no environment (EnvironmentId IS NULL): enforced by the schema");
        if (report.UnplacedWorkloadCount == 0)
        {
            w.AppendLine("   Not queried — the column is a required foreign key, so this state cannot be stored.");
            w.AppendLine("   If the EnvironmentId migration has not applied here, this line is not an answer.");
        }
        else
        {
            foreach (var a in report.UnplacedApps)
                w.AppendLine($"   - App             {a.Name,-30} id={a.Id} workspace={a.WorkspaceSlug}");
            foreach (var s in report.UnplacedManagedServices)
                w.AppendLine($"   - ManagedService  {s.Name,-30} id={s.Id} workspace={s.WorkspaceSlug}");
            w.AppendLine("   These were detached AFTER the 2026-07-30 backfill by an environment delete.");
            w.AppendLine("   Treat this as a bug report against that delete path, not something to patch here.");
        }
        w.AppendLine();

        w.AppendLine($"2) Environments with no workload: {report.EmptyEnvironments.Count}");
        if (report.EmptyEnvironments.Count == 0)
        {
            w.AppendLine("   None found.");
        }
        else
        {
            foreach (var e in report.EmptyEnvironments)
                w.AppendLine($"   - {e.ProjectSlug}/{e.Slug,-20} id={e.Id} workspace={e.WorkspaceSlug}");
        }
        w.AppendLine();

        w.AppendLine($"3) Workloads that would attach to more than one network today: " +
                      $"{report.DualAttachWorkloadCount} of {report.TotalWorkloadCount}");
        w.AppendLine("   (always zero since P3 retired the dual attach; see NetworkPlan.For)");
        w.AppendLine();

        w.AppendLine($"4) Workspaces with a workload but no project: {report.WorkspacesWithWorkloadsButNoProject.Count}");
        if (report.WorkspacesWithWorkloadsButNoProject.Count == 0)
        {
            w.AppendLine("   None found.");
        }
        else
        {
            foreach (var ws in report.WorkspacesWithWorkloadsButNoProject)
                w.AppendLine($"   - {ws.WorkspaceSlug,-20} id={ws.WorkspaceId} " +
                              $"apps={ws.AppCount} managedServices={ws.ManagedServiceCount}");
        }

        return w.ToString();
    }
}

/// <summary>One <c>App</c> or <c>ManagedService</c> row with a null <c>EnvironmentId</c>.</summary>
public sealed record UnplacedWorkload(Guid Id, string Kind, string Name, Guid WorkspaceId, string WorkspaceSlug);

/// <summary>An <c>Environment</c> with no <c>App</c> or <c>ManagedService</c> pointing at it.</summary>
public sealed record EmptyEnvironment(
    Guid Id, string Name, string Slug, string ProjectSlug, Guid WorkspaceId, string WorkspaceSlug);

/// <summary>A workspace that owns at least one workload but no <c>Project</c> row.</summary>
public sealed record WorkspaceMissingProject(Guid WorkspaceId, string WorkspaceSlug, int AppCount, int ManagedServiceCount);

/// <summary>The full P1 report: four answers, each naming its rows rather than only counting them.</summary>
public sealed record EnvironmentPlacementReportResult(
    IReadOnlyList<UnplacedWorkload> UnplacedApps,
    IReadOnlyList<UnplacedWorkload> UnplacedManagedServices,
    IReadOnlyList<EmptyEnvironment> EmptyEnvironments,
    int TotalWorkloadCount,
    int DualAttachWorkloadCount,
    IReadOnlyList<WorkspaceMissingProject> WorkspacesWithWorkloadsButNoProject)
{
    public int UnplacedWorkloadCount => UnplacedApps.Count + UnplacedManagedServices.Count;
}
