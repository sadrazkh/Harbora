using Harbora.Domain.Common;

namespace Harbora.Domain.Mail;

public enum MailResourceStatus
{
    Provisioning = 0,
    Ready = 1,
    Failed = 2,
    Disabled = 3
}

public sealed class MailDomain : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid MailServerId { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string? ProviderObjectId { get; set; }
    public string? DnsZone { get; set; }
    public MailResourceStatus Status { get; set; } = MailResourceStatus.Provisioning;
    public string? LastError { get; set; }
    public long RatePerHourMinor { get; set; }
    public MailServer? MailServer { get; set; }
    public ICollection<MailMailbox> Mailboxes { get; set; } = [];
}

public sealed class MailMailbox : BaseEntity
{
    public Guid WorkspaceId { get; set; }
    public Guid MailDomainId { get; set; }
    public string LocalPart { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? ProviderObjectId { get; set; }
    public MailResourceStatus Status { get; set; } = MailResourceStatus.Provisioning;
    public string? LastError { get; set; }
    public long QuotaBytes { get; set; }
    public long RatePerHourMinor { get; set; }
    public MailDomain? MailDomain { get; set; }
}
