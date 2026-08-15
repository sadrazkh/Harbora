using Harbora.Domain.Common;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Networking;

/// <summary>The name, and the reason. <see cref="Alias"/> is null unless one was settled on.</summary>
public readonly record struct PrivateAddressDecision(string? Alias, PrivateAddressOutcome Outcome)
{
    public bool HasAlias => Alias is not null;
}

/// <summary>
/// The short name an app's neighbours can reach it by.
///
/// Pure, for the reason <see cref="ServicePlan"/> gives about itself: one testable statement rather
/// than a condition buried in a deployment path where getting it wrong fails a deploy. The Docker and
/// database halves stay in the pipeline.
/// </summary>
public static class PrivateAddress
{
    /// <summary>
    /// The alias this app should answer to, or null with the reason why not.
    ///
    /// <paramref name="taken"/> is every name already answered to on this network by somebody else.
    /// Docker resolves an alias to <b>every</b> container holding it and balances between them, so a
    /// duplicate does not fail loudly — it sends a share of the calls to a stranger. An app that
    /// reaches the wrong database intermittently is worse off than one with no shortcut at all.
    /// </summary>
    public static PrivateAddressDecision Decide(
        ServiceKind kind, string? slug, IReadOnlyCollection<string> taken)
    {
        if (!ServicePlan.JoinsInternalNetwork(kind))
            return new(null, PrivateAddressOutcome.KindDoesNotJoin);

        var alias = slug?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(alias))
            return new(null, PrivateAddressOutcome.NoSlug);

        return taken.Any(t => string.Equals(t?.Trim(), alias, StringComparison.OrdinalIgnoreCase))
            ? new(null, PrivateAddressOutcome.Ambiguous)
            : new(alias, PrivateAddressOutcome.Registered);
    }

    /// <summary>The address as somebody would paste it into a config file.</summary>
    public static string Url(string alias, int port) => $"http://{alias}:{port}";
}
