using System.Text.Json;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Resolves an <see cref="Harbora.Domain.Templates.AppTemplate"/> JSON manifest into a concrete
/// deploy spec (ADR-014 / fixes C3 for single-container templates). Pure + unit-tested. Manifests
/// look like: {"image":"nginx:alpine","port":80}, {"source":"git","port":3000},
/// {"service":"postgres",...} or {"image":"wordpress","requires":["mariadb"]}.
/// </summary>
public static class TemplateResolver
{
    public enum TemplateKind { Image, Git, ManagedService, Unsupported }

    public sealed record TemplateDeploySpec(TemplateKind Kind, string? Image, int? Port, string? Reason);

    public static TemplateDeploySpec Resolve(string manifestJson)
    {
        JsonElement root;
        try
        {
            root = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(manifestJson) ? "{}" : manifestJson);
        }
        catch (JsonException)
        {
            return new TemplateDeploySpec(TemplateKind.Unsupported, null, null,
                "The template manifest is not valid JSON.");
        }

        int? Port() =>
            root.TryGetProperty("port", out var p) && p.TryGetInt32(out var n) ? n : null;

        string? Str(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;

        // A managed backing service (postgres/redis/…): provisioned from the Databases page, not an app.
        if (!string.IsNullOrWhiteSpace(Str("service")))
            return new TemplateDeploySpec(TemplateKind.ManagedService, null, Port(),
                "This template provisions a managed database/cache — create it from the Databases page.");

        var image = Str("image");
        if (!string.IsNullOrWhiteSpace(image))
            return new TemplateDeploySpec(TemplateKind.Image, image, Port(), null);

        if (string.Equals(Str("source"), "git", StringComparison.OrdinalIgnoreCase))
            return new TemplateDeploySpec(TemplateKind.Git, null, Port(), null);

        return new TemplateDeploySpec(TemplateKind.Unsupported, null, Port(),
            "This template has no deployable image or Git source.");
    }
}
