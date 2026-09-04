using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Domain.ErrorTracking;

/// <summary>
/// An app's attachment to an <see cref="ErrorTrackingProvider"/> (1.8, 2026-09 market-gaps round
/// two). Mirrors <see cref="Harbora.Domain.Email.AppEmailProvider"/> exactly, down to the field
/// names: a provider's env var is a fixed name (<see cref="ErrorTrackingEnvKeys.Dsn"/>), so attaching
/// a second one would silently overwrite the first's value under the same key — met here with the
/// same config-groups-style merge answer F6 gave email providers (<see cref="ConfigGroupMerge"/>),
/// rather than reinvented.
/// </summary>
public class AppErrorTrackingProvider : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public Guid ErrorTrackingProviderId { get; set; }
    public ErrorTrackingProvider? ErrorTrackingProvider { get; set; }

    /// <summary>Attachment order for this app's error-tracking providers — current max + 1 when
    /// attached, never reused. Among providers, the higher order (attached later) wins on a shared
    /// key.</summary>
    public int AttachOrder { get; set; }

    /// <summary>
    /// The <see cref="Apps.AppConfigGroup.HasUnpublishedChanges"/> idiom, reused rather than
    /// reinvented. True whenever this app's running container might not carry this provider's current
    /// DSN (just attached, or the DSN edited since); cleared only when a deployment for this app
    /// succeeds and assembles the container's environment from the provider's current row.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
