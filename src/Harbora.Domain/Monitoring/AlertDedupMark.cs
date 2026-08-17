using Harbora.Domain.Common;

namespace Harbora.Domain.Monitoring;

/// <summary>
/// One row per (subject, window) that has already fired — the persisted replacement for
/// <c>Harbora.Infrastructure.Monitoring.AlertThrottle</c>'s in-memory dictionary (N2, 2026-08-16
/// notification-system spec, "say it once, across a restart").
///
/// <para>
/// The window is baked into <see cref="Key"/> itself rather than kept as a separate column read back
/// and compared against "now" — <c>ssl:{host}:{yyyy-mm-dd}</c>, <c>disk:{server}:{bucket}</c> — so the
/// question this table answers is never "has enough time passed" (arithmetic a restart, a clock skew
/// or a race can get wrong) but only "does this exact row exist yet", which a unique index answers by
/// itself. A new window is a new key, so an old row is simply dead weight the retention sweep clears
/// rather than state anyone has to reset.
/// </para>
///
/// <para>
/// Deliberately unfiltered by EF, like <c>ContainerLifecycleCursor</c>: a mark is keyed by server or
/// host, not by workspace — several workspaces can watch the same certificate or the same node, and
/// the mark that stops the second email from going out has to be visible to both.
/// </para>
/// </summary>
public class AlertDedupMark : BaseEntity
{
    /// <summary>
    /// The subject and window, e.g. <c>ssl:example.com:2026-08-16</c> or <c>disk:&lt;serverId&gt;:12345</c>.
    /// Unique — see <c>HarboraDbContext</c>'s model configuration — which is what makes writing this
    /// row and asking "did that just fire" the same act.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// When this mark was written. Not read by the dedup check itself — the row's mere existence is
    /// the answer — but kept for the retention sweep's cutoff and for a person reading the table to
    /// ask "when did we last warn about this".
    /// </summary>
    public DateTimeOffset FiredAt { get; set; }
}
