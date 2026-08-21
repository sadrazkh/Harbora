namespace Harbora.Web.ViewModels;

/// <param name="Password">Non-null only for the one provider somebody clicked to reveal — the
/// same rule <c>StorageBucketViewModel.SecretKey</c> follows for a bucket's secret key.</param>
/// <param name="LastTestSucceeded">Null means never tested, shown as "not tested" rather than as a
/// fabricated pass or fail — the same three-state rule a usage figure follows (measured / not
/// measured / zero are three different things).</param>
/// <param name="AttachedApps">Apps this provider is currently attached to.</param>
/// <param name="AvailableApps">Apps in this workspace not yet attached to this provider, for the
/// attach form.</param>
public sealed record EmailProviderViewModel(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string? Password,
    string FromAddress,
    string? FromName,
    bool UseSsl,
    DateTimeOffset? LastTestedAt,
    bool? LastTestSucceeded,
    string? LastTestMessage,
    IReadOnlyList<EmailProviderAttachedAppViewModel> AttachedApps,
    IReadOnlyList<EmailProviderAttachableAppViewModel> AvailableApps);

/// <summary>One app an email provider could be attached to next (F6, 2026-08-21
/// functions-and-services plan).</summary>
public sealed record EmailProviderAttachableAppViewModel(Guid Id, string Name);

/// <summary>One app an email provider is currently attached to (F6, 2026-08-21
/// functions-and-services plan).</summary>
public sealed record EmailProviderAttachedAppViewModel(Guid AppId, string AppName, bool HasUnpublishedChanges);

public sealed record EmailProvidersPageViewModel
{
    public IReadOnlyList<EmailProviderViewModel> Providers { get; init; } = [];
    public Guid? RevealedProviderId { get; init; }
}
