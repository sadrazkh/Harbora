using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;

namespace Harbora.Web.ViewModels;

/// <summary>
/// The external-access page for one managed database.
///
/// <see cref="Issued"/> is the only place a password ever appears, and it is populated only by the
/// action that just created or rotated one — never loaded from storage, because only its hash is
/// stored. It is deliberately not carried through TempData: this application's TempData is
/// cookie-backed, and a database password does not belong in a browser cookie.
/// </summary>
public sealed class DatabaseAccessPageViewModel
{
    public required ManagedService Database { get; init; }

    /// <summary>Newest first. Closed grants stay on the page — "who opened this in March" is a real question.</summary>
    public IReadOnlyList<DatabaseAccessGrant> Grants { get; init; } = [];

    public IReadOnlyList<DatabaseAccessAudit> History { get; init; } = [];

    /// <summary>Set when the platform cannot open this database to the outside, with the reason.</summary>
    public AccessUnavailable? Unavailable { get; init; }

    /// <summary>The credential just created, shown once and never again.</summary>
    public IssuedCredentialViewModel? Issued { get; init; }

    public string? Error { get; init; }
    public string? Message { get; init; }
}

/// <summary>
/// A password on its way to the screen for the first and last time.
///
/// <see cref="ConnectionString"/> contains the password, which is why it is assembled here and not
/// stored anywhere: the moment it is persisted, a copy of Harbora's database becomes a list of live
/// logins into customers' databases.
/// </summary>
public sealed record IssuedCredentialViewModel(
    string Username,
    string Password,
    string? ConnectionString,
    DateTimeOffset? ExpiresAt,
    bool Rotated);
