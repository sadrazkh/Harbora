using System.Globalization;
using Harbora.Data;
using Harbora.Domain.Deployments;
using Harbora.Infrastructure.Deployments;
using Harbora.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Where a build works, and how that place is removed.
///
/// <para>
/// Every build that needs source on disk (a Git checkout, an uploaded archive, a function app's
/// generated host, a Compose stack's tree) works in <c>{Runtime:WorkDir}/{app slug}/{deployment
/// number}</c>. A deployment number is never reused, and the only deletes there ever were ran before
/// a checkout into the same path, so every build ever made stayed on the <c>harbora_builds</c> volume.
/// The pipeline now removes its own when it ends (<c>DeploymentPipeline.ExecuteAsync</c>), and
/// <see cref="BuildDirectorySweep"/> removes what older versions, and any crash, left behind.
/// </para>
///
/// <para>
/// <b>Nothing reads one afterwards.</b> The image is the build's only product: a rollback re-releases
/// an image and never rebuilds (ADR-006); the build cache names an earlier image, not a directory
/// (<c>BuildCache</c>); a Compose stack refuses host bind mounts (<c>ComposeFile</c>), so no running
/// service mounts a file from here; an uploaded archive is a file under <c>_uploads</c> that the build
/// deletes once unpacked; and Launch reads its source in a folder of its own under <c>_launch</c>,
/// which it removes itself.
/// </para>
/// </summary>
public static class BuildDirectories
{
    /// <summary>The directory a deployment builds in.</summary>
    public static string For(string workRoot, string appSlug, int deploymentNumber) =>
        Path.Combine(workRoot, appSlug, deploymentNumber.ToString(CultureInfo.InvariantCulture));

    /// <summary>A build directory found on disk: the app it was named for, and the deployment.</summary>
    public sealed record Found(string AppSlug, int DeploymentNumber, string Path);

    /// <summary>
    /// Every <c>{slug}/{number}</c> directory under the work root, and nothing else.
    ///
    /// <para>
    /// A top-level folder whose name starts with <c>_</c> or <c>.</c> is the platform's own
    /// (<c>_uploads</c>, <c>_launch</c>) and is never a candidate; neither is a child whose name is not
    /// a deployment number, nor anything that is a link rather than a directory. Whatever this does not
    /// recognise, it leaves.
    /// </para>
    /// </summary>
    public static IReadOnlyList<Found> Find(string workRoot)
    {
        if (string.IsNullOrWhiteSpace(workRoot) || !Directory.Exists(workRoot)) return [];

        var found = new List<Found>();
        foreach (var app in new DirectoryInfo(workRoot).EnumerateDirectories())
        {
            if (app.Name.StartsWith('_') || app.Name.StartsWith('.') || IsLink(app)) continue;

            foreach (var build in app.EnumerateDirectories())
            {
                if (IsLink(build) || !IsDeploymentNumber(build.Name, out var number)) continue;
                found.Add(new Found(app.Name, number, build.FullName));
            }
        }

        return found;
    }

    /// <summary>
    /// Removes one build directory and returns the bytes its files held; zero when it is not there.
    ///
    /// <para>
    /// <b>Never outside the work root, and never through a link.</b> The tree is a tenant's checkout
    /// and can hold a symbolic link to anywhere, so files are counted and unlocked with links skipped,
    /// and the delete itself removes a link rather than following it. The path is resolved against the
    /// root first (<see cref="PathGuard"/>), and the root itself is refused.
    /// </para>
    /// <para>
    /// Throws when it cannot remove the directory, for the caller to report: a removal that half
    /// happened is not one to count.
    /// </para>
    /// </summary>
    public static long Remove(string workRoot, string directory)
    {
        var check = PathGuard.ResolveWithin(workRoot, directory);
        if (!check.Allowed ||
            string.Equals(
                Path.TrimEndingDirectorySeparator(check.ResolvedPath!),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(workRoot)),
                StringComparison.Ordinal))
            throw new InvalidOperationException($"'{directory}' is not a build directory inside '{workRoot}'.");

        var tree = new DirectoryInfo(check.ResolvedPath!);
        if (!tree.Exists) return 0;
        if (IsLink(tree))
            throw new InvalidOperationException($"'{directory}' is a link, not a build directory.");

        var bytes = 0L;
        var withoutLinks = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
        foreach (var file in tree.EnumerateFiles("*", withoutLinks))
        {
            bytes += file.Length;
            // A checkout's .git objects are read-only, and Windows refuses to delete a read-only file.
            // Elsewhere the directory's own permission is what a delete needs, so nothing is changed.
            if (OperatingSystem.IsWindows() && file.IsReadOnly) file.IsReadOnly = false;
        }

