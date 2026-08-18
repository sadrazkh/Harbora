using System.Text.Json;

namespace Harbora.Domain.Networking;

/// <summary>
/// Reads and writes <see cref="Route.ExtraUpstreamsJson"/> — the only code that should ever touch
/// that string directly, so the shape of the JSON is decided in exactly one place.
/// </summary>
public static class RouteUpstreams
{
    /// <summary>One server behind a route's loadBalancer.</summary>
    public sealed record Upstream(string Host, int Port);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Every upstream this route carries, in order: the primary <see cref="Route.TargetService"/>/
    /// <see cref="Route.TargetPort"/> first, then whatever <see cref="Route.ExtraUpstreamsJson"/>
    /// adds. Always at least one entry for a route with a non-empty <see cref="Route.TargetService"/>.
    /// </summary>
    public static IReadOnlyList<Upstream> All(Route route)
    {
        var list = new List<Upstream>();
        if (!string.IsNullOrWhiteSpace(route.TargetService))
            list.Add(new Upstream(route.TargetService, route.TargetPort));
        list.AddRange(Deserialize(route.ExtraUpstreamsJson));
        return list;
    }

    /// <summary>
    /// The JSON to store in <see cref="Route.ExtraUpstreamsJson"/> for these extra upstreams (every
    /// one beyond the primary target) — null when there are none, so an ordinary single-target route
    /// keeps the column empty rather than holding an empty array forever.
    /// </summary>
    public static string? Serialize(IReadOnlyList<Upstream> extra) =>
        extra.Count == 0 ? null : JsonSerializer.Serialize(extra, JsonOptions);

    /// <summary>Never throws: a corrupted or hand-edited value reads back as no extra upstreams.</summary>
    public static IReadOnlyList<Upstream> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Upstream>>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }
}
