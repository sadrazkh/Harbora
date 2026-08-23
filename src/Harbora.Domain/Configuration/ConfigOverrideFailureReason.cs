namespace Harbora.Domain.Configuration;

/// <summary>
/// The four causes a rule's own doc in the plan enumerates by name — a deployment failure names
/// exactly one of these, never a bare "override failed". See
/// <c>Harbora.Infrastructure.Configuration.ConfigOverrideDiagnostic</c> for the facts each carries.
/// </summary>
public enum ConfigOverrideFailureReason
{
    /// <summary>The file does not exist at the path this rule names, inside the container.</summary>
    FileNotFound,

    /// <summary>The file exists but its own format's parser could not read it.</summary>
    ParseError,

    /// <summary>The file parsed cleanly, but the key path this rule names is not in it.</summary>
    KeyPathNotFound,

    /// <summary>This rule references an attached service that is no longer attached (or whose
    /// connection string is not ready), for a value kind of
    /// <see cref="ConfigOverrideValueKind.AttachedServiceConnectionString"/>.</summary>
    ServiceReferenceUnavailable
}
