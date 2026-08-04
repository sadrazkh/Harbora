using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Navigation;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The panel mode for whoever is signed in, resolved once per request.
///
/// Reads the preference from the account rather than the browser, so somebody who chose Advanced on
/// their laptop meets Advanced on their phone. A local-storage flag would make the mode a property
/// of the device, which is the wrong thing for a setting people configure once and expect to keep.
/// </summary>
public sealed class PanelModeProvider(HarboraDbContext db, ICurrentUser currentUser)
{
    /// <summary>Where an administrator's default for new accounts lives.</summary>
    public const string DefaultModeSettingKey = "panel.default_mode";

    private PanelMode? _resolved;

    public async Task<PanelMode> GetAsync(CancellationToken ct = default)
    {
        if (_resolved is { } cached) return cached;

        // Signed out — the landing and login pages have no sidebar to filter, and Advanced is the
        // honest answer for "no preference known".
        if (currentUser.UserId is not { } userId)
            return (_resolved = PanelMode.Advanced).Value;

        var account = await db.Users.IgnoreQueryFilters()
            .Where(u => u.Id == userId)
            .Select(u => new { u.PanelMode, u.Role })
            .FirstOrDefaultAsync(ct);

        if (account is null) return (_resolved = PanelMode.Advanced).Value;

        var platformDefault = await ReadDefaultAsync(ct);

        _resolved = PanelModeResolver.Resolve(account.PanelMode, account.Role, platformDefault);
        return _resolved.Value;
    }

    /// <summary>Records a person's choice. Null means "follow the platform default again".</summary>
    public async Task SetAsync(PanelMode? mode, CancellationToken ct = default)
    {
        if (currentUser.UserId is not { } userId) return;

        var account = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (account is null) return;

        account.PanelMode = mode;
        await db.SaveChangesAsync(ct);

        _resolved = null;
    }

    /// <summary>The administrator's default for accounts that have never chosen.</summary>
    public async Task<PanelMode?> ReadDefaultAsync(CancellationToken ct = default)
    {
        var value = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == DefaultModeSettingKey)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

        return Enum.TryParse<PanelMode>(value, ignoreCase: true, out var parsed) ? parsed : null;
    }
}
