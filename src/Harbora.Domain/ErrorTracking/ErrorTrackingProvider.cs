using Harbora.Domain.Common;

namespace Harbora.Domain.ErrorTracking;

/// <summary>
/// A workspace's own Sentry-compatible DSN, used to inject <c>SENTRY_DSN</c> into the apps attached
/// to it (1.8, 2026-09 market-gaps round two).
///
/// <para>
/// Bring-your-own, the same shape <see cref="Harbora.Domain.Email.EmailProvider"/> already is for
/// SMTP: Harbora stores the DSN and hands it to an attached app as an env var, and there is no relay
/// or proxy in front of it — the app's own container speaks the Sentry protocol straight to whatever
/// answers the DSN's host. That host can be a GlitchTip instance this workspace deployed from the
/// one-click template (<c>ReadyAppCatalog</c>'s "sentry" entry — itself an ordinary
/// <c>App</c> plus the <c>ManagedService</c> Postgres/Redis it declares in <c>requires</c>, which
/// already get generated credentials, billing and full start/stop/rebuild lifecycle through
/// <c>TemplateDeploymentService</c>; nothing here re-provisions that), or it can be a project on
/// Sentry SaaS, or any other Sentry-API-compatible server — this row does not care which, and neither
/// does the attach/inject path below.
/// </para>
///
/// <para>
/// Why not a <see cref="Harbora.Domain.Common.ManagedServiceType"/> entry instead, the way RabbitMQ
/// and NATS are: every existing entry in that catalogue is a single, self-contained container that
/// needs nothing but credentials Harbora itself generates. GlitchTip is not — it is a Django
/// application that will not start without its own PostgreSQL and Redis, and
/// <c>ManagedServiceEngine</c> has no notion of one service depending on another. Forcing it into that
/// single-container shape would mean either inventing a working link to services it was never told
/// about (a container that reports Running while it cannot actually reach a database — the exact
/// "reports success for work it never did" defect this codebase forbids), or leaving the link
/// unresolved and shipping something that cannot boot. The one-click template already solves the
/// real dependency correctly (<c>requires: ["postgres","redis"]</c>, each provisioned as its own real,
/// billed, backed-up <c>ManagedService</c>); what it never had was a way to hand its DSN to another
/// app. That is the gap this type closes.
/// </para>
/// </summary>
public class ErrorTrackingProvider : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>What the workspace calls it — "GlitchTip (production)", "Sentry SaaS" — shown
    /// instead of the DSN's host wherever a person picks one.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The full DSN (<c>https://&lt;public-key&gt;@&lt;host&gt;/&lt;project-id&gt;</c>), encrypted at
    /// rest with the platform key like every other stored credential. Unlike
    /// <see cref="Harbora.Domain.Email.EmailProvider"/>'s host/port/username, a DSN has no plaintext
    /// half worth splitting out — the public key is part of the same opaque string a Sentry/GlitchTip
    /// SDK expects verbatim, so it is kept, and revealed, as one value rather than several fields.
    /// </summary>
    public string EncryptedDsn { get; set; } = string.Empty;

    public ICollection<AppErrorTrackingProvider> Apps { get; set; } = new List<AppErrorTrackingProvider>();
}
