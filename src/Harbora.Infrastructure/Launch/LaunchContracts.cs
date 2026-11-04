using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Launch;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Verification;
using Harbora.Shared.Inspection;

namespace Harbora.Infrastructure.Launch;

/// <summary>Where to read the code from. Exactly one of <see cref="CloneUrl"/> and <see cref="ArchivePath"/>, as <see cref="Source"/> says.</summary>
public sealed record LaunchStartRequest
{
    public required Guid WorkspaceId { get; init; }
    public required Guid UserId { get; init; }
    public required LaunchSourceKind Source { get; init; }
    public string? CloneUrl { get; init; }

    /// <summary>Blank reads the repository's default branch.</summary>
    public string? GitRef { get; init; }

    /// <summary>Stored encrypted; never printed by <see cref="ToString"/>.</summary>
    public string? GitToken { get; init; }

    /// <summary>An archive the caller has already written under the runtime's upload directory. Ownership passes to the launch.</summary>
    public string? ArchivePath { get; init; }

    public string? ClientIp { get; init; }

    /// <summary>A record prints every property by default, and this one carries an access token.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"WorkspaceId = {WorkspaceId}, Source = {Source}, CloneUrl = {CloneUrl}, GitRef = {GitRef}");
        return true;
    }
}

/// <summary>One database or broker to create and attach.</summary>
/// <param name="Name">Blank derives one from the app's name.</param>
/// <param name="Version">Blank takes the catalogue's newest offered version.</param>
/// <param name="Aliases">Extra names the app reads this service's values under, from the example env file.</param>
public sealed record LaunchServiceChoice(
    ManagedServiceType Type,
    string? Name = null,
    string? Version = null,
    IReadOnlyList<AttachEnvAlias>? Aliases = null);

/// <summary>One variable to set on the app.</summary>
/// <param name="Value">Null generates a random secret for a <c>GeneratedSecret</c> name; otherwise it is the person's answer.</param>
public sealed record LaunchEnvChoice(string Key, string? Value, bool IsSecret = false);

/// <summary>
/// What the person agreed to on the confirm screen. Serialised onto the launch for a retry, with every
/// <see cref="LaunchEnvChoice.Value"/> left out: those live on the app's own environment rows once set.
/// </summary>
public sealed record LaunchChoices
{
    public required string Name { get; init; }
    public string? Slug { get; init; }
    public ServiceKind Kind { get; init; } = ServiceKind.Web;
    public int ContainerPort { get; init; } = 80;
    public string? Command { get; init; }
    public string? ReleaseCommand { get; init; }
    public string? RootDirectory { get; init; }
    public Guid? EnvironmentId { get; init; }

    /// <summary>Null lets the scheduler choose. Everything in one launch lands on one server: an app and its database must share a host.</summary>
    public Guid? ServerId { get; init; }

    public string? InstanceSizeKey { get; init; }
    public IReadOnlyList<LaunchServiceChoice> Services { get; init; } = [];
    public IReadOnlyList<LaunchEnvChoice> EnvVars { get; init; } = [];
}

/// <summary>One variable the confirm screen shows.</summary>
/// <param name="SuggestedValue">Pre-filled for a <c>KeepExample</c>; null for everything else.</param>
public sealed record LaunchEnvField(string Key, EnvVarDisposition Disposition, string? SuggestedValue, bool IsSecret, string SourceFile);

/// <summary>A service the screen can offer, whether or not it was detected.</summary>
/// <param name="Detected">Null when nothing in the source suggested it.</param>
public sealed record LaunchServiceOption(ManagedServiceType Type, string DisplayName, string DisplayNameFa, DetectedService? Detected);

/// <summary>What the confirm screen is drawn from. Nothing here has been created.</summary>
/// <param name="Defaults">The form's initial values — the inspection turned into choices.</param>
/// <param name="AppName">Derived from the repository or the archive; what <see cref="LaunchChoices.Name"/> starts as.</param>
public sealed record LaunchProposal(
    Guid LaunchId,
    SourceInspection Inspection,
    LaunchChoices Defaults,
    string AppName,
    IReadOnlyList<LaunchEnvField> EnvFields,
    IReadOnlyList<LaunchServiceOption> ServiceOptions);

/// <param name="HourlyMinor">Null when this resource has no price yet, which creation refuses.</param>
public sealed record LaunchQuoteLine(string Name, string NameFa, long? HourlyMinor);

/// <summary>
/// What confirming will cost, read through the function that charges it rather than a second copy of
/// the rule. The one place in this flow a bill can surprise somebody, so it is shown before the button.
/// </summary>
/// <param name="FirstHourMinor">What leaves the balance the moment this is confirmed.</param>
/// <param name="BalanceMinor">The workspace's balance, or null when there is no wallet to read.</param>
/// <param name="Exempt">Billing is off, or this is the provider's own workspace: nothing is charged.</param>
/// <param name="Refusal">Why this could not be paid for, already in both languages. Null when it can.</param>
public sealed record LaunchQuote(
    IReadOnlyList<LaunchQuoteLine> Lines,
    long FirstHourMinor,
    long? BalanceMinor,
    string Currency,
    bool Exempt,
    ServiceRefusal? Refusal);

