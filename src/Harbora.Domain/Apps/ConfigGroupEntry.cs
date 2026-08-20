using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>
/// One key/value pair inside a <see cref="ConfigGroup"/>. Shaped exactly like
/// <see cref="EnvironmentVariable"/> on purpose — same fields, same encryption, same UI treatment —
/// so a group entry never becomes a second, weaker way to hold a secret.
/// </summary>
public class ConfigGroupEntry : BaseEntity
{
    public Guid ConfigGroupId { get; set; }
    public ConfigGroup? ConfigGroup { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>Plaintext value for non-secret entries; ciphertext for secrets.</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsSecret { get; set; }
}
