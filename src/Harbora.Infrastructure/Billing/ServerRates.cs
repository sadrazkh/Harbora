using Harbora.Domain.Billing;
using Harbora.Domain.Servers;
using Harbora.Domain.Tenancy;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// What one hour of one tier costs <i>on one server</i>.
///
/// <para>
/// A thin layer over <see cref="BillingRates"/> rather than a replacement for it: the global rules —
/// each run state resolved from its own column, <c>NotApplicable</c> a real zero, and null meaning
/// "nobody priced this" rather than "free" — are stated once, there, and this class defers to them
/// for every case an override does not answer. A second copy of those rules would be free to drift,
/// and the drift would be a wrong bill.
/// </para>
///
/// <para>
/// Pure, no database, for the reason the rest of the money arithmetic is.
/// </para>
/// </summary>
public static class ServerRates
{
    /// <summary>
    /// The hourly rate for one workload on one server, or <c>null</c> if nobody has priced that tier
    /// in that state at either level.
    ///
    /// <para>
    /// The order is: what this server charges for this state, then what the tier charges for this
    /// state, then nothing.
    /// </para>
    ///
    /// <para>
    /// <b>Each state falls back on its own global column.</b> A server that sets a running rate and
    /// leaves stopped blank inherits the <i>global stopped</i> rate, never its own running one. This
    /// is the rule most easily got wrong and most expensive when it is: crossing the two would bill a
    /// stopped workload at the running price precisely where an operator had been careful.
    /// </para>
    ///
    /// <para>
    /// <b>An override is an answer, not a discount.</b> So a server may price a tier the global list
    /// never did, and may give away — at a deliberate zero — a tier the global list charges for.
    /// Neither is expressible if the override is only consulted when a global rate already exists.
    /// </para>
    ///
    /// <para>
    /// <b><see cref="ServerInstanceOffer.IsOffered"/> is not consulted here.</b> Whether a server
    /// takes new work on a tier is a question for the chooser and the scheduler; what the hour costs
    /// is this one. Conflating them would stop billing every workload already running on a withdrawn
    /// tier — see the remarks on that property.
    /// </para>
    /// </summary>
    public static long? ForWorkload(InstanceSize size, ServerInstanceOffer? offer, BilledRunState state)
    {
        // Not a workload state, so no rate column is consulted at either level and the answer is a
        // real zero. Delegated rather than restated so the volume and plan-minimum lines cannot come
        // to one figure here and another there.
        if (state is not (BilledRunState.Running or BilledRunState.Stopped))
            return BillingRates.ForWorkload(size, state);

        var overridden = state == BilledRunState.Running
            ? offer?.RunningRatePerHourMinor
            : offer?.StoppedRatePerHourMinor;

        // `??` and not a null check with a branch: the whole rule is "the server's answer if it gave
        // one, otherwise the tier's". An override of zero is an answer and survives this, which is
        // what lets a provider run a free tier on one box.
        return overridden ?? BillingRates.ForWorkload(size, state);
    }

    /// <summary>
    /// Whether this server takes new work on this tier.
    ///
    /// <para>
    /// <b>No row means yes.</b> A provider who has never opened the pricing matrix offers every tier
    /// on every server, which is what the platform did before this table existed — so the absence of
    /// a row can never be read as a refusal.
    /// </para>
    /// </summary>
    public static bool OffersNewWork(ServerInstanceOffer? offer) => offer?.IsOffered ?? true;
}
