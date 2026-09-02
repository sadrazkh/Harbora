namespace Harbora.Web.ViewModels;

/// <param name="Secret">Non-null only for the one credential somebody clicked to reveal — the same
/// rule <c>EmailProviderViewModel.Password</c> and <c>StorageBucketViewModel.SecretKey</c> follow.</param>
/// <param name="UpdatedAt">When this row was last saved — the only visible trace of a rotation, since
/// nothing else records that the secret changed.</param>
public sealed record RegistryCredentialViewModel(
    Guid Id,
    string RegistryHost,
    string Username,
    string? Secret,
    DateTimeOffset UpdatedAt);

public sealed record RegistryCredentialsPageViewModel
{
    public IReadOnlyList<RegistryCredentialViewModel> Credentials { get; init; } = [];
    public Guid? RevealedCredentialId { get; init; }
}
