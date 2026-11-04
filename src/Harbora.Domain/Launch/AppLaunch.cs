using Harbora.Domain.Common;

namespace Harbora.Domain.Launch;

/// <summary>Where the code came from. Persisted as an int; append only.</summary>
public enum LaunchSourceKind
{
    Git = 0,
    Upload = 1
}

/// <summary>
/// How far one launch has got. Persisted as an int; append only.
///
/// <para>
/// Everything from <see cref="WaitingForServices"/> on is moved by a background tick rather than by a
/// job holding a worker: a job that waited for a provision or a deployment to finish would occupy the
/// slot that work needs, and on an install with <c>Jobs:MaxConcurrency</c> of 1 it would wait for ever.
/// </para>
/// </summary>
public enum LaunchStatus
{
    /// <summary>Fetching the source and reading it.</summary>
    Inspecting = 0,

    /// <summary>The source could not be read. Nothing was created, and nothing was charged.</summary>
    InspectFailed = 1,

    /// <summary>The proposal is ready and nothing has been created. The only state in which it can still be edited.</summary>
    AwaitingConfirmation = 2,

    /// <summary>Confirmed; the request is creating the resources. Guards a double submit.</summary>
    Creating = 3,

    /// <summary>The databases are being provisioned. The app must not be deployed before they answer.</summary>
    WaitingForServices = 4,

    /// <summary>The first deployment is queued or running.</summary>
    Deploying = 5,

    /// <summary>Deployed; the checks are running.</summary>
    Verifying = 6,

    /// <summary>Deployed, and every check that could run passed.</summary>
    Succeeded = 7,

    /// <summary>
    /// Deployed, and at least one check failed. Separate from <see cref="Failed"/> because the app is
    /// running and being billed, and the person's next move is to read the check rather than try again.
    /// </summary>
    SucceededWithProblems = 8,

    /// <summary>A step could not finish. Whatever was created is still there and named on the row.</summary>
    Failed = 9,

    /// <summary>Left at the confirm screen and swept. Nothing was ever created.</summary>
    Abandoned = 10
}

/// <summary>
/// One run of "here is my code, make it work": the source, what was found in it, what the person
/// agreed to, and how far that has got.
///
/// <para>
/// A row rather than a request, so a page reload, a closed laptop or a panel restart in the middle
/// loses nothing. Creating resources is not repeated on a retry: the ids below are what a retry reads
/// to know what already exists.
/// </para>
/// </summary>
public class AppLaunch : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Who started it. The tick has no session, so this is the name its deployment is queued under.</summary>
    public Guid UserId { get; set; }

    public LaunchSourceKind SourceKind { get; set; }
    public LaunchStatus Status { get; set; } = LaunchStatus.Inspecting;

    /// <summary>When <see cref="Status"/> last changed. What every "stuck for too long" judgement reads.</summary>
    public DateTimeOffset StatusChangedAt { get; set; } = DateTimeOffset.UtcNow;

    public string? CloneUrl { get; set; }

    /// <summary>The ref the person named. Blank means the repository's default branch.</summary>
    public string? GitRef { get; set; }

    /// <summary>The branch the source was actually read from — what the app is created tracking, so a repository on <c>master</c> does not get a form's <c>main</c>.</summary>
    public string? ResolvedBranch { get; set; }

    /// <summary>Through <c>ISecretProtector</c>. Cleared as soon as it has been handed to the app's Git provider row, or the launch ends.</summary>
    public string? EncryptedGitToken { get; set; }

    /// <summary>The uploaded archive on disk. Consumed by the first deployment, deleted when the launch is abandoned.</summary>
    public string? ArchivePath { get; set; }

    /// <summary>A serialised <c>SourceInspection</c>.</summary>
    public string? InspectionJson { get; set; }

    /// <summary>A serialised <c>LaunchChoices</c> — what was confirmed, secrets excluded. A retry reads it.</summary>
    public string? ChoicesJson { get; set; }

    /// <summary>Set once created. Nullable, and survives the app being deleted, because the launch is still history.</summary>
    public Guid? AppId { get; set; }

    /// <summary>A JSON array of the managed-service ids this launch created.</summary>
    public string? ServiceIdsJson { get; set; }

    public Guid? DeploymentId { get; set; }

    /// <summary>When verification began, which is what its give-up time is measured from.</summary>
    public DateTimeOffset? VerifyStartedAt { get; set; }

    /// <summary>A stable key from <c>LaunchStepKeys</c>: the step that stopped it.</summary>
    public string? FailedStep { get; set; }

    public string? FailureMessage { get; set; }
    public string? FailureMessageFa { get; set; }
}

/// <summary>
/// The latest answer to "does this app really work". One row per app, overwritten by each run, so the
/// page has something to show without a table that grows.
/// </summary>
public class AppVerification : BaseEntity
{
    public Guid AppId { get; set; }

    /// <summary>Copied from the app so the workspace filter needs no join.</summary>
    public Guid WorkspaceId { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null while a check is still waiting (a certificate being issued, say).</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>The deployment these checks were run against.</summary>
    public Guid? DeploymentId { get; set; }

    /// <summary>A serialised list of <c>AppCheck</c>.</summary>
    public string ChecksJson { get; set; } = "[]";
}
