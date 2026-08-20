using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>
/// A workspace-level bag of environment variables an app can attach to by reference, instead of
/// copy-pasting the same values (a shared database prefix, a feature flag, a common API key) into
/// every app's own environment (Sub-project 9, 2026-08-20 platform-options plan).
///
/// <para>
/// Entries mirror <see cref="EnvironmentVariable"/> exactly — name, value, <see cref="ConfigGroupEntry.IsSecret"/>
/// with ciphertext via <c>ISecretProtector</c>, the same masking in the UI. A value is not a lesser
/// kind of secret for being shared across apps rather than held by one.
/// </para>
/// </summary>
public class ConfigGroup : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    public ICollection<ConfigGroupEntry> Entries { get; set; } = new List<ConfigGroupEntry>();
    public ICollection<AppConfigGroup> Apps { get; set; } = new List<AppConfigGroup>();
}
