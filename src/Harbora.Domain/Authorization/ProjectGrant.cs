using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>
/// Permission to work on one project — or one environment inside it.
///
/// A workspace role answers <b>what</b> someone may do; this answers <b>where</b>. Until it existed,
/// anyone who could deploy could deploy everything the tenant owned, which makes "let the contractor
/// work on the marketing site" impossible to say without also handing them production.
///
/// A grant never widens what a role allows — see <see cref="ProjectAccess"/>. It only decides which
/// projects the role reaches, and may narrow the role further for one of them.
/// </summary>
public class ProjectGrant : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid UserId { get; set; }

    public Guid ProjectId { get; set; }

    /// <summary>
    /// One environment, or null for the whole project. This is how "may deploy staging, may only
    /// look at production" is expressed, which is the request behind most of these.
    ///
    /// <para>Ignored when <see cref="AppId"/> or <see cref="ServiceId"/> is set — a resource-level
    /// grant already names something narrower than any environment.</para>
    /// </summary>
    public Guid? EnvironmentId { get; set; }

    /// <summary>
    /// 5.1 (per-app and per-service grants, HARBORA-0035): narrows this grant to one named app
    /// rather than everything in <see cref="ProjectId"/>. Set together with <see cref="ProjectId"/>
    /// (the app's own project, so the WorkspaceId+ProjectId lookup in
    /// <c>ProjectAccessService.CallerAsync</c> still finds it) — never together with
    /// <see cref="ServiceId"/>. "Let the contractor work on the marketing site" without also handing
    /// them the whole project it lives in is the request this exists for; a project or environment
    /// grant still reaches the app fine on its own, and a resource-level grant is a narrowing of that,
    /// never an addition — see <c>ProjectAccess.Allows</c>.
    /// </summary>
    public Guid? AppId { get; set; }

    /// <summary>The same narrowing as <see cref="AppId"/>, for one named managed service.</summary>
    public Guid? ServiceId { get; set; }

    /// <summary>
    /// The role that applies here. Never more than the member's workspace role grants — the two are
    /// intersected, so a grant cannot be used to escalate.
    /// </summary>
    public SystemRole Role { get; set; } = SystemRole.Member;
}
