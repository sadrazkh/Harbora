using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Networking;

/// <summary>Why an app ended up with the address it did — or with none.</summary>
public enum AppAddressOutcome
{
    /// <summary>It got the name it asked for.</summary>
    Assigned = 0,

    /// <summary>The name was taken, so it got a discriminated one. The person is told.</summary>
    Discriminated = 1,

    /// <summary>This kind of service takes no inbound traffic, so an address would answer nothing.</summary>
    KindTakesNoTraffic = 2,

    /// <summary>No platform root domain is configured, so there is nothing to build a name under.</summary>
    NoRootDomain = 3,

    /// <summary>The name is one of the platform's own.</summary>
    Reserved = 4,

    /// <summary>Every discriminated attempt was taken too. Rare, and said out loud rather than skipped.</summary>
    Exhausted = 5,

    /// <summary>
    /// A name somebody typed was already in use. Refused rather than discriminated onto a name they
    /// never agreed to — see <see cref="AppAddressAssigner"/>'s <c>AssignAsync</c> for why only a
    /// <em>typed</em> collision is refused instead of mangled.
    /// </summary>
    Taken = 6
}

/// <summary>The decision, and the reason for it. <see cref="Host"/> is null unless one was settled on.</summary>
public readonly record struct AppAddressDecision(string? Host, AppAddressOutcome Outcome)
{
    public bool HasAddress => Host is not null;
}

/// <summary>
/// What hostname an app should be given.
///
/// Pure on purpose, for the reason <see cref="ServicePlan"/> gives about itself: each rule is one
/// testable statement rather than a condition buried in a creation path. There were four such paths
/// and they disagreed — one skipped silently on a clash, one checked nothing at all, one had no rule
/// whatsoever. The database half lives in <c>AppAddressAssigner</c>.
/// </summary>
public static class AppAddress
{
    /// <summary>
    /// The name this app should be given, or null with the reason why not.
    ///
    /// <paramref name="requested"/> is a name somebody typed, which wins over the derived one — and is
    /// still subject to every check below, because the reserved-host rule exists precisely for names
    /// people type.
    /// </summary>
    public static AppAddressDecision Decide(
        ServiceKind kind, string? requested, string? slug, string? rootDomain,
        IEnumerable<string> reservedHosts)
    {
        if (!ServicePlan.CanHaveDomains(kind))
            return new(null, AppAddressOutcome.KindTakesNoTraffic);

        var host = ServicePlan.HostFor(kind, requested, slug, rootDomain);
        if (string.IsNullOrWhiteSpace(host))
            return new(null, AppAddressOutcome.NoRootDomain);

        // "localhost" is what appsettings ships as RootDomain for a developer machine. Building
        // {slug}.localhost from it produces a name that resolves nowhere and a certificate request
        // that cannot be answered — TemplateDeploymentService already refused it by hand, and that
        // refusal belongs here with the rest of them.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return new(null, AppAddressOutcome.NoRootDomain);

        // Sub-project 7 (2026-08-20 platform-options plan): status-{workspaceSlug} is reserved the
        // same way — one guard, asked twice, rather than a second reservation mechanism next to this
        // one. See ReservedHosts.IsReservedPrefix for why a prefix check exists at all.
        return ReservedHosts.IsReserved(host, reservedHosts) || ReservedHosts.IsReservedPrefix(host, rootDomain)
            ? new(null, AppAddressOutcome.Reserved)
            : new(host, AppAddressOutcome.Assigned);
    }

    /// <summary>
    /// The same name with a discriminator on its leftmost label: <c>shop.apps.example.com</c> becomes
    /// <c>shop-k3f.apps.example.com</c>.
    ///
    /// Leftmost, because the certificate is a wildcard for <c>*.apps.example.com</c>. A discriminator
    /// added anywhere else would produce a name that is not covered by it, and the app would answer
    /// with a certificate error rather than a page — a worse outcome than the clash it was solving.
    ///
    /// Bounded to <see cref="Harbora.Infrastructure.Projects.PreviewNaming.MaxLabel"/>, the same 63
    /// characters DNS itself stops a label at: the original label is trimmed to make room for the
    /// suffix rather than left to grow past the limit, which is how a legal 63-character label became
    /// an illegal 67-character one before this bound existed. An empty suffix (test-reachable only —
    /// production's own suffix is always three characters) is left with no trailing dash rather than
    /// a label ending in one, which DNS also refuses.
    /// </summary>
    public static string Discriminate(string host, string suffix)
    {
        var dot = host.IndexOf('.');
        var label = dot < 0 ? host : host[..dot];
        var rest = dot < 0 ? "" : host[dot..];

        if (suffix.Length == 0) return $"{label}{rest}";

        var maxLabel = Harbora.Infrastructure.Projects.PreviewNaming.MaxLabel;
        var room = Math.Max(0, maxLabel - suffix.Length - 1);
        if (label.Length > room) label = label[..room];

        return $"{label}-{suffix}{rest}";
    }
}
