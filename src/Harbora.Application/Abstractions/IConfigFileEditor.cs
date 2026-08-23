using Harbora.Domain.Configuration;

namespace Harbora.Application.Abstractions;

/// <summary>
/// Reads and edits one config-file format for C2 (2026-08-22 config-delivery plan). One
/// implementation per <see cref="ConfigFileFormat"/>, each speaking that format's own key-path
/// idiom — never one syntax forced onto all five, per the owner's binding correction.
///
/// <para>
/// Every implementation runs a real parser for its format, never a regex over the whole file: a
/// <c>docker-compose.yml</c>-style hand-rolled allowlist parser is the wrong shape here because a
/// developer's own config file is not one Harbora controls the grammar of — it can use anything the
/// format allows, and an override must either understand that or refuse cleanly, never mangle it
/// silently.
/// </para>
/// </summary>
public interface IConfigFileEditor
{
    ConfigFileFormat Format { get; }

    /// <summary>
    /// Parses <paramref name="content"/> and reports what it actually contains: every key path
    /// present (for "show what was actually there" when a key is missed), and — when
    /// <paramref name="keyPath"/> is given — whether it resolves and its current value. Used both by
    /// deploy-time resolution and by the panel's "validate a rule against the deployed app" feature.
    /// </summary>
    ConfigFileInspection Inspect(string content, string? keyPath);

    /// <summary>
    /// Parses, locates <paramref name="keyPath"/>, and replaces its value, returning the new file
    /// content with everything else — comments, ordering, formatting — kept as recognisable as this
    /// format's own parser allows. Never partially applies: a failure returns the original content
    /// untouched.
    /// </summary>
    ConfigFileEditOutcome Apply(string content, string keyPath, string newValue);
}

/// <summary>A parser's own complaint, carried whole rather than swallowed into "could not parse".</summary>
public sealed record ConfigFileParseError(string Message, int? Line, int? Column)
{
    public override string ToString() => Line is { } l
        ? $"{Message} (line {l}{(Column is { } c ? $", column {c}" : string.Empty)})"
        : Message;
}

/// <summary>
/// What <see cref="IConfigFileEditor.Inspect"/> found. <see cref="ParseError"/> is set only when
/// <see cref="Parsed"/> is false; <see cref="CurrentValue"/> is set only when <see cref="KeyFound"/>
/// is true. <see cref="KeyPaths"/> is always populated when the file parsed, whether or not a
/// specific key path was asked for — it is the "what was actually there" list.
/// </summary>
public sealed record ConfigFileInspection(
    bool Parsed,
    ConfigFileParseError? ParseError,
    IReadOnlyList<string> KeyPaths,
    bool KeyFound,
    string? CurrentValue)
{
    public static ConfigFileInspection ParseFailure(ConfigFileParseError error) =>
        new(false, error, [], false, null);
}

/// <summary>
/// What <see cref="IConfigFileEditor.Apply"/> produced. Exactly one of three shapes: success
/// (<see cref="Ok"/>, <see cref="NewContent"/> set), a parse failure (<see cref="ParseError"/> set),
/// or a key-path miss (<see cref="KeyPaths"/> populated, <see cref="KeyFound"/> false) — the caller
/// (<c>ConfigOverrideResolver</c>) turns whichever one it is into the matching
/// <see cref="ConfigOverrideFailureReason"/>.
/// </summary>
public sealed record ConfigFileEditOutcome(
    bool Ok,
    string? NewContent,
    ConfigFileParseError? ParseError,
    bool KeyFound,
    IReadOnlyList<string> KeyPaths)
{
    public static ConfigFileEditOutcome Success(string newContent) => new(true, newContent, null, true, []);

    public static ConfigFileEditOutcome ParseFailure(ConfigFileParseError error) =>
        new(false, null, error, false, []);

    public static ConfigFileEditOutcome KeyNotFound(IReadOnlyList<string> keyPaths) =>
        new(false, null, null, false, keyPaths);
}
