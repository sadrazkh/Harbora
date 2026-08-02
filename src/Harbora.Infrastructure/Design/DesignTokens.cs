using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Design;

/// <summary>
/// Reads the theme blocks out of the stylesheet so the palette can be asserted against.
///
/// Parsing the real file rather than duplicating the values in C# is the entire point: a copy would
/// drift, and a test that passes against a copy of the palette proves nothing about the palette.
/// </summary>
public static class DesignTokens
{
    private static readonly Regex LightBlock =
        new(@":root\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DarkBlock =
        new(@"html\.dark\s*\{(?<body>[^}]*)\}", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex Declaration =
        new(@"(?<name>--[a-z0-9-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Theme name → token name → raw value, exactly as written in the stylesheet.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Parse(string css) =>
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            ["light"] = Block(css, LightBlock),
            ["dark"] = Block(css, DarkBlock)
        };

    private static IReadOnlyDictionary<string, string> Block(string css, Regex block)
    {
        var match = block.Match(css);
        if (!match.Success) return new Dictionary<string, string>();

        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match declaration in Declaration.Matches(match.Groups["body"].Value))
            tokens[declaration.Groups["name"].Value] = declaration.Groups["value"].Value.Trim();

        return tokens;
    }
}
