using Harbora.Domain.Common;

namespace Harbora.Domain.Servers;

/// <summary>
/// What one server charges for one resource tier, and whether it offers it at all.
///
/// <para>
/// A tier's price used to be a property of the tier alone, which is right while every host is
/// interchangeable and wrong as soon as one of them is a memory-heavy box that cost twice as much.
/// This row is how a provider says "small costs more here".
/// </para>
///
/// <para>
/// <b>Keyed by the tier's key, not its id</b>, for the same reason an app and a managed service store
/// the key: it is the identifier that cannot be edited, and the hourly pass already builds its
/// dictionary of sizes from it. A row whose key matches no <c>InstanceSize</c> prices nothing and
/// harms nothing.
/// </para>
///
/// <para>
/// <b>Attached to <see cref="Server"/> rather than to <c>Node</c></b> because <c>Server</c> is what
/// placement and billing actually read — <c>App.ServerId</c> and <c>ManagedService.ServerId</c> point
/// here, and a node reaches this row through <c>Node.ServerId</c> when it is a placement target. The
/// editing screens live on the node page all the same, because <c>/servers</c> is the deprecated
/// agent and should not grow a feature.
/// </para>
/// </summary>
public class ServerInstanceOffer : BaseEntity
{
    public Guid ServerId { get; set; }

    /// <summary>The <c>InstanceSize.Key</c> this row prices.</summary>
    public string InstanceSizeKey { get; set; } = string.Empty;

    /// <summary>
    /// Whether this server takes new work on this tier.
    ///
    /// <para>
    /// <b>A withdrawal is not a repricing.</b> Anything already running on the tier goes on being
    /// charged at its proper rate; this only stops the chooser offering it and the scheduler placing
    /// on it. Reading a withdrawal as an unpriced tier would stop billing everything already there —
    /// silently, and in the platform's favour — which is the failure the nullable rate columns below
    /// exist to prevent. It is the same separation <c>Plan.IsEnabled</c> already makes for a plan
    /// withdrawn from new tenants.
    /// </para>
    /// </summary>
    public bool IsOffered { get; set; } = true;

    /// <summary>
    /// What one running hour of this tier costs on this server, or <c>null</c> to charge whatever the
    /// tier itself charges.
    ///
    /// <para>
    /// <b>Null here means "inherit", which is not what null means on <c>InstanceSize</c>.</b> There a
    /// blank says nobody has priced the tier; here it says nobody has overridden it. The two compose
    /// without ambiguity — inheriting an unpriced tier leaves it unpriced, and the pass reports it —
    /// but they read the same in a form, which is why the provider's matrix shows the inherited
    /// figure inside the box as its placeholder rather than explaining the difference underneath.
    /// </para>
    ///
    /// <para>
    /// A row that is absent altogether and a row whose rates are both null mean the same thing: this
    /// server offers the tier at the global price. That equivalence is what makes switching the
    /// feature on change nobody's bill.
    /// </para>
    /// </summary>
    public long? RunningRatePerHourMinor { get; set; }

    /// <summary>
    /// The same for an hour the workload is stopped but not deleted.
    ///
    /// <para>
    /// Resolved from its own column and inheriting its own global column, never falling back to
    /// <see cref="RunningRatePerHourMinor"/>. A server that prices running and leaves this blank
    /// would otherwise charge a stopped workload the running rate — on exactly the servers where
    /// somebody was careful enough to price one state and not the other.
    /// </para>
    /// </summary>
    public long? StoppedRatePerHourMinor { get; set; }
}
