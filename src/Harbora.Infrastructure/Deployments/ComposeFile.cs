namespace Harbora.Infrastructure.Deployments;

/// <summary>One service from a Compose file, reduced to what Harbora can actually run.</summary>
public sealed class ComposeService
{
    public required string Name { get; init; }

    /// <summary>Image to run. Mutually exclusive with <see cref="Build"/>.</summary>
    public string? Image { get; set; }

    /// <summary>Build context, when the service is built from source in the repo.</summary>
    public string? Build { get; set; }

    /// <summary>Dockerfile inside <see cref="Build"/>, when given.</summary>
    public string? Dockerfile { get; set; }

    /// <summary>Container port this service listens on, taken from the first published port.</summary>
    public int? Port { get; set; }

    public Dictionary<string, string> Environment { get; } = new(StringComparer.Ordinal);

    /// <summary>Named volumes only: (volume, mountPath). Host bind mounts are refused.</summary>
    public List<(string Volume, string MountPath)> Volumes { get; } = [];

    public List<string> DependsOn { get; } = [];

    /// <summary>Command override, already split into argv.</summary>
    public List<string> Command { get; } = [];

    /// <summary>Whether this is the service that receives inbound traffic.</summary>
    public bool IsWeb { get; set; }
}

public sealed class ComposeParseResult
{
    public List<ComposeService> Services { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];

    public bool IsValid => Errors.Count == 0 && Services.Count > 0;

    /// <summary>The service traffic is routed to, if one could be determined.</summary>
    public ComposeService? Web => Services.FirstOrDefault(s => s.IsWeb);
}

/// <summary>
/// Reads the subset of Compose that Harbora can honestly run, and says plainly what it cannot.
///
/// This is deliberately an allowlist rather than a best-effort parser. A Compose file that silently
/// loses <c>cap_add</c>, <c>network_mode: host</c> or a bind mount would deploy something other than
/// what the author wrote — which is worse than refusing, because the difference only shows up later
/// and looks like a platform bug. Anything not understood is reported, with the directive named.
///
/// Parsed by hand for the same reason the CLI config is: the accepted shape is small, and the errors
/// need to name the offending service and key rather than a YAML node path.
/// </summary>
public static class ComposeFile
{
    /// <summary>Top-level keys we understand. Everything else is reported.</summary>
    private static readonly HashSet<string> TopLevel =
        new(StringComparer.OrdinalIgnoreCase) { "version", "services", "volumes", "networks", "name" };

