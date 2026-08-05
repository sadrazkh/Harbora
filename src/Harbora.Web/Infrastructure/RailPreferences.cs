using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Navigation;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Which side panels are open for whoever is signed in, resolved once per request.
///
/// The sibling of <see cref="PanelModeProvider"/>, and deliberately built the same way: the choice
/// lives on the account so it follows the person rather than the browser, and the operator's
/// default only applies to people who have not chosen.
/// </summary>
public sealed class RailPreferences(HarboraDbContext db, ICurrentUser currentUser)
{
    private (bool? QuickStart, bool? Overview)? _account;
    private (bool? QuickStart, bool? Overview)? _platform;

    /// <summary>Whether to draw this panel open.</summary>
    public async Task<bool> IsOpenAsync(RailPanel panel, CancellationToken ct = default)
    {
        var account = await AccountAsync(ct);
        var platform = await PlatformAsync(ct);

        return panel == RailPanel.QuickStart
            ? RailVisibility.Resolve(account.QuickStart, platform.QuickStart, panel)
            : RailVisibility.Resolve(account.Overview, platform.Overview, panel);
    }

    /// <summary>Records a person's choice. Null means "follow the default again".</summary>
    public async Task SetAsync(RailPanel panel, bool? open, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId) return;

        var user = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;

        if (panel == RailPanel.QuickStart) user.ShowQuickStart = open;
        else user.ShowOverview = open;

        await db.SaveChangesAsync(ct);
        _account = null;
    }

    private async Task<(bool? QuickStart, bool? Overview)> AccountAsync(CancellationToken ct)
    {
        if (_account is { } cached) return cached;

        if (currentUser.UserId is not { } userId) return (_account = (null, null)).Value;

        var row = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => new { u.ShowQuickStart, u.ShowOverview })
            .FirstOrDefaultAsync(ct);

        return (_account = (row?.ShowQuickStart, row?.ShowOverview)).Value;
    }

    private async Task<(bool? QuickStart, bool? Overview)> PlatformAsync(CancellationToken ct)
    {
        if (_platform is { } cached) return cached;

        // Unfiltered: a platform setting belongs to the installation, and reading it through the
        // tenant filter is how a default silently stops applying to everyone but whoever saved it.
        var rows = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.QuickStartDefault || s.Key == SettingKeys.OverviewDefault)
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        rows.TryGetValue(SettingKeys.QuickStartDefault, out var quickStart);
        rows.TryGetValue(SettingKeys.OverviewDefault, out var overview);

        return (_platform = (
            RailVisibility.ParseSetting(quickStart),
            RailVisibility.ParseSetting(overview))).Value;
    }
}
