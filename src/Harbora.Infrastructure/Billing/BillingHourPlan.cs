using Harbora.Domain.Billing;

namespace Harbora.Infrastructure.Billing;

/// <summary>One thing a workspace held for an hour, with the rate already resolved.</summary>
public sealed record BillableResource(
    BilledResourceType Type,
    Guid Id,
    string Name,
    BilledRunState State,
    long RatePerHourMinor);

/// <summary>One ledger line, worked out but not yet written.</summary>
public sealed record PlannedLine(
    BilledResourceType Type,
    Guid? Id,
    string Name,
    BilledRunState State,
    long RatePerHourMinor,
    long AmountMinor,
    LedgerKind Kind);

/// <summary>
/// The lines for one workspace for one hour. Pure — no database, no clock — so every rule below is
/// provable directly rather than by running a job and reading a table.
/// </summary>
public static class BillingHourPlan
{
    /// <summary>
    /// Charge each resource at its own rate, then, if the total falls short of the plan's hourly
    /// floor, add one line for the difference.
    ///
    /// <para>
    /// The shortfall is a line rather than an adjustment to the others because a bill has to add up
    /// in front of the person paying it: every app shows what it actually cost, and the gap between
    /// that and the floor is labelled as what it is.
    /// </para>
    ///
    /// <para>
    /// <b><paramref name="planBaseRatePerHourMinor"/> is nullable, and null is not zero.</b> It is
    /// null in two situations that look nothing alike and want the same answer: nobody has priced
    /// the plan, and this hour could not be priced in full. In both, the shortfall is not a number
    /// anyone knows — subtracting an incomplete total from a floor produces a top-up that covers a
    /// gap which may not exist, and a corrected pass would then add the missing resource lines ON
    /// TOP of it. Passing the property straight through is also what stops the caller writing
    /// <c>?? 0</c>, which would put back the exact "unpriced reads as free" ambiguity the nullable
    /// rate columns exist to remove.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PlannedLine> For(
        IReadOnlyList<BillableResource> resources,
        long? planBaseRatePerHourMinor)
    {
        var lines = new List<PlannedLine>(resources.Count + 1);
        var total = 0L;

        foreach (var r in resources)
        {
            // A rate of zero writes nothing. A row of zero every hour for every unpriced resource is
            // how a ledger becomes the largest table on the install without ever holding a number.
            //
            // A negative rate is dropped by the same test, and not because it is tidier: negating it
            // would write a POSITIVE line — the platform paying the customer for holding a server —
            // and counting it towards the hour would make the plan-minimum top-up larger than the
            // floor to cover a gap that does not exist. Neither shows up as an error; the ledger
            // still adds up, to the wrong number.
            if (r.RatePerHourMinor <= 0) continue;

            total += r.RatePerHourMinor;
            lines.Add(new PlannedLine(
                r.Type, r.Id, r.Name, r.State, r.RatePerHourMinor,
                AmountMinor: -r.RatePerHourMinor, LedgerKind.Charge));
        }

        // No floor known, no floor line. Deliberately not folded into the `> 0` test below: a null
        // that reached that comparison would evaluate to false and produce the right rows for the
        // wrong reason, leaving nothing to read when somebody asks what an unpriced plan does.
        if (planBaseRatePerHourMinor is not { } floor) return lines;

        var shortfall = floor - total;
        if (shortfall > 0)
        {
            lines.Add(new PlannedLine(
                BilledResourceType.PlanBase,
                // Null on purpose: the index that makes a retried tick harmless keys on
                // (workspace, type, id, hour), and this line needs a stable key to collide on.
                Id: null,
                Name: "Plan minimum",
                BilledRunState.NotApplicable,
                // The floor, not the gap — so the ledger records what the plan was that hour. This
                // is the one line where rate × hours is deliberately not the amount; anything that
                // reconciles a line by that multiplication has to exclude this kind.
                RatePerHourMinor: floor,
                AmountMinor: -shortfall,
                LedgerKind.PlanMinimumTopUp));
        }

        return lines;
    }
}