        // Links to directories first, each removed as the link it is. The recursive delete would leave
        // them alone too, but on Windows it also tries to unmount a junction, which needs an elevated
        // process, and then reports the whole removal as failed.
        RemoveDirectoryLinks(tree);
        tree.Delete(recursive: true);
        return bytes;
    }

    private static void RemoveDirectoryLinks(DirectoryInfo directory)
    {
        foreach (var child in directory.EnumerateDirectories())
        {
            if (!IsLink(child)) RemoveDirectoryLinks(child);
            // RemoveDirectory takes a junction or a directory symlink itself on Windows; elsewhere a
            // symlink is a name to unlink, which never touches what it names.
            else if (OperatingSystem.IsWindows()) Directory.Delete(child.FullName, recursive: false);
            else File.Delete(child.FullName);
        }
    }

    private static bool IsLink(FileSystemInfo entry) => entry.Attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool IsDeploymentNumber(string name, out int number)
    {
        number = 0;
        return name.Length is > 0 and <= 9 && name.All(char.IsAsciiDigit)
               && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && number > 0;
    }
}

/// <summary>What one sweep of the work directory did.</summary>
/// <param name="Removed">Build directories removed.</param>
/// <param name="FreedBytes">What their files held, counted file by file as they were removed.</param>
/// <param name="Kept">Build directories left because a deployment that is not over still owns them.</param>
/// <param name="Failed">Directories that could not be removed, each with the reason.</param>
public sealed record BuildDirectorySweepResult(
    int Removed, long FreedBytes, int Kept, IReadOnlyList<string> Failed);

/// <summary>
/// Removes the build directories nothing owns any more: every one left on disk by a deployment that
/// is over, or by an app that no longer exists. Reached two ways — once a day unattended
/// (<see cref="BuildDirectorySweeper"/>) and from the Clean up disk button.
///
/// <para>
/// <b>An unfinished deployment keeps its directory.</b> One that is queued, building, deploying,
/// health-checking or waiting for approval (<see cref="DeploymentStateMachine.Unsettled"/>) owns
/// <c>{slug}/{number}</c>, read unfiltered because this has no session. The disk is listed
/// <i>before</i> the database is asked, and the pipeline records Building before it creates its
/// directory, so a directory this sweep has seen belongs to a deployment whose status is already
/// stored: one starting while the sweep runs is either already protected or not yet on the list.
/// </para>
/// </summary>
public sealed class BuildDirectorySweep(
    HarboraDbContext db,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<BuildDirectorySweep> logger)
{
    public async Task<BuildDirectorySweepResult> RunAsync(CancellationToken ct)
    {
        var root = options.Value.WorkDir;

        // Disk first, then the database: see the class remarks for why the order is the guarantee.
        var found = BuildDirectories.Find(root);
        if (found.Count == 0) return new BuildDirectorySweepResult(0, 0, 0, []);

        var unsettled = DeploymentStateMachine.Unsettled.ToList();
        var owned = (await db.Deployments.IgnoreQueryFilters().AsNoTracking()
                .Where(d => unsettled.Contains(d.Status))
                .Join(db.Apps.IgnoreQueryFilters(), d => d.AppId, a => a.Id, (d, a) => new { a.Slug, d.Number })
                .ToListAsync(ct))
            .Select(x => (x.Slug, x.Number))
            .ToHashSet();

        int removed = 0, kept = 0;
        long freed = 0;
        var failed = new List<string>();

        foreach (var build in found)
        {
            ct.ThrowIfCancellationRequested();

            if (owned.Contains((build.AppSlug, build.DeploymentNumber)))
            {
                kept++;
                continue;
            }

            try
            {
                freed += BuildDirectories.Remove(root, build.Path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add($"{build.AppSlug}/{build.DeploymentNumber}: {ex.Message}");
                logger.LogWarning(ex, "Build directory {Path} could not be removed.", build.Path);
            }
        }

        // An app's own folder once its last build has gone. Not recursive: a build that has just
        // started there makes the folder not empty, and the delete then refuses rather than taking it.
        foreach (var appFolder in found.Select(f => Path.GetDirectoryName(f.Path)!).Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (Directory.Exists(appFolder) && !Directory.EnumerateFileSystemEntries(appFolder).Any())
                    Directory.Delete(appFolder, recursive: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not empty after all */ }
        }

        return new BuildDirectorySweepResult(removed, freed, kept, failed);
    }
}
