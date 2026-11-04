using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Jobs;
using Harbora.Domain.Launch;
using Harbora.Infrastructure.Apps;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Infrastructure.Launch;

/// <summary>
/// See <see cref="ILaunchService"/>. Starting and reading the source are here; the proposal, the
/// confirm and the tick each have a file of their own.
///
/// <para>
/// <b>No launch row stays tracked.</b> Every write loads the row, changes it, saves and lets go
/// (<see cref="UpdateAsync"/>), because this service shares a context with the ones it calls.
/// <c>DatabaseProvisioningService</c> clears the whole change tracker when a wallet refuses, and each
/// of them commits whatever is pending in its own save, so a row held across one of those calls would
/// either be silently forgotten or be written as a side effect of somebody else's create.
/// </para>
///
/// <para>
/// <see cref="IAppVerifier"/> is resolved from the scope only when a launch needs it, because a launch
/// that has not reached its checks has no use for one.
/// </para>
/// </summary>
public sealed partial class LaunchService(
    HarboraDbContext db,
    IAppProvisioningService apps,
    IDatabaseProvisioningService databases,
    IDatabaseAttachmentService attachments,
    IAppConfigurationService configuration,
    IDeploymentEngine deployments,
    IManagedServiceEngine engine,
    IGitService git,
    IJobQueue jobs,
    ISecretProtector protector,
    ISecretRedactor redactor,
    IAuditLogger audit,
    ICurrentUser currentUser,
    ISystemClock clock,
    ProjectAccessService access,
    ISchedulerService scheduler,
    IQuotaService quota,
    INodeCapacityService capacity,
    ResourceCreationBilling creationBilling,
    IOptions<HarboraRuntimeOptions> runtime,
    IOptions<BillingOptions> billing,
    ILaunchSourceReader reader,
    IServiceProvider serviceProvider,
    ILogger<LaunchService> logger) : ILaunchService
{
    private const string AuditTarget = "launch";

    /// <summary>The column's own limit (<c>HarboraDbContext</c>): past it Postgres refuses the whole save.</summary>
    private const int MaxMessageLength = 2000;
    private const int MaxRefLength = 255;
    private const int MaxArchivePathLength = 1024;

    /// <summary>How many levels of "a folder of folders of apps" are followed. A repository deeper than this is one nobody chooses from a list.</summary>
    private const int MaxCandidateDepth = 4;

    private static readonly ServiceRefusal NotFound = new(
        "This launch does not exist.", "این راه‌اندازی وجود ندارد.");

    private static readonly ServiceRefusal NotTheCaller = new(
        "This request names a workspace or user other than the one signed in.",
        "این درخواست به فضای کاری یا کاربری غیر از کاربرِ واردشده اشاره می‌کند.");

    // ---- starting ------------------------------------------------------------------------------

    public async Task<ServiceOutcome<Guid>> StartAsync(LaunchStartRequest request, CancellationToken ct)
    {
        // The services a confirm goes through judge the signed-in user, so one named here who is not
        // them would be refused a step later. Said now, with nothing yet written.
        if (currentUser.WorkspaceId != request.WorkspaceId || currentUser.UserId != request.UserId)
            return ServiceOutcome<Guid>.Refused(NotTheCaller);

        var suspended = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == request.WorkspaceId).Select(w => (bool?)w.IsSuspended).FirstOrDefaultAsync(ct);
        if (suspended is null) return ServiceOutcome<Guid>.Refused(NotFound);
        if (suspended.Value)
            return ServiceOutcome<Guid>.Refused(new ServiceRefusal(
                "This workspace is suspended, so nothing can be launched in it.",
                "این فضای کاری معلق است و چیزی در آن راه‌اندازی نمی‌شود."));

        var place = await PlacementAsync(request.WorkspaceId, ct);
        if (!place.Allowed) return ServiceOutcome<Guid>.Refused(AppRefusals.EnvironmentNotAllowed);

        string? cloneUrl = null, token = null, gitRef = null, archivePath = null;
        switch (request.Source)
        {
            case LaunchSourceKind.Git:
                var reading = LaunchSource.ReadCloneUrl(request.CloneUrl);
                if (reading.Refusal is { } refusal) return ServiceOutcome<Guid>.Refused(refusal);
                cloneUrl = reading.CloneUrl;
                token = string.IsNullOrWhiteSpace(request.GitToken) ? reading.EmbeddedToken : request.GitToken.Trim();
                gitRef = string.IsNullOrWhiteSpace(request.GitRef) ? null : request.GitRef.Trim();
                if (gitRef?.Length > MaxRefLength)
                    return ServiceOutcome<Guid>.Refused(new ServiceRefusal(
                        "That branch name is too long.", "نام این شاخه بیش از حد بلند است.", nameof(LaunchStartRequest.GitRef)));
                break;

            case LaunchSourceKind.Upload:
                archivePath = ArchiveUnderUploads(request.ArchivePath);
                if (archivePath is null)
                    return ServiceOutcome<Guid>.Refused(new ServiceRefusal(
                        "The uploaded file could not be found. Please upload it again.",
                        "فایل آپلودشده پیدا نشد. لطفاً دوباره آپلود کنید.", nameof(LaunchStartRequest.ArchivePath)));
                break;

            default:
                return ServiceOutcome<Guid>.Refused(new ServiceRefusal(
                    "Give a repository URL or an archive.", "آدرس مخزن یا یک فایل فشرده بدهید."));
        }

        var now = clock.UtcNow;
        var launch = new AppLaunch
        {
            WorkspaceId = request.WorkspaceId,
            UserId = request.UserId,
            SourceKind = request.Source,
            Status = LaunchStatus.Inspecting,
            StatusChangedAt = now,
            CloneUrl = cloneUrl,
            GitRef = gitRef,
            EncryptedGitToken = string.IsNullOrEmpty(token) ? null : protector.Protect(token),
            ArchivePath = archivePath
        };
        db.AppLaunches.Add(launch);
        await db.SaveChangesAsync(ct);
        db.Entry(launch).State = EntityState.Detached;

        await audit.LogAsync("launch.started", AuditTarget, launch.Id.ToString(), request.ClientIp,
            metadataJson: JsonSerializer.Serialize(new
            {
                source = request.Source.ToString(),
                url = cloneUrl,
                gitRef,
                archive = archivePath is null ? null : LaunchSource.LabelForArchive(archivePath)
            }),
            workspaceId: request.WorkspaceId, ct: ct);

        await jobs.EnqueueAsync(JobKind.LaunchInspect, launch.Id, request.WorkspaceId, ct);
        return ServiceOutcome<Guid>.Success(launch.Id);
    }

    /// <summary>The full path, when it is an existing file in the upload directory; a launch later deletes this path.</summary>
    private string? ArchiveUnderUploads(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var uploads = Path.GetFullPath(Path.Combine(runtime.Value.WorkDir, "_uploads")) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.Length <= MaxArchivePathLength && full.StartsWith(uploads, StringComparison.Ordinal) && File.Exists(full)
            ? full
            : null;
    }

    /// <summary>
    /// Where this person may put an app: the workspace's default environment when they may, else the
    /// first one they may. The same order <c>ProjectService.ResolveEnvironmentAsync</c> reads, without
    /// its side effect: for a workspace with no environment at all that method makes the default
    /// project, and nothing here may exist before the confirm. Such a workspace gets one at the first
    /// create, and <see cref="Placement.Environment"/> stays null until then.
    /// </summary>
    private async Task<Placement> PlacementAsync(Guid workspaceId, CancellationToken ct)
    {
        var environments = await db.Environments.AsNoTracking()
            .Where(e => e.WorkspaceId == workspaceId)
            .OrderByDescending(e => e.IsDefault).ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

        if (environments.Count == 0)
            return new Placement(null, await access.AllowsAsync(new ResourcePlacement(null, null), Capabilities.AppsCreate, ct));

        foreach (var environment in environments)
            if (await access.AllowsAsync(new ResourcePlacement(environment.ProjectId, environment.Id), Capabilities.AppsCreate, ct))
                return new Placement(environment, true);

        return new Placement(null, false);
    }

    private sealed record Placement(Environment? Environment, bool Allowed);

    // ---- reading the source --------------------------------------------------------------------

    public async Task InspectAsync(Guid launchId, CancellationToken ct)
    {
        // Unfiltered: the job worker has no session. Safe to run twice: a crashed job is claimed again.
        var launch = await db.AppLaunches.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null || launch.Status != LaunchStatus.Inspecting) return;

        var work = WorkDirFor(launchId);
        var token = TokenOf(launch);
        try
        {
            string root;
            string? branch = null;
            if (launch.SourceKind == LaunchSourceKind.Git)
            {
                // No log is kept of the clone: its progress lines are of no use to anybody here, and a
                // line that is never written cannot be how a token reaches a log file.
                var checkout = await git.CheckoutAsync(
                    launch.CloneUrl!, launch.GitRef ?? "", token, work, new InlineProgress<string>(_ => { }), ct);
                root = checkout.LocalPath;
                branch = checkout.Branch ?? launch.GitRef;
            }
            else
            {
                DeleteDirectory(work);
                await using var stream = File.OpenRead(launch.ArchivePath!);
                await SourceArchive.ExtractAsync(stream, work, ct);
                root = work;
            }

            var stored = ReadTree(root, launch.GitRef, branch);

            await UpdateAsync(launchId, l =>
            {
                l.InspectionJson = LaunchJson.Write(stored);
                l.ResolvedBranch = branch is { Length: <= MaxRefLength } ? branch : null;
            }, ct);
            await LaunchMoves.TryMoveAsync(db, launchId, LaunchStatus.Inspecting, LaunchStatus.AwaitingConfirmation, clock.UtcNow, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The worker's own deadline, or the panel going down: its row is what settles this, and the
            // tick writes the launch off if the job never comes back.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Launch {LaunchId} could not read its source: {Reason}", launchId, Scrub(ex.Message, token));
            var (message, messageFa) = launch.SourceKind == LaunchSourceKind.Git
                ? CloneFailure(ex, token)
                : ArchiveFailure(ex);
            await EndInspectionAsync(launchId, message, messageFa, CancellationToken.None);
        }
        finally
        {
            // Always: a source tree can be large and holds whatever the repository holds.
            DeleteDirectory(work);
        }
    }

    private string WorkDirFor(Guid launchId) => Path.Combine(runtime.Value.WorkDir, "_launch", launchId.ToString());

    /// <summary>
    /// The tree, and every folder in it a person might choose: this is the last moment any of them can
    /// be read. A folder that turns out to hold several apps of its own is replaced by those apps, so
    /// the list a person chooses from never leads to another list.
    /// </summary>
    private StoredInspection ReadTree(string root, string? asked, string? got)
    {
        var top = reader.Read(root);

        var candidates = new Dictionary<string, SourceInspection>(StringComparer.Ordinal);
        foreach (var candidate in top.RootCandidates)
            ReadCandidate(root, candidate, candidates, depth: 1);

        return new StoredInspection(
            WithLaunchNotes(top with { RootCandidates = candidates.Keys.Order(StringComparer.Ordinal).ToList() }, root, asked, got),
            candidates.ToDictionary(c => c.Key, c => WithLaunchNotes(c.Value, root, asked, got), StringComparer.Ordinal));
    }

    private void ReadCandidate(string root, string candidate, Dictionary<string, SourceInspection> into, int depth)
    {
        var found = reader.Read(Path.Combine(root, candidate));

        if (found.RootCandidates.Count > 0 && depth < MaxCandidateDepth)
        {
            foreach (var inner in found.RootCandidates)
                ReadCandidate(root, $"{candidate}/{inner}", into, depth + 1);
            return;
        }

        // The inspector answered about this folder, so its answer is relative to this folder, and the
        // pipeline will read it from the top of the tree.
        into[candidate] = found with
        {
            RootDirectory = string.IsNullOrWhiteSpace(found.RootDirectory) ? candidate : $"{candidate}/{found.RootDirectory}",
            RootCandidates = []
        };
    }

    /// <summary>
    /// What only this step can see, because the tree is gone afterwards. An <c>artisan</c> file is what
    /// says an <c>APP_KEY</c> must be in Laravel's own format, and a ref that was not there means the
    /// clone quietly read the default branch instead.
    /// </summary>
    private static SourceInspection WithLaunchNotes(SourceInspection inspection, string root, string? asked, string? got)
    {
        var notes = inspection.Notes.ToList();

        var appRoot = string.IsNullOrWhiteSpace(inspection.RootDirectory) ? root : Path.Combine(root, inspection.RootDirectory);
        if (File.Exists(Path.Combine(appRoot, "artisan"))
            && inspection.EnvVars.Any(v => v.Name == AppKeyName && v.Disposition == EnvVarDisposition.GeneratedSecret))
            notes.Add(new InspectionNote(LaravelAppKeyNote,
                "This looks like Laravel, so APP_KEY will be generated in the format it expects.",
                "این پروژه شبیه Laravel است، پس APP_KEY در قالبی که لازم دارد ساخته می‌شود."));

        if (!string.IsNullOrWhiteSpace(asked) && got is not null && !string.Equals(asked, got, StringComparison.Ordinal))
            notes.Add(new InspectionNote("ref-not-found",
                $"The repository has no branch or tag called '{asked}', so this was read from '{got}'.",
                $"مخزن شاخه یا تگی به نام «{asked}» ندارد، پس از «{got}» خوانده شد."));

        return inspection with { Notes = notes };
    }

    private (string Message, string MessageFa) CloneFailure(Exception ex, string? token)
    {
        var reason = LogText.Clean(Scrub(ex.Message, token)).Trim();
        return ($"Could not read this repository. The URL may be wrong, or the repository may be private: if it is, add an access token and try again. Nothing was created. ({reason})",
            $"این مخزن خوانده نشد. شاید آدرس اشتباه باشد یا مخزن خصوصی باشد؛ اگر خصوصی است، توکن دسترسی را هم بدهید و دوباره امتحان کنید. چیزی ساخته نشد. ({reason})");
    }

    /// <summary>
    /// <see cref="SourceArchive"/> words its own refusals for a person. Anything else, a file gone from
    /// the disk say, would be quoting the panel's own paths at them, so it only gets the sentence.
    /// </summary>
    private static (string Message, string MessageFa) ArchiveFailure(Exception ex)
    {
        var reason = ex is InvalidOperationException ? LogText.Clean(ex.Message).Trim() + " " : "";
        return ($"Could not read this file. {reason}Zip the project's folder and upload it again. Nothing was created.",
            $"این فایل خوانده نشد؛ خالی است یا zip و tar.gz معتبری نیست. پوشهٔ پروژه را zip کنید و دوباره آپلود کنید. چیزی ساخته نشد.");
    }

    /// <summary>
    /// An end with nothing created: the form comes back for another go, which is a new launch, so
    /// neither the token nor the archive has any further use.
    /// </summary>
    private async Task EndInspectionAsync(Guid launchId, string message, string messageFa, CancellationToken ct)
    {
        string? archive = null;
        await UpdateAsync(launchId, l =>
        {
            if (l.Status != LaunchStatus.Inspecting) return;
            archive = l.ArchivePath;
            l.Status = LaunchStatus.InspectFailed;
            l.StatusChangedAt = clock.UtcNow;
            l.FailedStep = LaunchStepKeys.Source;
            l.FailureMessage = Clip(message);
            l.FailureMessageFa = Clip(messageFa);
            l.EncryptedGitToken = null;
            l.ArchivePath = null;
        }, ct);
        DeleteArchive(archive);
    }

    // ---- giving up -----------------------------------------------------------------------------

    public async Task<bool> AbandonAsync(Guid launchId, CancellationToken ct)
    {
        var launch = await db.AppLaunches.AsNoTracking().FirstOrDefaultAsync(l => l.Id == launchId, ct);
        if (launch is null) return false;
        if (launch.Status == LaunchStatus.Abandoned) return true;

        var nothingCreated = launch.AppId is null && LaunchJson.ReadIds(launch.ServiceIdsJson).Count == 0;
        var mayAbandon = launch.Status is LaunchStatus.Inspecting or LaunchStatus.InspectFailed or LaunchStatus.AwaitingConfirmation
            || (launch.Status == LaunchStatus.Failed && nothingCreated);
        if (!mayAbandon) return false;

        // Conditional: losing to a confirm in the same instant must not delete the archive it is using.
        if (!await LaunchMoves.TryMoveAsync(db, launchId, launch.Status, LaunchStatus.Abandoned, clock.UtcNow, ct))
            return false;

        if (launch.Status == LaunchStatus.Inspecting)
            await jobs.RequestCancellationAsync(JobKind.LaunchInspect, launchId, ct);

        await DropLeftoversAsync(launchId, ct);
        return true;
    }

    /// <summary>Clears the token and deletes the archive, whatever else the launch is doing.</summary>
    private async Task DropLeftoversAsync(Guid launchId, CancellationToken ct, Action<AppLaunch>? also = null)
    {
        string? archive = null;
        await UpdateAsync(launchId, l =>
        {
            also?.Invoke(l);
            archive = l.ArchivePath;
            l.ArchivePath = null;
            l.EncryptedGitToken = null;
        }, ct);
        DeleteArchive(archive);
    }

    // ---- shared --------------------------------------------------------------------------------

    /// <summary>Loads the row, changes it, saves, and lets go of it. See the type's doc for why it lets go.</summary>
    private async Task UpdateAsync(Guid launchId, Action<AppLaunch> change, CancellationToken ct)
    {
        var row = await db.AppLaunches.IgnoreQueryFilters().FirstAsync(l => l.Id == launchId, ct);
        change(row);
        await db.SaveChangesAsync(ct);
        db.Entry(row).State = EntityState.Detached;
    }

    private string? TokenOf(AppLaunch launch)
    {
        if (string.IsNullOrEmpty(launch.EncryptedGitToken)) return null;
        try { return protector.Unprotect(launch.EncryptedGitToken); }
        catch (Exception ex)
        {
            // A key rotated since it was typed. Read on with none: a public repository still works, and
            // a private one fails with the sentence that asks for a token.
            logger.LogWarning(ex, "The stored token on a launch could not be decrypted.");
            return null;
        }
    }

    /// <summary>Removes the token from text bound for a message or a log, in both spellings a URL could carry it.</summary>
    private string Scrub(string text, string? token) => string.IsNullOrEmpty(token)
        ? text
        : redactor.Redact(text, [token, Uri.EscapeDataString(token)]);

    private static string? Clip(string? text) =>
        text is { Length: > MaxMessageLength } ? text[..MaxMessageLength] : text;

    private void DeleteArchive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { logger.LogWarning(ex, "A launch's uploaded archive could not be deleted."); }
    }

    private void DeleteDirectory(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;

            // .git holds read-only files on Windows, and a delete refuses those.
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "A launch's working folder could not be deleted.");
        }
    }
}
