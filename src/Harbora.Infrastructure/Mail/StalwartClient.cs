using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Harbora.Infrastructure.Mail;

public sealed record StalwartResult(bool Succeeded, string? Id, string? Error);
public sealed record StalwartDnsResult(bool Succeeded, string? Zone, string? Error);

/// <summary>Small, typed client for Stalwart's management JMAP endpoint.</summary>
public sealed class StalwartClient(IHttpClientFactory clients)
{
    private static readonly string[] Capabilities = ["urn:ietf:params:jmap:core", "urn:stalwart:jmap"];

    public Task<StalwartResult> CreateDomainAsync(
        string baseUrl, string user, string password, string domain, CancellationToken ct) =>
        SetAsync(baseUrl, user, password, "x:Domain", new Dictionary<string, object?>
        {
            ["name"] = domain,
            ["aliases"] = new Dictionary<string, object>(),
            ["certificateManagement"] = new Dictionary<string, object> { ["@type"] = "Automatic" },
            ["dkimManagement"] = new Dictionary<string, object> { ["@type"] = "Automatic" },
            ["dnsManagement"] = new Dictionary<string, object> { ["@type"] = "Manual" },
            ["subAddressing"] = new Dictionary<string, object> { ["@type"] = "Enabled" },
            ["isEnabled"] = true
        }, ct);

    public Task<StalwartResult> CreateMailboxAsync(
        string baseUrl, string user, string adminPassword, string domainId,
        string localPart, string password, string displayName, long quotaBytes, CancellationToken ct) =>
        SetAsync(baseUrl, user, adminPassword, "x:Account", new Dictionary<string, object?>
        {
            ["@type"] = "User",
            ["name"] = localPart,
            ["domainId"] = domainId,
            ["description"] = displayName,
            ["credentials"] = new Dictionary<string, object>
            {
                ["0"] = new Dictionary<string, object> { ["@type"] = "Password", ["secret"] = password }
            },
            ["memberGroupIds"] = new Dictionary<string, object>(),
            ["roles"] = new Dictionary<string, object> { ["@type"] = "User" },
            ["permissions"] = new Dictionary<string, object> { ["@type"] = "Inherit" },
            ["quotas"] = quotaBytes > 0
                ? new Dictionary<string, object> { ["Email"] = quotaBytes }
                : new Dictionary<string, object>(),
            ["aliases"] = new Dictionary<string, object>(),
            ["encryptionAtRest"] = new Dictionary<string, object> { ["@type"] = "Disabled" }
        }, ct);

    public Task<StalwartResult> DeleteDomainAsync(
        string baseUrl, string user, string password, string id, CancellationToken ct) =>
        DestroyAsync(baseUrl, user, password, "x:Domain", id, ct);

    public Task<StalwartResult> DeleteMailboxAsync(
        string baseUrl, string user, string password, string id, CancellationToken ct) =>
        DestroyAsync(baseUrl, user, password, "x:Account", id, ct);

