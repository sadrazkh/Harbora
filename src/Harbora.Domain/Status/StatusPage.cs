using Harbora.Domain.Common;

namespace Harbora.Domain.Status;

/// <summary>
/// A workspace's public status page (P7, 2026-08-20 platform-options plan, "Public status page on a
/// platform subdomain"). At most one per workspace, created lazily the first time a workspace owner
/// opens the settings screen — its mere existence is not publication; <see cref="IsEnabled"/> is.
///
/// <para>
/// <b>Opt-in only.</b> A row with <see cref="IsEnabled"/> false answers every anonymous request the
/// same way a workspace with no row at all does: not found. Nothing is public until a customer says
/// so, and nothing about which apps appear is inferred — see <see cref="StatusPageComponent"/>.
/// </para>
///
/// <para>
/// The address this page answers on is never stored here: it is derived at request time as
/// <c>status-{Workspace.Slug}.{platform root domain}</c>, the same way an app's own address is
/// derived from its slug rather than persisted redundantly. <c>ReservedHosts.IsReservedPrefix</c>
/// keeps an app from ever being assigned that same host.
/// </para>
/// </summary>
public class StatusPage : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>Whether this page answers anonymous requests at all. Off by default.</summary>
    public bool IsEnabled { get; set; }

    public ICollection<StatusPageComponent> Components { get; set; } = new List<StatusPageComponent>();
    public ICollection<StatusIncident> Incidents { get; set; } = new List<StatusIncident>();
}

/// <summary>
/// One app a workspace has chosen to show on its status page, and the name it should be shown under.
///
/// <para>
/// <see cref="DisplayName"/> exists so the page never has to fall back to the app's real slug or
/// hostname — the plan is explicit that nothing beyond what the customer chose to call the component
/// is shown. A row with no display name set yet is not rendered until one is given (see
/// <c>StatusPageReport</c>), the same "not configured" honesty every other unmeasured value in this
/// codebase gets.
/// </para>
/// </summary>
public class StatusPageComponent : BaseEntity
{
    /// <summary>
    /// Denormalised from the parent page for the same reason <c>EventDelivery.WorkspaceId</c> is: the
    /// anonymous status-page request has no session to derive a workspace from, so every read it makes
    /// scopes explicitly with <c>IgnoreQueryFilters()</c> + <c>WorkspaceId ==</c> rather than trusting
    /// a join through a filtered parent.
    /// </summary>
    public Guid WorkspaceId { get; set; }

    public Guid StatusPageId { get; set; }
    public Guid AppId { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Display order on the public page, chosen explicitly rather than inferred from creation order.</summary>
    public int SortOrder { get; set; }
}

/// <summary>
/// A short, customer-authored, bilingual note — "we know, we're on it" — posted against a workspace's
/// status page and later resolved. This is the only human-authored signal on the page; every other
/// state is computed from <see cref="Domain.Apps.App.Status"/> and <c>LifecycleHistory</c>, never
/// typed by anyone.
/// </summary>
public class StatusIncident : BaseEntity
{
    /// <summary>Denormalised from the parent page — see <see cref="StatusPageComponent.WorkspaceId"/>.</summary>
    public Guid WorkspaceId { get; set; }

    public Guid StatusPageId { get; set; }

    public string TitleEn { get; set; } = string.Empty;
    public string TitleFa { get; set; } = string.Empty;

    public string? BodyEn { get; set; }
    public string? BodyFa { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Null while the incident is open. Resolving never deletes the row — the page's own history.</summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    public Guid CreatedByUserId { get; set; }
}
