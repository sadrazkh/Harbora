using Harbora.Data;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Storage;

/// <summary>
/// HARBORA-0033's read-only half: which volumes the database still knows about after the app or
/// environment that owned them is gone. Modelled on
/// <see cref="Harbora.Infrastructure.Projects.EnvironmentPlacementReport"/> — same idiom, same rule
/// that a clean answer is stated, not merely absent, because a report that printed nothing for a
/// clean database would look exactly like a report that never ran.
///
/// <para>
/// Two of the three things "orphan" could mean here are enforced by the schema today, and both are
/// queried for real anyway rather than assumed: <c>Volume.AppId</c> cascades with its <c>App</c>
/// (<c>HarboraDbContext</c>, <c>DeleteBehavior.Cascade</c>), and <c>App.EnvironmentId</c> is a
/// required foreign key that refuses to let its <c>Environment</c> go while an app still points at it
/// (<c>DeleteBehavior.Restrict</c>). A constraint holds only as firmly as every write path that could
/// bypass EF — a restore, a hand-run script, an install mid-migration — and <c>EnvironmentPlacementReport</c>'s
/// own Q1 made the identical choice about a null <c>EnvironmentId</c> for the identical reason: a
/// report that only recited the constraint's name would say nothing new on the day it is actually
/// violated.
/// </para>
///
/// <para>
/// The third — a Docker volume physically present on a node with no <see cref="Domain.Apps.Volume"/>
/// row pointing at it at all, left behind by an unmount or by an app delete with
/// <c>removeVolumes: false</c> — is exactly the "volumes on disk" this report's name promises, and it
/// is the one question this build cannot answer: doing so needs a live connection to every server's
/// Docker daemon, and no engine in this codebase exposes a "list every volume" call to build that on
/// (<see cref="Application.Abstractions.IDockerEngine"/> has <c>EnsureVolumeAsync</c> and
/// <c>RemoveVolumeAsync</c>, nothing that enumerates). Rather than leave that silently unmentioned,
/// section 3 of <see cref="Render"/> always says plainly that it was not checked and why — the same
/// discipline this report's own Q1/Q2 sections apply to a zero that WAS actually checked.
/// </para>
/// </summary>
public static class VolumeOrphanReport
{
    public static async Task<VolumeOrphanReportResult> BuildAsync(HarboraDbContext db, CancellationToken ct = default)
    {
        // IgnoreQueryFilters() is defence in depth here too, for the same reason
        // EnvironmentPlacementReport states it: AdminCommands opens this context unscoped, so the
        // workspace filters are already inert. Volume itself carries none in the first place
        // (HarboraDbContext's own remarks list it among the tables deliberately left unfiltered).
        var volumes = await db.Volumes.IgnoreQueryFilters()
            .Select(v => new { v.Id, v.Name, v.MountPath, v.AppId, v.Protected })
            .ToListAsync(ct);
        var apps = await db.Apps.IgnoreQueryFilters()
            .Select(a => new { a.Id, a.Name, a.WorkspaceId, a.EnvironmentId })
            .ToListAsync(ct);
        var appById = apps.ToDictionary(a => a.Id);
        var environmentIds = (await db.Environments.IgnoreQueryFilters()
            .Select(e => e.Id).ToListAsync(ct)).ToHashSet();
        var workspaceSlugs = await db.Workspaces
            .Select(w => new { w.Id, w.Slug }).ToDictionaryAsync(w => w.Id, w => w.Slug, ct);

        string SlugOf(Guid workspaceId) =>
            workspaceSlugs.TryGetValue(workspaceId, out var slug) ? slug : "(unknown workspace)";

        // ---- volumes whose App row is gone ----
        var noApp = volumes.Where(v => !appById.ContainsKey(v.AppId))
            .Select(v => new OrphanedVolume(v.Id, v.Name, v.MountPath, v.Protected, v.AppId, null, null, null))
            .OrderBy(v => v.MountPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // ---- volumes whose App exists but whose Environment is gone ----
        var noEnvironment = volumes
            .Where(v => appById.TryGetValue(v.AppId, out var app) && !environmentIds.Contains(app.EnvironmentId))
            .Select(v =>
            {
                var app = appById[v.AppId];
                return new OrphanedVolume(
                    v.Id, v.Name, v.MountPath, v.Protected, v.AppId, app.Name, app.WorkspaceId, SlugOf(app.WorkspaceId));
            })
            .OrderBy(v => v.MountPath, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new VolumeOrphanReportResult(noApp, noEnvironment, volumes.Count, DiskCheckPerformed: false);
    }

    /// <summary>
    /// Formats a built report for a person to read on a terminal. Every section names a zero
    /// explicitly, or names why it could not be checked at all — the same rule
    /// <see cref="Harbora.Infrastructure.Projects.EnvironmentPlacementReport.Render"/> follows, for
    /// the same reason.
    /// </summary>
    public static string Render(VolumeOrphanReportResult report)
    {
        var w = new System.Text.StringBuilder();

        w.AppendLine("Volume orphan report");
        w.AppendLine("────────────────────────────────────────");
        w.AppendLine();

        w.AppendLine($"1) Volume rows with no owning App: {report.VolumesWithNoApp.Count} of {report.TotalVolumeCount}");
        if (report.VolumesWithNoApp.Count == 0)
        {
            w.AppendLine("   None found. (Volume.AppId cascades with its App, so this should never be non-zero —");
            w.AppendLine("   checked directly against the database rather than assumed from the schema.)");
        }
        else
        {
            foreach (var v in report.VolumesWithNoApp)
                w.AppendLine($"   - {v.MountPath,-30} id={v.Id} name={v.Name} appId={v.AppId}{(v.Protected ? " [PROTECTED]" : "")}");
        }
        w.AppendLine();

        w.AppendLine($"2) Volumes whose App exists but its Environment is gone: {report.VolumesWithNoEnvironment.Count} of {report.TotalVolumeCount}");
        if (report.VolumesWithNoEnvironment.Count == 0)
        {
            w.AppendLine("   None found. (App.EnvironmentId is a required, Restrict-on-delete foreign key, so this");
            w.AppendLine("   should never be non-zero — checked directly against the database rather than assumed.)");
        }
        else
        {
            foreach (var v in report.VolumesWithNoEnvironment)
                w.AppendLine($"   - {v.MountPath,-30} id={v.Id} app={v.AppName} workspace={v.WorkspaceSlug}{(v.Protected ? " [PROTECTED]" : "")}");
        }
        w.AppendLine();

        w.AppendLine("3) Volumes on disk with no database row at all: " +
                      (report.DiskCheckPerformed ? "0" : "not checked"));
        if (!report.DiskCheckPerformed)
        {
            w.AppendLine("   This needs a live connection to every server's Docker daemon, and no engine in this");
            w.AppendLine("   codebase exposes a listing of every volume to build that on. A volume left behind by an");
            w.AppendLine("   unmount, or by an app delete with its data kept, will not appear above until that");
            w.AppendLine("   capability exists — this line exists so that gap is never mistaken for a clean zero.");
        }

        return w.ToString();
    }
}

/// <summary>
/// One <see cref="Domain.Apps.Volume"/> row this report found with no live App or Environment behind
/// it. <see cref="AppName"/>, <see cref="WorkspaceId"/> and <see cref="WorkspaceSlug"/> are null for a
/// volume with no App at all — there is nothing left to name them from.
/// </summary>
public sealed record OrphanedVolume(
    Guid Id, string Name, string MountPath, bool Protected, Guid AppId,
    string? AppName, Guid? WorkspaceId, string? WorkspaceSlug);

/// <summary>The full report: two real answers, and one honestly named as not yet answerable.</summary>
public sealed record VolumeOrphanReportResult(
    IReadOnlyList<OrphanedVolume> VolumesWithNoApp,
    IReadOnlyList<OrphanedVolume> VolumesWithNoEnvironment,
    int TotalVolumeCount,
    bool DiskCheckPerformed);
