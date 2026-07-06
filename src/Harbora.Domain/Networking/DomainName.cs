using Harbora.Domain.Common;
using Harbora.Domain.Apps;

namespace Harbora.Domain.Networking;

/// <summary>A hostname bound to an app, optionally with automatic Let's Encrypt SSL.</summary>
public class DomainName : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public string Host { get; set; } = string.Empty;   // e.g. app.example.com
    public bool SslEnabled { get; set; } = true;
    public bool ForceHttps { get; set; } = true;
    public bool IsPrimary { get; set; }

    public Guid? CertificateId { get; set; }
    public Certificate? Certificate { get; set; }
}
