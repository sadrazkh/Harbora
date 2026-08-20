namespace Harbora.Infrastructure.Security;

/// <summary>
/// The two rules that decide whether an account may lose a way in, kept apart from the controller
/// because both are asked in more than one place and a second copy of either is how a door gets
/// bricked up from the inside.
/// </summary>
public static class ExternalLoginPolicy
{
    /// <summary>
    /// Whether this stored hash can ever answer "yes" to a password.
    ///
    /// <para>
    /// An account provisioned by an external provider has never had a password, and the column is not
    /// nullable, so it holds an empty string. <see cref="Pbkdf2PasswordHasher.Verify"/> reads that as
    /// a refusal — which is right — but "the sign-in form will always say no" is exactly the fact the
    /// unlink refusal has to know, so it is asked here in the same shape the hasher parses.
    /// </para>
    /// </summary>
    public static bool HasUsablePassword(string? passwordHash) =>
        !string.IsNullOrWhiteSpace(passwordHash) && passwordHash.Split('.', 3).Length == 3;

    /// <summary>
    /// True when unlinking would leave the account with no way to sign in at all — no password the
    /// login form would accept, and no other provider left.
    ///
    /// <para>
    /// This is the refusal, not a warning: a person who unlinks their only provider from an account
    /// that never had a password is locked out of it permanently, and the panel has no
    /// administrator-side "set a password for somebody else" to undo it with.
    /// </para>
    /// </summary>
    public static bool WouldLeaveNoWayIn(bool hasUsablePassword, int otherLinksRemaining) =>
        !hasUsablePassword && otherLinksRemaining <= 0;
}
