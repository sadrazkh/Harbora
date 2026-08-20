using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Harbora.Infrastructure.Networking;

/// <summary>One zone a token can see.</summary>
public sealed record CloudflareZone(string Id, string Name);

/// <summary>One DNS record, trimmed to the fields the panel shows or writes.</summary>
public sealed record CloudflareDnsRecord(
    string Id, string Type, string Name, string Content, int Ttl, int? Priority, bool Proxied);

/// <summary>
/// The Cloudflare v4 REST calling convention — bearer token, one error shape, one success envelope —
/// shared by every Cloudflare-token holder Harbora has: the platform's own token
/// (<see cref="CloudflarePlatformService"/>) and, as of F9 (2026-08-21 functions-and-services plan),
/// a workspace's own bring-your-own token (<see cref="CustomerCloudflareService"/>).
///
/// <para>
/// This is the "extend the client's shape, don't fork it" the F9 plan asked for: one HTTP transport,
/// two entirely separate credential stores. The platform's token lives in <c>Setting</c> rows and
/// routes the panel's and S3's own TLS; a workspace's token lives in
/// <see cref="Harbora.Domain.Networking.CustomerDnsCredential"/> and never touches platform routing.
/// Neither service holds the other's credential, and this class holds no credential at all — every
/// call is handed a token by its caller.
/// </para>
/// </summary>
public sealed class CloudflareApiClient(IHttpClientFactory clients)
{
    private const string Api = "https://api.cloudflare.com/client/v4/";

    public async Task<JsonDocument> SendAsync(
        string token, HttpMethod method, string path, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Api + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        var client = clients.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(15);
        using var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        var document = JsonDocument.Parse(body);
        var success = response.IsSuccessStatusCode &&
                      document.RootElement.TryGetProperty("success", out var ok) && ok.GetBoolean();
        if (success) return document;

        var reason = document.RootElement.TryGetProperty("errors", out var errors)
            ? string.Join("; ", errors.EnumerateArray().Select(e =>
                e.TryGetProperty("message", out var message) ? message.GetString() : e.ToString()))
            : $"HTTP {(int)response.StatusCode}";
        document.Dispose();
        throw new InvalidOperationException("Cloudflare refused the request: " + reason);
    }

    /// <summary>Throws unless the token authenticates at all. Does not imply it can see anything.</summary>
    public async Task VerifyTokenAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(token, HttpMethod.Get, "user/tokens/verify", null, ct);
    }

    /// <summary>
    /// Every active zone the token can see. A verified-but-narrow token (e.g. `Zone:Read` on nothing,
    /// or a revoked grant) answers this with an empty, still-successful list — callers decide what an
    /// empty list means to them, this method does not fabricate a reason.
    /// </summary>
    public async Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(string token, CancellationToken ct)
    {
        using var doc = await SendAsync(token, HttpMethod.Get, "zones?status=active&per_page=50", null, ct);
        return doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(z => new CloudflareZone(
                z.GetProperty("id").GetString() ?? "",
                z.GetProperty("name").GetString() ?? ""))
            .ToList();
    }

    /// <summary>The id of the one active zone matching <paramref name="zoneName"/> exactly.</summary>
    public async Task<string> FindZoneIdAsync(string token, string zoneName, CancellationToken ct)
    {
        using var zones = await SendAsync(token, HttpMethod.Get,
            $"zones?name={Uri.EscapeDataString(zoneName)}&status=active&per_page=1", null, ct);
        var result = zones.RootElement.GetProperty("result");
        if (result.GetArrayLength() == 0)
            throw new InvalidOperationException(
                $"The token is valid but cannot read active zone {zoneName}. Add Zone:Read for this zone.");
        return result[0].GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Cloudflare returned a zone without an id.");
    }

    /// <summary>Every DNS record in the zone. Callers filter to the record types they support.</summary>
    public async Task<IReadOnlyList<CloudflareDnsRecord>> ListDnsRecordsAsync(
        string token, string zoneId, CancellationToken ct)
    {
        using var doc = await SendAsync(token, HttpMethod.Get,
            $"zones/{zoneId}/dns_records?per_page=100", null, ct);
        return doc.RootElement.GetProperty("result").EnumerateArray()
            .Select(ParseRecord)
            .ToList();
    }

    /// <summary>Creates one DNS record and returns its new id. <paramref name="priority"/> is ignored
    /// unless <paramref name="type"/> is MX, matching what Cloudflare itself accepts.</summary>
    public async Task<string> CreateDnsRecordAsync(
        string token, string zoneId, string type, string name, string content, int ttl, int? priority,
        CancellationToken ct)
    {
        var body = new StringBuilder("{")
            .Append($"\"type\":{JsonSerialize(type)},")
            .Append($"\"name\":{JsonSerialize(name)},")
            .Append($"\"content\":{JsonSerialize(content)},")
            .Append($"\"ttl\":{(ttl <= 0 ? 1 : ttl)}");
        if (type == "MX" && priority is { } p) body.Append($",\"priority\":{p}");
        body.Append('}');

        using var doc = await SendAsync(token, HttpMethod.Post, $"zones/{zoneId}/dns_records", body.ToString(), ct);
        return doc.RootElement.GetProperty("result").GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Cloudflare created the record but returned no id.");
    }

    public async Task DeleteDnsRecordAsync(string token, string zoneId, string recordId, CancellationToken ct)
    {
        using var doc = await SendAsync(
            token, HttpMethod.Delete, $"zones/{zoneId}/dns_records/{recordId}", null, ct);
    }

    private static CloudflareDnsRecord ParseRecord(JsonElement r) => new(
        r.GetProperty("id").GetString() ?? "",
        r.GetProperty("type").GetString() ?? "",
        r.GetProperty("name").GetString() ?? "",
        r.TryGetProperty("content", out var content) ? content.GetString() ?? "" : "",
        r.TryGetProperty("ttl", out var ttl) ? ttl.GetInt32() : 1,
        r.TryGetProperty("priority", out var priority) && priority.ValueKind == JsonValueKind.Number
            ? priority.GetInt32()
            : null,
        r.TryGetProperty("proxied", out var proxied) && proxied.ValueKind == JsonValueKind.True);

    private static string JsonSerialize(string value) => JsonSerializer.Serialize(value);
}
