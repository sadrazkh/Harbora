using Harbora.Application.Abstractions;

namespace Harbora.Web.ViewModels;

public sealed class CloudflareSettingsViewModel
{
    public bool Enabled { get; init; }
    public bool HasToken { get; init; }
    public string? Zone { get; init; }
    public DateTimeOffset? LastVerifiedAt { get; init; }
    public string PanelDomain { get; init; } = string.Empty;
    public string RootDomain { get; init; } = string.Empty;
    public string? S3Domain { get; init; }
    public DomainStatus? PanelStatus { get; init; }
}
