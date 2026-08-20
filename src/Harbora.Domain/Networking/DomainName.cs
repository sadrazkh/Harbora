using Harbora.Domain.Common;
using Harbora.Domain.Apps;
using Harbora.Domain.Status;

namespace Harbora.Domain.Networking;

/// <summary>
/// A hostname bound to either an app or a workspace's status page, optionally with automatic Let's
/// Encrypt SSL. The same row shape and the same attach flow (diagnosis, certificate, Traefik route)
/// serve both owners — see <see cref="StatusPage"/> (sub-project 8, 2026-08-20 platform-options
/// plan): a status page's custom domain is not a second machinery next to an app's, it is this one
/// pointed at a different backend.
///
/// <para>
/// Exactly one of <see cref="AppId"/>/<see cref="StatusPageId"/> is set, never both and never
/// neither — enforced by the two call sites that create a row (<c>AppsController.AddDomain</c>,
/// <c>AppAddressAssigner.AssignAsync</c> for the app side; <c>StatusPageDomainService</c> for the
/// status-page side), not by a database constraint, the same way every other "which of two owners"
/// row in this codebase relies on its writers rather than a CHECK.
/// </para>
/// </summary>
public class DomainName : BaseEntity
{
    public Guid? AppId { get; set; }
    public App? App { get; set; }

    /// <summary>Set only for a workspace's status-page custom domain (sub-project 8). Null for every
    /// app domain, which is every row before this sub-project existed.</summary>
    public Guid? StatusPageId { get; set; }
    public StatusPage? StatusPage { get; set; }

    public string Host { get; set; } = string.Empty;   // e.g. app.example.com
    public bool SslEnabled { get; set; } = true;
    public bool ForceHttps { get; set; } = true;
    public bool IsPrimary { get; set; }

    public Guid? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }
}
