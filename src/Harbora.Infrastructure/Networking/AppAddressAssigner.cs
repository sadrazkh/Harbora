using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Networking;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Harbora.Infrastructure.Networking;

/// <summary>
/// Where <see cref="AppAddressAssigner.AssignAsync"/>'s <c>requested</c> name came from — decides
/// what a collision means.
///
/// A name a person typed into a form is a promise made to them: refuse rather than mangle it onto a
/// zone the platform's wildcard certificate does not cover and no DNS record points at. A name the
/// platform derived — from the slug, or from a branch preview's own <c>PreviewNaming.Host</c> — carries
/// no such promise, so a collision is discriminated the same way an app with no requested name at all
/// would be. <c>AssignAsync</c> cannot tell the two apart from the string itself, so every caller
/// states which one it is bringing.
/// </summary>
public enum AppAddressRequestOrigin
{
    /// <summary>Computed by the platform. A collision is discriminated.</summary>
    Derived,

    /// <summary>Typed by a person. A collision is refused — see <see cref="AppAddressOutcome.Taken"/>.</summary>
    Typed
}

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
    /// which is why the name is an input and only the checks are shared. <paramref name="origin"/>
    /// says which of the two it is, and decides what a collision means for it: see
    /// <see cref="AppAddressRequestOrigin"/>.
    ///
    /// <paramref name="suffix"/> exists so a test can pin the discriminator. Production passes null and
    /// gets a short random one.
    /// </summary>
    public async Task<AppAddressDecision> AssignAsync(
        App app, string? requested, AppAddressRequestOrigin origin, Func<string>? suffix, CancellationToken ct)
    {
        var decision = AppAddress.Decide(
            app.Kind, requested, app.Slug, await RootDomainAsync(ct), ReservedFor());
        if (!decision.HasAddress) return decision;

        // Read across every workspace on purpose: a hostname taken by another tenant is still taken,
        // because DNS is not multi-tenant. A filtered read would report the name free and then route
        // two apps to one hostname.
        //
        // No IgnoreQueryFilters() here, and that is deliberate rather than an oversight. DomainName
        // carries no tenant filter — HarboraDbContext says so explicitly and gives the reason: it is
        // only ever reached through its parent, which is filtered, so a navigation filter would add a
        // join to every read for no extra protection. Writing IgnoreQueryFilters() would imply a
        // filter that is not there and quietly contradict that decision. If DomainName is ever given
        // one, this read must gain the escape at the same time — the test named for cross-workspace
        // collisions is what fails on the day it does not.
        var host = decision.Host!;
        var outcome = AppAddressOutcome.Assigned;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (!await db.Domains.AnyAsync(d => d.Host == host, ct))
            {
                app.Domains.Add(new DomainName
                {
                    Host = host, SslEnabled = true, ForceHttps = true, IsPrimary = true
                });
                return new(host, outcome);
            }

            // A typed name is a promise made to the person who typed it: mangling shop.mycompany.com
            // into shop-k3f.mycompany.com would put the app on a zone with no DNS record for that name
            // and no wildcard certificate to cover it — "reachable" would be false. Only a derived name
            // may be discriminated; see AppAddressRequestOrigin.
            if (origin == AppAddressRequestOrigin.Typed)
                return new(null, AppAddressOutcome.Taken);

            host = AppAddress.Discriminate(decision.Host!, (suffix ?? NewSuffix)());
            outcome = AppAddressOutcome.Discriminated;
        }

        return new(null, AppAddressOutcome.Exhausted);
    }

    /// <summary>Three base-36 characters. Short enough to read aloud, wide enough that a second clash is rare.</summary>
    private static string NewSuffix() =>
        Guid.NewGuid().ToString("N")[..3];
}
