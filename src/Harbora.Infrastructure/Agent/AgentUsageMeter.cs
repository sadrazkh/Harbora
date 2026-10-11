using System.Globalization;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The per-workspace daily cap on model calls (<see cref="AgentSettingKeys.DailyRequestLimit"/>).
///
/// <para>
/// Every chat-completions call counts — a turn that reads three things and then answers is four
/// calls — because that is what the provider bills, and the owner's budget is "about a thousand
/// requests a day", not "about a thousand questions".
/// </para>
///
/// <para>
/// A call is reserved before it is made, with the check and the increment in one conditional
/// <c>UPDATE</c> (<see cref="TryReserveAsync"/>), so two turns running at once cannot both read "one
/// left" and both spend it. The same InMemory caveat as <c>SignInTokenService.TryClaimAsync</c>
/// applies: EF InMemory has no <c>ExecuteUpdateAsync</c>, so there the fallback is a plain
/// read-then-write that is not atomic — enough for the single-caller tests in Harbora.Tests.
/// </para>
/// </summary>
public sealed class AgentUsageMeter(HarboraDbContext db, ISystemClock clock)
{
    /// <summary>The configured cap: 0 means unlimited; blank, missing or unreadable means the default.</summary>
    public async Task<int> LimitAsync(CancellationToken ct)
    {
        // Unfiltered: settings are platform-wide (see ServiceCatalogReader for why that matters).
        var raw = await db.Settings.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.Key == AgentSettingKeys.DailyRequestLimit)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        // An unreadable value falls back to the default rather than to unlimited: a typo in a setting
        // must not quietly remove the only thing standing between a runaway loop and the owner's bill.
        return int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var limit) && limit >= 0
            ? limit
            : AgentSettingKeys.DefaultDailyRequestLimit;
    }

    /// <summary>Model calls this workspace has made today (UTC).</summary>
    public async Task<int> UsedTodayAsync(Guid workspaceId, CancellationToken ct)
    {
        var today = Today;
        return await db.AgentUsageDays.IgnoreQueryFilters().AsNoTracking()
            .Where(u => u.WorkspaceId == workspaceId && u.Day == today)
            .Select(u => u.ModelCalls)
            .FirstOrDefaultAsync(ct);
    }

    /// <summary>Calls left today, or null when the workspace has no cap.</summary>
    public async Task<int?> RemainingAsync(Guid workspaceId, CancellationToken ct)
    {
        var limit = await LimitAsync(ct);
        if (limit == 0) return null;
        return Math.Max(0, limit - await UsedTodayAsync(workspaceId, ct));
    }

    /// <summary>
    /// Spends one call from today's allowance, if there is one left. True means the caller may make
    /// exactly one model call; false means the cap is reached and it must not.
    /// </summary>
    public async Task<bool> TryReserveAsync(Guid workspaceId, CancellationToken ct)
    {
        var limit = await LimitAsync(ct);
        var today = Today;

        if (!db.Database.IsRelational())
            return await TryReserveInMemoryAsync(workspaceId, today, limit, ct);

        // Two attempts: the second exists only for the first call of the day losing the race to
        // insert the day's row — the unique index refuses the loser's insert, and the loser then
        // takes the ordinary conditional-update path against the winner's row.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var reserved = await db.AgentUsageDays.IgnoreQueryFilters()
                .Where(u => u.WorkspaceId == workspaceId && u.Day == today && (limit == 0 || u.ModelCalls < limit))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ModelCalls, u => u.ModelCalls + 1), ct);
            if (reserved == 1) return true;

            var exists = await db.AgentUsageDays.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(u => u.WorkspaceId == workspaceId && u.Day == today, ct);
            if (exists) return false;

            var row = new AgentUsageDay { WorkspaceId = workspaceId, Day = today, ModelCalls = 1 };
            db.AgentUsageDays.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                // Somebody else opened today's row first. Forget ours and count against theirs.
                db.Entry(row).State = EntityState.Detached;
            }
        }

        return false;
    }

    private async Task<bool> TryReserveInMemoryAsync(Guid workspaceId, DateOnly today, int limit, CancellationToken ct)
    {
        var row = await db.AgentUsageDays.IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.WorkspaceId == workspaceId && u.Day == today, ct);

        if (row is null)
        {
            db.AgentUsageDays.Add(new AgentUsageDay { WorkspaceId = workspaceId, Day = today, ModelCalls = 1 });
        }
        else
        {
            if (limit != 0 && row.ModelCalls >= limit) return false;
            row.ModelCalls++;
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
}