    /// <summary>Service keys we honour.</summary>
    private static readonly HashSet<string> ServiceKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "image", "build", "ports", "environment", "env_file", "volumes",
        "depends_on", "command", "restart", "container_name", "networks", "expose", "labels"
    };

    /// <summary>
    /// Keys we refuse outright: each one changes isolation or host access in a way a shared platform
    /// cannot grant. Named individually so the message can explain the specific problem.
    /// </summary>
    private static readonly Dictionary<string, string> Refused = new(StringComparer.OrdinalIgnoreCase)
    {
        ["privileged"] = "privileged containers are not allowed on a shared platform",
        ["network_mode"] = "network_mode bypasses per-tenant network isolation",
        ["pid"] = "sharing the host PID namespace is not allowed",
        ["ipc"] = "sharing the host IPC namespace is not allowed",
        ["cap_add"] = "adding kernel capabilities is not allowed",
        ["devices"] = "host device access is not allowed",
        ["userns_mode"] = "changing the user namespace is not allowed",
        ["security_opt"] = "security_opt can disable the sandbox",
        ["sysctls"] = "sysctls affect the host kernel",
        ["extra_hosts"] = "extra_hosts can be used to spoof internal names"
    };

    public static ComposeParseResult Parse(string yaml)
    {
        var result = new ComposeParseResult();
        var lines = yaml.Replace("\r\n", "\n").Split('\n');

        string? currentService = null;
        string? currentKey = null;
        var inServices = false;
        var serviceIndent = -1;

        foreach (var raw in lines)
        {
            var line = StripComment(raw);
            if (line.Trim().Length == 0) continue;

            var indent = line.Length - line.TrimStart().Length;
            var text = line.Trim();

            // ---- top level ----
            if (indent == 0)
            {
                inServices = false;
                currentService = null;
                currentKey = null;

                var key = text.TrimEnd(':').Split(':')[0].Trim();
                if (!TopLevel.Contains(key))
                {
                    result.Warnings.Add($"Ignoring unsupported top-level key '{key}'.");
                    continue;
                }
                inServices = key.Equals("services", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inServices) continue;

            // ---- a service name ----
            if (serviceIndent == -1 || indent == serviceIndent)
            {
                if (text.EndsWith(':') && !text.StartsWith("- "))
                {
                    serviceIndent = indent;
                    currentService = text.TrimEnd(':').Trim();
                    currentKey = null;
                    result.Services.Add(new ComposeService { Name = currentService });
                    continue;
                }
            }

            if (currentService is null) continue;
            var service = result.Services[^1];

            // ---- list items belonging to the previous key ----
            if (text.StartsWith("- "))
            {
                ApplyListItem(service, currentKey, text[2..].Trim(), result);
                continue;
            }

            // ---- key: value inside a service ----
            var colon = text.IndexOf(':');
            if (colon <= 0) continue;
            var name = text[..colon].Trim();
            var value = text[(colon + 1)..].Trim();

            if (Refused.TryGetValue(name, out var why))
            {
                result.Errors.Add($"Service '{service.Name}': '{name}' is not supported — {why}.");
                continue;
            }

            if (!ServiceKeys.Contains(name))
            {
                // A nested key under environment/labels is a value, not an unknown directive.
                if (currentKey is "environment" or "labels")
                {
                    if (currentKey == "environment") service.Environment[name] = Unquote(value);
                    continue;
                }
                result.Warnings.Add($"Service '{service.Name}': ignoring unsupported key '{name}'.");
                continue;
            }

            currentKey = name.ToLowerInvariant();
            if (value.Length == 0) continue;   // opens a block or list

            ApplyScalar(service, currentKey, Unquote(value), result);
        }

        Validate(result);
        return result;
    }

    private static void ApplyScalar(ComposeService service, string key, string value, ComposeParseResult result)
    {
        switch (key)
        {
            case "image": service.Image = value; break;
            case "build": service.Build = value; break;
            case "container_name":
                // Names are assigned by Harbora so old and new can coexist during a cutover.
                result.Warnings.Add($"Service '{service.Name}': container_name is ignored — Harbora names containers.");
                break;
            case "command": service.Command.AddRange(SplitCommand(value)); break;
            case "restart" or "networks" or "expose" or "labels" or "env_file":
                // Accepted and handled by the platform's own policy.
                break;
        }
    }

    private static void ApplyListItem(ComposeService service, string? key, string item, ComposeParseResult result)
    {
        item = Unquote(item);
        switch (key)
        {
            case "ports":
                // "8080:80" or "80" — the container side is what we route to.
                var parts = item.Split(':');
                var containerSide = parts[^1].Split('/')[0];
                if (int.TryParse(containerSide, out var port)) service.Port ??= port;
                break;

            case "environment":
                var eq = item.IndexOf('=');
                if (eq > 0) service.Environment[item[..eq]] = item[(eq + 1)..];
                break;

            case "volumes":
                var v = item.Split(':');
                if (v.Length < 2) { result.Warnings.Add($"Service '{service.Name}': ignoring volume '{item}'."); break; }
                var source = v[0];
                // A bind mount would expose the host filesystem to a tenant.
                if (source.StartsWith('.') || source.StartsWith('/') || source.Contains('\\'))
                    result.Errors.Add(
                        $"Service '{service.Name}': bind mount '{item}' is not supported — " +
                        "use a named volume, host paths are not exposed to apps.");
                else
                    service.Volumes.Add((source, v[1]));
                break;

            case "depends_on": service.DependsOn.Add(item); break;
            case "command": service.Command.Add(item); break;
        }
    }

    private static void Validate(ComposeParseResult result)
    {
        if (result.Services.Count == 0)
        {
            result.Errors.Add("No services found. A compose file needs a 'services:' section.");
            return;
        }

        foreach (var service in result.Services)
        {
            if (service.Image is null && service.Build is null)
                result.Errors.Add($"Service '{service.Name}' has neither 'image' nor 'build'.");
            if (service.Image is not null && service.Build is not null)
                result.Errors.Add($"Service '{service.Name}' sets both 'image' and 'build' — pick one.");
        }

        // Exactly one service takes inbound traffic. Choosing implicitly and silently would be a
        // coin flip, so it is only automatic when there is no ambiguity.
        var published = result.Services.Where(s => s.Port is not null).ToList();
        if (published.Count == 1)
        {
            published[0].IsWeb = true;
        }
        else if (published.Count > 1)
        {
            var named = published.FirstOrDefault(s =>
                s.Name.Equals("web", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals("app", StringComparison.OrdinalIgnoreCase) ||
                s.Name.Equals("frontend", StringComparison.OrdinalIgnoreCase));
            if (named is not null) named.IsWeb = true;
            else
                result.Errors.Add(
                    $"{published.Count} services publish ports ({string.Join(", ", published.Select(s => s.Name))}). " +
                    "Name the one that should receive traffic 'web' so routing isn't a guess.");
        }
        else
        {
            result.Errors.Add("No service publishes a port, so there is nothing to route traffic to.");
        }
    }

    private static IEnumerable<string> SplitCommand(string value)
    {
        value = value.Trim();
        // JSON-array form: ["npm", "start"]
        if (value.StartsWith('[') && value.EndsWith(']'))
            return value[1..^1].Split(',').Select(v => Unquote(v.Trim())).Where(v => v.Length > 0);
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static string StripComment(string line)
    {
        var hash = line.IndexOf('#');
        if (hash < 0) return line;
        var quotes = line[..hash].Count(c => c is '"' or '\'');
        return quotes % 2 == 1 ? line : line[..hash];
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;
}
