using System.Text.RegularExpressions;
using Harbora.Infrastructure.Services;

namespace Harbora.Infrastructure.Templates;

/// <summary>
/// Resolves Railway-style references in template variables, for example
/// <c>${{postgres.host}}</c> or <c>redis://${{redis.host}}:${{redis.port}}</c>.
/// Values are assembled in memory while a stack is created; secrets are encrypted before the
/// resulting application is saved.
/// </summary>
public static partial class TemplateReferences
{
    public static IReadOnlyDictionary<string, string> For(string alias, ServiceCreds credentials)
    {
        var prefix = alias.Trim().ToLowerInvariant();
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [$"{prefix}.host"] = credentials.Host,
            [$"{prefix}.port"] = credentials.Port.ToString(),
            [$"{prefix}.user"] = credentials.User,
            [$"{prefix}.password"] = credentials.Password,
            [$"{prefix}.database"] = credentials.Database
        };
    }

    public static string Resolve(string value, IReadOnlyDictionary<string, string> references,
        out IReadOnlyList<string> missing)
    {
        var unresolved = new List<string>();
        var result = Reference().Replace(value, match =>
        {
            var key = match.Groups["key"].Value.Trim();
            if (references.TryGetValue(key, out var replacement)) return replacement;
            unresolved.Add(key);
            return match.Value;
        });

        missing = unresolved.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return result;
    }

    [GeneratedRegex(@"\$\{\{\s*(?<key>[a-zA-Z0-9_.-]+)\s*\}\}")]
    private static partial Regex Reference();
}
