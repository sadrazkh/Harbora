namespace Harbora.Domain.Configuration;

/// <summary>
/// What a <see cref="ConfigOverrideRule"/>'s value actually is, once resolved at deploy time.
/// </summary>
public enum ConfigOverrideValueKind
{
    /// <summary>A plain value entered in the panel — <see cref="ConfigOverrideRule.LiteralValue"/>.</summary>
    Literal,

    /// <summary>A secret value, encrypted at rest via <c>ISecretProtector</c> —
    /// <see cref="ConfigOverrideRule.EncryptedSecretValue"/>. Never echoed back in plaintext.</summary>
    Secret,

    /// <summary>
    /// A reference to an attached managed service's own connection string (C1, 2026-08-22
    /// config-delivery plan), addressed by the attachment's alias
    /// (<see cref="Harbora.Domain.Services.AppManagedService.Alias"/>) — resolved at deploy time
    /// through <c>Harbora.Application.Abstractions.IAttachedServiceConnectionStringResolver</c>,
    /// never stored here itself. This is what lets an operator point
    /// <c>ConnectionStrings:Default</c> at an attached database and never see its password.
    /// </summary>
    AttachedServiceConnectionString
}
