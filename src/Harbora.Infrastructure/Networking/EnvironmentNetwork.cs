namespace Harbora.Infrastructure.Networking;

/// <summary>
/// The private network a project's environment runs on.
///
/// Until now every service a tenant owned shared one network, which meant staging could reach
/// production's database by name — the isolation stopped at the tenant boundary and went no further.
/// An environment is the right boundary: it is what people mean when they say "production".
///
/// Names are bounded and sanitised because Docker rejects a network name over 63 characters, and the
/// failure arrives mid-deploy rather than when the project was named.
/// </summary>
public static class EnvironmentNetwork
{
    /// <summary>Docker's limit for a network name.</summary>
    public const int MaxLength = 63;

    private const string Prefix = "harbora-env-";

    /// <summary>
    /// The network for one environment. The id is part of the name so two projects that slugify to
    /// the same thing — "My App" and "my-app" — cannot end up sharing a network, which would be
    /// exactly the isolation failure this replaces.
    /// </summary>
    public static string For(string? projectSlug, string? environmentSlug, Guid environmentId)
    {
        var project = Clean(projectSlug);
        var environment = Clean(environmentSlug);
        var id = environmentId.ToString("N")[..8];

        var name = $"{Prefix}{project}-{environment}-{id}";
        if (name.Length <= MaxLength) return name;

        // Trimmed from the descriptive part, never from the id: the id is what makes it unique.
        var room = MaxLength - Prefix.Length - id.Length - 2;
        var descriptive = $"{project}-{environment}";
        return $"{Prefix}{descriptive[..Math.Max(0, Math.Min(room, descriptive.Length))]}-{id}";
    }

    /// <summary>True for a name this class produced — used to tell our networks from a workspace one.</summary>
    public static bool IsEnvironmentNetwork(string? name) =>
        name is not null && name.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Lowercase letters, digits and dashes only. Docker accepts more, but a name that travels
    /// through shell commands and label filters is safer without it.
    /// </summary>
    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "x";

        var cleaned = new string(value.Trim().ToLowerInvariant()
            .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');

        return cleaned.Length == 0 ? "x" : cleaned;
    }
}
