using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbora.Infrastructure.Templates;

/// <summary>One environment variable a template declares.</summary>
/// <param name="Key">The variable name the application reads.</param>
/// <param name="Default">Value to start with, if any.</param>
/// <param name="Secret">Stored encrypted. Generated when no default is given, unless <see cref="Required"/>.</param>
/// <param name="Description">Shown next to the field, for values a person must supply.</param>
/// <param name="Required">
/// A secret with no default that only the person deploying can know — a third-party API key or a
/// bot token, never something Harbora can invent. Without this, a secret with no default is always
/// auto-generated (right for an internal value like an application key; silently wrong for a
/// Kavenegar API key or a Telegram bot token, which would deploy an app authenticating with a
/// random string against a service that never issued it). Ignored when <see cref="Secret"/> is
/// false — a plain variable with no default already asks.
/// </param>
public sealed record ManifestVariable(string Key, string? Default, bool Secret, string? Description, bool Required = false);

/// <summary>A directory whose contents must survive a redeploy.</summary>
public sealed record ManifestVolume(string MountPath);

/// <summary>
/// A template manifest, read into something the rest of the platform can act on.
///
/// The manifests always described more than the platform used: a static-site template declared a
/// volume for its content and an app created from it got none, so the site was empty again after
/// every redeploy. A framework template declared <c>APP_KEY (secret)</c> and the app was created
/// without it. The manifest was documentation that looked like configuration.
///
/// It is also validated where someone writes it rather than when a deploy fails an hour later —
/// which is the only moment the mistake is cheap to fix.
/// </summary>
public sealed class TemplateManifest
{
    public string? Image { get; init; }
    public string? Source { get; init; }
    public string? Service { get; init; }
    public int? Port { get; init; }

    /// <summary>
    /// What kind of deployable unit this template makes, in the same vocabulary as
    /// <c>Harbora.Domain.Common.ServiceKind</c>: "web" (the default — serves HTTP, gets a domain),
    /// "private" (reachable only inside the project's network) or "worker" (a long-running process
    /// with no inbound traffic — a queue consumer, or a Telegram bot polling instead of listening).
    /// Null means "web", matching every template written before this existed.
    /// </summary>
    public string? Kind { get; init; }
    public string? HealthPath { get; init; }
    public string? DocumentationUrl { get; init; }
    public string? WebsiteUrl { get; init; }
    public bool Featured { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<ManifestVariable> Variables { get; init; } = [];
    public IReadOnlyList<ManifestVolume> Volumes { get; init; } = [];
    public IReadOnlyList<string> Requires { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>
    /// Reads a manifest, or explains what is wrong with it. Never throws: this runs against text
    /// somebody typed.
    /// </summary>
    public static bool TryParse(string? json, out TemplateManifest? manifest, out IReadOnlyList<string> errors)
    {
        manifest = null;
        var problems = new List<string>();
        errors = problems;

        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(json) ? "{}" : json!, Json);
        }
        catch (JsonException ex)
        {
            problems.Add($"This is not valid JSON: {ex.Message}");
            return false;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add("A manifest is a JSON object, for example {\"image\": \"nginx:alpine\", \"port\": 80}.");
            return false;
        }

        var image = Text(root, "image");
        var source = Text(root, "source");
        var service = Text(root, "service");
        var port = Number(root, "port");

        if (string.IsNullOrWhiteSpace(image) && string.IsNullOrWhiteSpace(source) && string.IsNullOrWhiteSpace(service))
            problems.Add("A template needs \"image\", \"source\": \"git\", or a managed \"service\".");

        if (!string.IsNullOrWhiteSpace(source)
            && !source!.Equals("git", StringComparison.OrdinalIgnoreCase))
            problems.Add($"\"source\" must be \"git\"; \"{source}\" is not something Harbora can build.");

        if (root.TryGetProperty("port", out var portValue) && port is null)
            problems.Add("\"port\" must be a whole number.");

        // Out of range is worth catching here: the deploy fails much later, with a message about a
        // health check rather than about the template.
        if (port is < 1 or > 65535)
            problems.Add($"\"port\" must be between 1 and 65535, but is {port}.");

        var kind = Text(root, "kind");
        if (kind is { Length: > 0 }
            && kind.ToLowerInvariant() is not ("web" or "private" or "worker"))
            problems.Add($"\"kind\" must be \"web\", \"private\", or \"worker\", not \"{kind}\".");

        var variables = ReadVariables(root, problems);
        var volumes = ReadVolumes(root, problems);
        var requires = ReadRequires(root);
        var tags = ReadStrings(root, "tags");

        if (problems.Count > 0) return false;

        manifest = new TemplateManifest
        {
            Image = image, Source = source, Service = service, Port = port, Kind = kind,
            HealthPath = Text(root, "healthPath"),
            DocumentationUrl = Text(root, "documentation"),
            WebsiteUrl = Text(root, "website"),
            Featured = Flag(root, "featured"),
            Tags = tags, Variables = variables, Volumes = volumes, Requires = requires
        };
        return true;
    }

    private static List<ManifestVariable> ReadVariables(JsonElement root, List<string> problems)
    {
        var result = new List<ManifestVariable>();
        if (!root.TryGetProperty("env", out var env)) return result;

        if (env.ValueKind != JsonValueKind.Array)
        {
            problems.Add("\"env\" must be a list, for example [{\"key\": \"APP_ENV\", \"default\": \"production\"}].");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in env.EnumerateArray())
        {
            var key = Text(item, "key");
            if (string.IsNullOrWhiteSpace(key))
            {
                problems.Add("Every entry in \"env\" needs a \"key\".");
                continue;
            }

            // Duplicates are worth refusing rather than silently keeping the last one: which of the
            // two defaults applied would be invisible.
            if (!seen.Add(key!))
            {
                problems.Add($"\"{key}\" is listed twice in \"env\".");
                continue;
            }

            result.Add(new ManifestVariable(
                key!, Text(item, "default"), Flag(item, "secret"), Text(item, "description"),
                Required: Flag(item, "required")));
        }

        return result;
    }

    private static List<ManifestVolume> ReadVolumes(JsonElement root, List<string> problems)
    {
        var result = new List<ManifestVolume>();
        if (!root.TryGetProperty("volumes", out var volumes)) return result;

        if (volumes.ValueKind != JsonValueKind.Array)
        {
            problems.Add("\"volumes\" must be a list, for example [{\"mount\": \"/var/www/html\"}].");
            return result;
        }

        foreach (var item in volumes.EnumerateArray())
        {
            var mount = Text(item, "mount");
            if (string.IsNullOrWhiteSpace(mount))
            {
                problems.Add("Every entry in \"volumes\" needs a \"mount\" path.");
                continue;
            }

            // A relative path silently mounts somewhere nobody intended.
            if (!mount!.StartsWith('/'))
            {
                problems.Add($"\"{mount}\" must be an absolute path, starting with /.");
                continue;
            }

            result.Add(new ManifestVolume(mount));
        }

        return result;
    }

    private static List<string> ReadRequires(JsonElement root)
        => ReadStrings(root, "requires");

    private static List<string> ReadStrings(JsonElement root, string name)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(name, out var values) || values.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in values.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } value)
                result.Add(value);

        return result;
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static int? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : null;
}