    public async Task<StalwartDnsResult> GetDomainDnsAsync(
        string baseUrl, string user, string password, string id, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["using"] = Capabilities,
            ["methodCalls"] = new object[]
            {
                new object[] { "x:Domain/get", new { ids = new[] { id }, properties = new[] { "dnsZoneFile" } }, "dns" }
            }
        };
        using var request = Request(baseUrl, user, password, payload);
        try
        {
            using var response = await clients.CreateClient(nameof(StalwartClient)).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new(false, null, $"Stalwart returned {(int)response.StatusCode}: {Trim(body)}");
            using var json = JsonDocument.Parse(body);
            var first = json.RootElement.GetProperty("methodResponses")[0];
            if (first[0].GetString() == "error") return new(false, null, Trim(first[1].ToString()));
            var list = first[1].GetProperty("list");
            if (list.GetArrayLength() == 0) return new(false, null, "Stalwart returned no DNS zone for this domain.");
            return new(true, list[0].GetProperty("dnsZoneFile").GetString(), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, null, ex.Message);
        }
    }

    public Task<StalwartResult> ResetMailboxPasswordAsync(
        string baseUrl, string user, string adminPassword, string id, string newPassword, CancellationToken ct) =>
        UpdateAsync(baseUrl, user, adminPassword, "x:Account", id,
            new Dictionary<string, object?>
            {
                ["credentials"] = new Dictionary<string, object>
                {
                    ["0"] = new Dictionary<string, object> { ["@type"] = "Password", ["secret"] = newPassword }
                }
            }, ct);

    public async Task<StalwartResult> TestAsync(
        string baseUrl, string user, string password, CancellationToken ct)
    {
        using var request = Request(baseUrl, user, password, new
        {
            using_ = Capabilities,
            methodCalls = new object[] { new object[] { "x:Domain/query", new { limit = 1 }, "health" } }
        });
        return await SendAsync(request, null, ct);
    }

    private async Task<StalwartResult> SetAsync(
        string baseUrl, string user, string password, string type,
        Dictionary<string, object?> value, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["using"] = Capabilities,
            ["methodCalls"] = new object[]
            {
                new object[] { type + "/set", new { create = new Dictionary<string, object> { ["new"] = value } }, "create" }
            }
        };
        using var request = Request(baseUrl, user, password, payload);
        return await SendAsync(request, "new", ct);
    }

    private async Task<StalwartResult> DestroyAsync(
        string baseUrl, string user, string password, string type, string id, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["using"] = Capabilities,
            ["methodCalls"] = new object[]
            {
                new object[] { type + "/set", new { destroy = new[] { id } }, "destroy" }
            }
        };
        using var request = Request(baseUrl, user, password, payload);
        return await SendAsync(request, null, ct);
    }

    private async Task<StalwartResult> UpdateAsync(
        string baseUrl, string user, string password, string type, string id,
        Dictionary<string, object?> changes, CancellationToken ct)
    {
        var payload = new Dictionary<string, object>
        {
            ["using"] = Capabilities,
            ["methodCalls"] = new object[]
            {
                new object[] { type + "/set", new { update = new Dictionary<string, object> { [id] = changes } }, "update" }
            }
        };
        using var request = Request(baseUrl, user, password, payload);
        return await SendAsync(request, null, ct);
    }

    private static HttpRequestMessage Request(string baseUrl, string user, string password, object payload)
    {
        var url = baseUrl.TrimEnd('/') + "/api";
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(user + ":" + password)));
        // The anonymous-object spelling cannot name "using"; serializing through a dictionary above
        // normally avoids this, but TestAsync uses an object for readability.
        var json = JsonSerializer.Serialize(payload).Replace("\"using_\":", "\"using\":");
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private async Task<StalwartResult> SendAsync(HttpRequestMessage request, string? creationKey, CancellationToken ct)
    {
        try
        {
            using var response = await clients.CreateClient(nameof(StalwartClient)).SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                return new(false, null, $"Stalwart returned {(int)response.StatusCode}: {Trim(body)}");

            using var json = JsonDocument.Parse(body);
            var calls = json.RootElement.GetProperty("methodResponses");
            var first = calls[0];
            if (first[0].GetString() == "error")
                return new(false, null, Trim(first[1].ToString()));

            var result = first[1];
            if (result.TryGetProperty("notDestroyed", out var notDestroyed))
                return new(false, null, Trim(notDestroyed.ToString()));
            if (result.TryGetProperty("notUpdated", out var notUpdated))
                return new(false, null, Trim(notUpdated.ToString()));
            if (creationKey is null) return new(true, null, null);
            if (result.TryGetProperty("notCreated", out var rejected))
                return new(false, null, Trim(rejected.ToString()));
            if (result.TryGetProperty("created", out var created)
                && created.TryGetProperty(creationKey, out var item)
                && item.TryGetProperty("id", out var id))
                return new(true, id.GetString(), null);
            return new(false, null, "Stalwart accepted the request but returned no object id.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, null, ex.Message);
        }
    }

    private static string Trim(string value) => value.Length <= 1000 ? value : value[..1000];
}
