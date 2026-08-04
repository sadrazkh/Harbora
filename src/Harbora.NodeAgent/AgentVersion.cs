using System.Reflection;

namespace Harbora.NodeAgent;

/// <summary>
/// The version this build reports. Read from the assembly rather than a constant so the number in
/// <c>Directory.Build.props</c> is the only place it is written — a version the binary claims but
/// the release did not build is worse than no version at all.
/// </summary>
public static class AgentVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AgentVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // The SDK appends "+<commit sha>" to the informational version; the contract wants semver.
        if (informational is { Length: > 0 })
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return typeof(AgentVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Compares two dotted versions numerically. Returns negative when <paramref name="left"/> is
    /// older. A pre-release suffix is ignored: "is this node too old to trust" is a question about
    /// the release line, and treating <c>1.2.0-rc1</c> as older than <c>1.2.0</c> would ground a
    /// node for a difference that does not change the protocol.
    /// </summary>
    public static int Compare(string? left, string? right)
    {
        var a = Parse(left);
        var b = Parse(right);

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var x = i < a.Length ? a[i] : 0;
            var y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    /// <summary>True when <paramref name="version"/> is at least <paramref name="minimum"/>.</summary>
    public static bool IsAtLeast(string? version, string? minimum) =>
        string.IsNullOrWhiteSpace(minimum) || Compare(version, minimum) >= 0;

    private static int[] Parse(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return [0];

        var core = version.Split('-', '+')[0];
        return core.Split('.')
            .Select(part => int.TryParse(part, out var value) ? value : 0)
            .ToArray();
    }
}
