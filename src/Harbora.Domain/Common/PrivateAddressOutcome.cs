namespace Harbora.Domain.Common;

/// <summary>Why an app can or cannot be called by a short name inside its own environment.</summary>
public enum PrivateAddressOutcome
{
    /// <summary>It answers to its slug.</summary>
    Registered = 0,

    /// <summary>Something else on the network already answers to that name, so this one is withheld.</summary>
    Ambiguous = 1,

    /// <summary>This kind does not join the internal network at all.</summary>
    KindDoesNotJoin = 2,

    /// <summary>No slug to build a name from.</summary>
    NoSlug = 3
}
