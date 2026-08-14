using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Gives an app the address it should have — the one thing every path that creates an app calls.
///
/// There were four such paths and four different answers. AppsController skipped the insert when the
/// name was taken and told nobody. TemplateDeploymentService built the hostname by hand with no
/// kind check, no reserved-host check and no collision check. PreviewEnvironmentService had its own
/// third rule. EnvironmentCloner had none at all, so a cloned app was created with no address.
///
/// This does not call SaveChangesAsync. Every caller is already inside its own transaction — a save
/// here would commit half of somebody else's unit of work.
/// </summary>
public sealed class AppAddressAssigner(HarboraDbContext db, IConfiguration config)
{
    /// <summary>How many discriminated names to try before giving up and saying so.</summary>
    private const int MaxAttempts = 5;

    /// <summary>The platform's configured root domain, or null when none is set.</summary>
    public async Task<string?> RootDomainAsync(CancellationToken ct) =>
        await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.PlatformRootDomain)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// What this app would be given, writing nothing. The backfill preview screen renders this, and it
    /// must be the same answer <see cref="AssignAsync"/> would reach — a preview computed by a second
    /// copy of the rule is a preview that can lie about what the button will do.
    ///
    /// Collisions are deliberately not resolved here: the discriminator is chosen at assignment time,
    /// so promising a specific one on a screen the operator might sit on for a minute would be
    /// promising something this cannot keep.
    /// </summary>
    public async Task<AppAddressDecision> PreviewAsync(App app, CancellationToken ct) =>
        AppAddress.Decide(app.Kind, requested: null, app.Slug, await RootDomainAsync(ct), ReservedFor());

    private IReadOnlyList<string> ReservedFor() => ReservedHosts.ForPlatform(
        config["PANEL_DOMAIN"], config["NodeAgent:PublicUrl"], config["Storage:S3:PublicEndpoint"]);

    /// <summary>
    /// Decide this app's address and attach it. <paramref name="requested"/> is a name somebody typed,
    /// or a candidate a caller derives itself — branch previews pass their own branch-keyed name here,
    /// which is why the name is an input and only the checks are shared.
    ///
    /// <paramref name="suffix"/> exists so a test can pin the discriminator. Production passes null and
    /// gets a short random one.
    /// </summary>
    public async Task<AppAddressDecision> AssignAsync(
        App app, string? requested, Func<string>? suffix, CancellationToken ct)
    {
        var decision = AppAddress.Decide(
            app.Kind, requested, app.Slug, await RootDomainAsync(ct), ReservedFor());
        if (!decision.HasAddress) return decision;

        // IgnoreQueryFilters: a hostname taken by another workspace is still taken. DNS is not
        // multi-tenant, and a filtered query would report the name free and then route two apps to it.
        var host = decision.Host!;
        var outcome = AppAddressOutcome.Assigned;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!await db.Domains.IgnoreQueryFilters().AnyAsync(d => d.Host == host, ct))
            {
                app.Domains.Add(new DomainName
                {
                    Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true
                });
                return new(host, outcome);
            }

            host = AppAddress.Discriminate(decision.Host!, (suffix ?? NewSuffix)());
            outcome = AppAddressOutcome.Discriminated;
        }

        return new(null, AppAddressOutcome.Exhausted);
    }

    /// <summary>Three base-36 characters. Short enough to read aloud, wide enough that a second clash is rare.</summary>
    private static string NewSuffix() =>
        Guid.NewGuid().ToString("N")[..3];
}