public enum LaunchStepState
{
    Waiting = 0,
    Running = 1,
    Done = 2,
    Failed = 3,
    Skipped = 4
}

/// <summary>The steps a launch walks, as stable keys. What <see cref="AppLaunch.FailedStep"/> and the timeline both use.</summary>
public static class LaunchStepKeys
{
    public const string Source = "source";
    public const string Services = "services";
    public const string App = "app";
    public const string Deploy = "deploy";
    public const string Verify = "verify";
}

/// <param name="LinkPath">Where to look closer, a site-relative path: the deployment's live log, the database's page.</param>
public sealed record LaunchStepView(
    string Key,
    string Title,
    string TitleFa,
    LaunchStepState State,
    string? Detail = null,
    string? DetailFa = null,
    string? LinkPath = null);

/// <summary>A launch as a page or the API shows it. Never carries a token or a value.</summary>
/// <param name="SourceLabel">The URL with any credentials stripped, or the archive's name.</param>
/// <param name="PublicUrl">The address to open, once there is one.</param>
/// <param name="CanRetry">A step failed and running it again could help.</param>
public sealed record LaunchView(
    Guid Id,
    LaunchStatus Status,
    LaunchSourceKind Source,
    string SourceLabel,
    Guid? AppId,
    string? AppName,
    string? PublicUrl,
    Guid? DeploymentId,
    string? FailureMessage,
    string? FailureMessageFa,
    IReadOnlyList<LaunchStepView> Steps,
    IReadOnlyList<AppCheck> Checks,
    bool CanRetry,
    bool IsTerminal);

/// <summary>
/// "Here is my code, make it work": read the source, propose what it needs, and once the person
/// agrees create it, connect it, deploy it and prove it. Every create goes through the same service
/// the forms use (<c>IAppProvisioningService</c>, <c>IDatabaseProvisioningService</c>,
/// <c>IDatabaseAttachmentService</c>, <c>IAppConfigurationService</c>); nothing here is a second way
/// of making an app.
/// </summary>
public interface ILaunchService
{
    /// <summary>Records the launch and queues the inspection. The source is not read on the request.</summary>
    Task<ServiceOutcome<Guid>> StartAsync(LaunchStartRequest request, CancellationToken ct);

    /// <summary>Fetches and reads the source. Called by the job worker: no session, and safe to run twice.</summary>
    Task InspectAsync(Guid launchId, CancellationToken ct);

    /// <summary>Null when it does not exist or is not this workspace's.</summary>
    Task<LaunchView?> GetAsync(Guid launchId, CancellationToken ct);

    /// <summary>
    /// Null unless the launch is <see cref="LaunchStatus.AwaitingConfirmation"/>. While a repository
    /// holding several apps has no folder chosen, <c>Inspection.RootDirectory</c> is null and
    /// <c>Inspection.RootCandidates</c> is not empty: the screen asks that one question and offers nothing
    /// else, because nothing it could propose yet is about the app the person means.
    /// </summary>
    Task<LaunchProposal?> ProposeAsync(Guid launchId, CancellationToken ct);

    /// <summary>
    /// Settles which folder of a several-app repository is being launched, and returns the proposal for
    /// it. Reads what <see cref="InspectAsync"/> stored for each candidate while the tree still existed:
    /// the tree is deleted after the inspection, so a folder picked later could otherwise never have been
    /// read, and the launch would go ahead with no database, no variables and the wrong port. Can be
    /// called again to change the answer, up to the moment it is confirmed. An unknown folder is refused.
    /// </summary>
    Task<ServiceOutcome<LaunchProposal>> ChooseRootAsync(Guid launchId, string candidate, CancellationToken ct);

    Task<LaunchQuote> QuoteAsync(Guid launchId, LaunchChoices choices, CancellationToken ct);

    /// <summary>The refusals <see cref="ConfirmAsync"/> would give right now. Changes nothing.</summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateConfirmAsync(Guid launchId, LaunchChoices choices, CancellationToken ct);

    /// <summary>
    /// Creates the databases, the app, the attachments and the variables, on the request, because each
    /// of those services judges the signed-in user. Then the tick takes over. A step refused part-way
    /// leaves the launch <see cref="LaunchStatus.Failed"/> with what exists named on the row.
    /// </summary>
    Task<ServiceOutcome<LaunchView>> ConfirmAsync(Guid launchId, LaunchChoices choices, string? clientIp, CancellationToken ct);

    /// <summary>Runs the failed step again, creating only what does not exist yet.</summary>
    Task<ServiceOutcome<LaunchView>> RetryAsync(Guid launchId, string? clientIp, CancellationToken ct);

    /// <summary>Gives up before anything was created, and deletes any uploaded archive. False when it is too late for that.</summary>
    Task<bool> AbandonAsync(Guid launchId, CancellationToken ct);

    /// <summary>
    /// One pass over every launch in flight: moves each as far as it can without waiting, and returns
    /// how many it moved. Public because it is the whole behaviour of the tick. No session.
    /// </summary>
    Task<int> AdvanceAllAsync(CancellationToken ct);
}
