namespace Harbora.Modules.Backup.Domain;

/// <summary>
/// Allowlists for the values that end up as arguments to an engine process.
///
/// <para>
/// The primary defence against command injection is structural — arguments are passed as a list and
/// no shell is ever spawned, so <c>;</c> and <c>&amp;&amp;</c> are ordinary characters (THREAT_MODEL
/// T1). This class is the second layer: it keeps values that have no business containing exotic
/// characters from acquiring them, so that a future refactor which reintroduces a shell somewhere
/// does not immediately become an RCE.
/// </para>
/// <para>
/// Defence in depth is the whole point. Neither layer is assumed sufficient alone.
/// </para>
/// </summary>
public static class EngineArgumentGuard
{
    /// <summary>Repository and policy names: letters, digits, and a few separators.</summary>
    public static bool IsSafeName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ' ')
        && !value.StartsWith('-')
        && !value.Contains("..", StringComparison.Ordinal);

    /// <summary>
    /// Engine snapshot identifiers. Hex-ish handles from Kopia, or a GUID from the native engine.
    /// </summary>
    public static bool IsSafeSnapshotId(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
        && !value.StartsWith('-');

    /// <summary>S3 bucket names, per the stricter of the AWS naming rules.</summary>
    public static bool IsSafeBucket(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length is >= 3 and <= 63
        && value.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
        && !value.StartsWith('-') && !value.EndsWith('-')
        && !value.StartsWith('.') && !value.EndsWith('.');

    /// <summary>Docker volume names, matching the daemon's own rule.</summary>
    public static bool IsSafeVolumeName(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 255
        && char.IsAsciiLetterOrDigit(value[0])
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    /// <summary>
    /// Guards a value that will be handed to a process, throwing rather than returning false.
    ///
    /// <para>
    /// Throwing is deliberate: this is called on the path to spawning a process, where a caller that
    /// ignores a returned <c>false</c> has produced precisely the bug the check exists to prevent.
    /// </para>
    /// </summary>
    public static string Require(string? value, Func<string?, bool> rule, string what)
    {
        if (!rule(value))
            throw new ArgumentException(
                $"{what} contains characters that are not permitted in an engine argument.", nameof(value));

        return value!;
    }

    /// <summary>
    /// Prefix a value that may legitimately begin with <c>-</c> so the engine reads it as a value
    /// rather than a flag. Callers place <c>--</c> before positional arguments; this covers the rest.
    /// </summary>
    public static bool LooksLikeFlag(string? value) => value is not null && value.StartsWith('-');
}
