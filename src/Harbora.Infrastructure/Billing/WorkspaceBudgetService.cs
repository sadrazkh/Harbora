using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Billing;

public sealed record WorkspaceBudgetState(
    DateTimeOffset Month,
    long SpendMinor,
    long? BudgetMinor,
    long? SpendLimitMinor)
{
    public bool BudgetExceeded => BudgetMinor is > 0 && SpendMinor >= BudgetMinor;
    public bool SpendLimitReached => SpendLimitMinor is > 0 && SpendMinor >= SpendLimitMinor;
    public long? RemainingMinor => SpendLimitMinor is > 0 ? Math.Max(0, SpendLimitMinor.Value - SpendMinor) : null;
}

/// <summary>Reads one workspace's UTC calendar-month cost from the append-only billing ledger.</summary>
public sealed class WorkspaceBudgetService(HarboraDbContext db)
{
    public static bool CanResetSpendLimit(Harbora.Domain.Identity.Workspace workspace, DateTimeOffset now) =>
        workspace.SuspendedReason == Harbora.Domain.Identity.SuspensionReason.SpendLimit
        && (workspace.SpendLimitResetsAt is null || workspace.SpendLimitResetsAt <= now
            || workspace.MonthlySpendLimitMinor is not > 0
            || workspace.SpendLimitAtSuspensionMinor is null
            || workspace.MonthlySpendLimitMinor > workspace.SpendLimitAtSuspensionMinor);

    public async Task<WorkspaceBudgetState> GetAsync(Guid workspaceId, DateTimeOffset instant, CancellationToken ct)
    {
        var utc = instant.ToUniversalTime();
        var from = new DateTimeOffset(utc.Year, utc.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddMonths(1);
        var limits = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.Id == workspaceId)
            .Select(w => new { w.MonthlyBudgetMinor, w.MonthlySpendLimitMinor })
            .FirstOrDefaultAsync(ct);
        var movement = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .Where(l => l.WorkspaceId == workspaceId && l.BillingHour >= from && l.BillingHour < to
                && (l.Kind == LedgerKind.Charge || l.Kind == LedgerKind.PlanMinimumTopUp))
            .SumAsync(l => (long?)l.AmountMinor, ct) ?? 0;
        return new WorkspaceBudgetState(from, checked(-movement),
            limits?.MonthlyBudgetMinor, limits?.MonthlySpendLimitMinor);
    }

    public async Task<bool> CanSpendAsync(Guid workspaceId, long additionalMinor, DateTimeOffset instant, CancellationToken ct)
    {
        var state = await GetAsync(workspaceId, instant, ct);
        return state.SpendLimitMinor is not > 0
            || checked(state.SpendMinor + Math.Max(0, additionalMinor)) <= state.SpendLimitMinor.Value;
    }
}
