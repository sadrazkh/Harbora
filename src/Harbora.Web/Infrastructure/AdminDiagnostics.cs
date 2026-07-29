using System.Text.RegularExpressions;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Formatting helpers for <see cref="AdminCommands"/>. Separated because they are the parts with
/// security consequences: this output is exactly what an operator pastes into a bug report when
/// they are locked out, so it must never contain a live credential.
/// </summary>
public static partial class AdminDiagnostics
{
    /// <summary>Well-known insecure key prefixes that must never be accepted in production.</summary>
    public const string DevKeyPrefix = "dev-insecure";

    /// <summary>
    /// Describes the master key without revealing it. Missing/insecure are called out by name
    /// because a missing key is the single most common reason the panel won't start after an update.
    /// </summary>
    public static string DescribeMasterKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "MISSING — the panel will refuse to start. Run: harbora fix-key";
        if (key.StartsWith(DevKeyPrefix, StringComparison.Ordinal))
            return "INSECURE DEFAULT — rejected in production. Run: harbora fix-key";
        return $"set ({key.Trim().Length} chars)";
    }

    /// <summary>
    /// Strips the password out of a connection string so the rest stays useful for diagnosis.
    /// Handles the spacing and casing variants Npgsql accepts, and the <c>pwd</c> alias.
    /// </summary>
    public static string RedactConnectionString(string? connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString)) return "(not set)";
        return PasswordPattern().Replace(connectionString, "$1***");
    }

    [GeneratedRegex(@"(?i)\b(password\s*=\s*|pwd\s*=\s*)[^;]*")]
    private static partial Regex PasswordPattern();
}
