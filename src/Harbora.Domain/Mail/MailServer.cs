using Harbora.Domain.Common;

namespace Harbora.Domain.Mail;

public enum MailServerStatus
{
    Provisioning = 0,
    Ready = 1,
    Failed = 2,
    Stopped = 3
}

/// <summary>The single shared mail plane operated by the platform provider.</summary>
public sealed class MailServer : BaseEntity
{
    public Guid ServerId { get; set; }
    public string PublicHostname { get; set; } = string.Empty;
    public string ApiBaseUrl { get; set; } = string.Empty;
    public string Image { get; set; } = "stalwartlabs/stalwart:v0.16";
    public string ContainerName { get; set; } = "harbora-mail";
    public string EncryptedAdminUser { get; set; } = string.Empty;
    public string EncryptedAdminPassword { get; set; } = string.Empty;
    public MailServerStatus Status { get; set; } = MailServerStatus.Provisioning;
    public string? LastError { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Price copied to a domain/mailbox when it is created.</summary>
    public long? DomainRatePerHourMinor { get; set; }
    public long? MailboxRatePerHourMinor { get; set; }
    public int MaxDomainsPerWorkspace { get; set; }
    public int MaxMailboxesPerWorkspace { get; set; }
}

