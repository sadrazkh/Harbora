using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Apps;

/// <summary>
/// Everything creating an app needs: the app form's fields with nothing of MVC in them, plus who is
/// asking. The defaults are the form's own, so a caller that sets only a name and a source gets the app
/// the form would have made from the same two answers.
///
/// <para>
/// <see cref="EnvironmentId"/> follows <c>ProjectService.ResolveEnvironmentAsync</c>: an id that is not
/// one of this workspace's environments falls back to the workspace's default rather than failing. A
/// caller that must not land somewhere else by accident (the agent, addressing environments by name)
/// resolves the environment itself and passes an id it has seen.
/// </para>
/// </summary>
public sealed record AppCreateRequest
{
    public required Guid WorkspaceId { get; init; }

    /// <summary>Recorded as the person who queued the first deployment. Access itself is judged for the
    /// request's own signed-in user (<c>ProjectAccessService</c> reads <c>ICurrentUser</c>), which is
    /// the same person for every caller this exists for.</summary>
    public required Guid UserId { get; init; }

    public required string Name { get; init; }

    /// <summary>Optional. Blank derives one from the name and suffixes it until it is free; typed, it is
    /// normalised and refused — never silently renamed — when the platform-wide namespace holds it.</summary>
    public string? Slug { get; init; }

    public AppSourceType SourceType { get; init; } = AppSourceType.GitRepository;
    public string? CloneUrl { get; init; }
    public string? GitRef { get; init; } = "main";

    /// <summary>Stored encrypted on the Git provider row; never printed by <see cref="ToString"/>.</summary>
    public string? GitToken { get; init; }

    public string? DockerfilePath { get; init; } = "Dockerfile";
    public string? ComposeFilePath { get; init; }

    /// <summary>The sub-path the build runs from; blank is the repository root.</summary>
    public string? RootDirectory { get; init; }

    public string? PrebuiltImage { get; init; }
    public int ContainerPort { get; init; } = 80;

    /// <summary>A host somebody typed. Blank derives one from the slug under the platform's root domain.</summary>
    public string? Domain { get; init; }

    public Guid? TemplateId { get; init; }
    public Guid? EnvironmentId { get; init; }
    public ServiceKind Kind { get; init; } = ServiceKind.Web;
    public string? ReleaseCommand { get; init; }
    public string? CronExpression { get; init; }
    public bool PreviewsEnabled { get; init; }
    public string? Command { get; init; }
    public Guid? ServerId { get; init; }
    public string? InstanceSizeKey { get; init; }

    /// <summary>Queue the first deployment straight after the app is committed, as the form does.</summary>
    public bool DeployNow { get; init; } = true;

    /// <summary>What the first deployment is recorded as having been started by.</summary>
    public DeploymentTrigger Trigger { get; init; } = DeploymentTrigger.Manual;

    /// <summary>Written to the audit entry; null when there is no request to take it from.</summary>
    public string? ClientIp { get; init; }

    /// <summary>
    /// A record prints every property by default, and this one carries a Git access token. A request
    /// logged while debugging must not be how that token reaches a log file, so only the fields that
    /// say which app this is are printed.
    /// </summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"WorkspaceId = {WorkspaceId}, Name = {Name}, Slug = {Slug}, SourceType = {SourceType}, ");
        builder.Append($"Kind = {Kind}, EnvironmentId = {EnvironmentId}, DeployNow = {DeployNow}");
        return true;
    }
}

/// <summary>Something the person should be told about an app that WAS created — in both languages.</summary>
public sealed record AppNotice(string Message, string MessageFa);

/// <param name="Host">The address the app was given, or null when it has none (a worker, no root
/// domain configured, every candidate taken).</param>
/// <param name="AddressNotice">Set when the address is not the one asked for, or there is none because
/// every candidate was taken — said out loud rather than left for the person to discover.</param>
/// <param name="TemplateAdvice">What a template still needs from a person (variables it leaves blank,
/// databases it expects), in English, exactly as <c>TemplateSetup.Advice</c> words it.</param>
/// <param name="DeploymentId">The first deployment, when one was queued.</param>
/// <param name="DeployRefusal">Why the first deployment did not start although it was asked for. The app
/// itself exists either way.</param>
public sealed record AppCreateResult(
    Guid AppId,
    string Name,
    string Slug,
    string? Host,
    AppNotice? AddressNotice,
    string? TemplateAdvice,
    Guid? DeploymentId,
    ServiceRefusal? DeployRefusal);

/// <summary>
/// Creating an app — the one path the app form, and the AI agent's "create app" tool after it, both go
/// through. Before this existed the whole of it was written inline in <c>AppsController.Create</c>,
/// between ModelState calls, so nothing but a form post could create an app and the act wrote no audit
/// entry.
/// </summary>
public interface IAppProvisioningService
{
    /// <summary>
    /// Would <see cref="CreateAsync"/> refuse this, and why. Changes nothing at all: no rows, no default
    /// environment created, no lock held after it returns, no billing, no scheduling. Empty means
    /// nothing found to refuse — not a promise: the wallet is only consulted by the create itself, and
    /// the world can change in between.
    /// </summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateCreateAsync(AppCreateRequest request, CancellationToken ct);

    /// <summary>
    /// Takes the workspace's creation lock first, runs every check again inside it — nothing is trusted
    /// from an earlier validate — and creates the app exactly as the form always has: Git provider and
    /// repository rows, address, template volumes and variables, first-hour billing, an
    /// <c>app.created</c> audit entry, then the first deployment when asked for. Nothing was changed
    /// when the outcome carries refusals.
    ///
    /// <para>
    /// The lock serialises creations within one workspace, so two of them cannot both pass on a stale
    /// read of the plan, the slug or a typed host. It cannot serialise across workspaces: the two names
    /// that are unique platform-wide — the app slug and a typed host — can still be claimed by another
    /// workspace in the same instant. The database's unique indexes refuse the second insert; that
    /// surfaces as an exception from this method rather than a refusal, as it always did from the form.
    /// </para>
    /// </summary>
    Task<ServiceOutcome<AppCreateResult>> CreateAsync(AppCreateRequest request, CancellationToken ct);
}
