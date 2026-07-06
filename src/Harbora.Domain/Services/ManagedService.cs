using Harbora.Domain.Common;

namespace Harbora.Domain.Services;

/// <summary>
/// A managed backing service (database/cache) that Harbora provisions as a container and
/// can attach to apps. Credentials are stored encrypted and surfaced as a connection string.
/// </summary>
public class ManagedService : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid ServerId { get; set; }

    public string Name { get; set; } = string.Empty;
    public ManagedServiceType Type { get; set; }
    public string Version { get; set; } = "latest";
    public ServiceStatus Status { get; set; } = ServiceStatus.Provisioning;

    public string ContainerName { get; set; } = string.Empty;
    public int InternalPort { get; set; }

    public string Username { get; set; } = string.Empty;
    /// <summary>Encrypted at rest.</summary>
    public string EncryptedPassword { get; set; } = string.Empty;
    public string DatabaseName { get; set; } = string.Empty;

    public string VolumeName { get; set; } = string.Empty;
}
