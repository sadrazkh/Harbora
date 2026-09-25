using Harbora.Domain.Identity;

namespace Harbora.Web.ViewModels;

/// <summary>The owner's own token page: the mint form, the list, and — for one response only — a
/// freshly minted secret.</summary>
public sealed class SignInTokensPageViewModel
{
    public IReadOnlyList<SignInTokenRow> Tokens { get; init; } = [];

    public int MaxPurposeLength { get; init; }
    public int RedemptionLifetimeMinutes { get; init; }
    public int SessionLifetimeMinutes { get; init; }

    /// <summary>What was typed, handed back when a refusal re-renders the form.</summary>
    public string? Purpose { get; init; }

    public string? Error { get; init; }

    /// <summary>
    /// The plaintext of a token that was just minted by this very response — never re-read from
    /// storage, because it is never stored. Null on every request except the one immediately after a
    /// successful mint.
    /// </summary>
    public string? MintedPlaintext { get; init; }

    public string? MintedPurpose { get; init; }
}

public sealed record SignInTokenRow(
    Guid Id,
    string Purpose,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RedeemedAt,
    DateTimeOffset? SessionExpiresAt,
    DateTimeOffset? RevokedAt,
    SignInTokenStatus Status);

/// <summary>The unauthenticated redemption page — shown when the link carried no token, or when one
/// presented (by link or by form) was refused.</summary>
public sealed class TokenSignInViewModel
{
    public string? Error { get; init; }
}
