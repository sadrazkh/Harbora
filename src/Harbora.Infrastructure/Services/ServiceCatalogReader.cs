using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// The database catalogue as an operator has configured it.
///
/// One place on purpose. The version list is read three times on the way to creating a database —
/// to draw the dropdown, to check what came back, and to decide the default when nobody picked —
/// and three readers is three chances for the page to offer something the check then refuses.
/// </summary>
public static class ServiceCatalogReader
{
    /// <summary>
    /// Every engine, with the versions that should actually be offered and a default image built
    /// from the first of them.
    /// </summary>
    public static async Task<IReadOnlyList<ServiceCatalogEntry>> EffectiveAsync(
        HarboraDbContext db, IManagedServiceEngine engine, CancellationToken ct)
    {
        var shipped = engine.Catalog;

        var keys = shipped.Select(e => SettingKeys.ServiceVersions(e.Type)).ToList();

        // Unfiltered: settings are platform-wide, and reading them through the tenant filter is how
        // a setting silently stops applying to everybody except whoever saved it.
        var stored = await db.Settings.IgnoreQueryFilters()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, ct);

        return shipped.Select(entry =>
        {
            stored.TryGetValue(SettingKeys.ServiceVersions(entry.Type), out var value);
            var versions = ServiceVersions.Resolve(value, entry.Versions);

            // The image has to move with the list. Leaving it at the shipped default would offer
            // "17-alpine" in the dropdown and pull 16-alpine for anybody who did not choose.
            var repository = Harbora.Infrastructure.Templates.ImageReference.RepositoryOf(entry.DefaultImage)
                             ?? entry.DefaultImage;

            return entry with
            {
                Versions = versions,
                DefaultImage = versions.Count > 0 ? $"{repository}:{versions[0]}" : entry.DefaultImage
            };
        }).ToList();
    }
}
