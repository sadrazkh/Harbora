using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>
/// An app config value. When <see cref="IsSecret"/> is true the value is stored
/// encrypted at rest and is redacted everywhere in the UI, API and logs.
/// </summary>
public class EnvironmentVariable : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public string Key { get; set; } = string.Empty;

    /// <summary>Plaintext value for non-secret vars; ciphertext for secrets.</summary>
    public string Value { get; set; } = string.Empty;

    public bool IsSecret { get; set; }

    /// <summary>If true the var is injected at build time as well as runtime.</summary>
    public bool AvailableAtBuild { get; set; }
}
