using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

/// <summary>
/// One identity at one external provider, bound to one Harbora account.
///
/// <para>
/// The pair that identifies the person is (<see cref="Provider"/>, <see cref="Subject"/>) — never the
/// email. A provider's subject is stable and belongs to the provider; an address is a display value
/// that can be changed, released and re-registered by somebody else. Matching on it is how an
/// external sign-in silently walks into an account that merely shares a mailbox, so this table is
/// keyed on the subject and the email below is kept for the settings page to show and for nothing to
/// decide by.
/// </para>
/// </summary>
public class ExternalLogin : BaseEntity
{
    /// <summary>One of <see cref="ExternalLoginProviders"/>, lower case.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>The provider's own stable identifier for the person. Unique within a provider.</summary>
    public string Subject { get; set; } = string.Empty;

    public Guid UserId { get; set; }
    public User? User { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>
    /// What the provider said the address was when the link was made. Shown on the account page so a
    /// person can tell which of their Google accounts this is; read by nothing that decides anything.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>What the provider called the person at link time. Display only, like <see cref="Email"/>.</summary>
    public string? DisplayName { get; set; }
}

/// <summary>The three providers the owner chose on 2026-08-20. The stored value is the key.</summary>
public static class ExternalLoginProviders
{
    public const string Google = "google";
    public const string GitHub = "github";

    /// <summary>Any OpenID Connect provider an operator points the panel at — Keycloak, Authentik, Entra.</summary>
    public const string Oidc = "oidc";

    public static readonly IReadOnlyList<string> All = [Google, GitHub, Oidc];

    /// <summary>The stored spelling of <paramref name="provider"/>, or null when it is not one of ours.</summary>
    public static string? Normalise(string? provider)
    {
        var value = (provider ?? "").Trim().ToLowerInvariant();
        return All.Contains(value) ? value : null;
    }

    /// <summary>
    /// How the provider is named on screen. Google and GitHub are proper nouns and stay themselves in
    /// both languages; the generic provider is whatever the operator called it, and only falls back to
    /// a word when they named nothing.
    /// </summary>
    public static string DisplayName(string provider, string? oidcName, bool isFa) => provider switch
    {
        Google => "Google",
        GitHub => "GitHub",
        _ => string.IsNullOrWhiteSpace(oidcName) ? (isFa ? "ورود یکپارچه" : "Single sign-on") : oidcName.Trim()
    };
}
