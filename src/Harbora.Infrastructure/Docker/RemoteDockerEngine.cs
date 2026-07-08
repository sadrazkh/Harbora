using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// <see cref="IDockerEngine"/> implemented by calling a remote node's Harbora Agent over HTTP
/// (bearer-token auth). Control operations are JSON; build/pull/logs stream the agent's output
/// back line-by-line into the caller's <see cref="IProgress{T}"/>.
/// </summary>
public sealed class RemoteDockerEngine(
    IHttpClientFactory httpFactory, string baseUrl, string token, X509Certificate2? clientCert = null) : IDockerEngine
{
    // IncludeFields lets the ValueTuple mounts in DockerRunRequest/DockerOneOffRequest round-trip.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { IncludeFields = true };

    // When mTLS is on, a dedicated client presents the certificate (factory clients can't carry one).
    private readonly HttpClient? _mtlsClient = clientCert is null ? null : BuildMtlsClient(baseUrl, token, clientCert);

    private static HttpClient BuildMtlsClient(string baseUrl, string token, X509Certificate2 cert)
    {
        var handler = new SocketsHttpHandler();
        handler.SslOptions.ClientCertificates = new X509CertificateCollection { cert };
        var client = new HttpClient(handler) { BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient Client()
    {
        if (_mtlsClient is not null) return _mtlsClient;
        var client = httpFactory.CreateClient();
        client.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        client.Timeout = TimeSpan.FromMinutes(30); // builds can be slow
        return client;
    }

    public async Task<string> BuildImageAsync(DockerBuildRequest request, IProgress<string> log, CancellationToken ct)
    {
        await using var tar = DockerTar.Create(request.ContextPath);
        using var content = new StreamContent(tar);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-tar");

        var client = Client();
        client.DefaultRequestHeaders.Add("X-Build-Args", JsonSerializer.Serialize(request.BuildArgs));
        var url = $"agent/build?tag={Uri.EscapeDataString(request.ImageTag)}&dockerfile={Uri.EscapeDataString(request.Dockerfile)}";
        using var res = await client.PostAsync(url, content, ct);
        res.EnsureSuccessStatusCode();
        await StreamLines(res, log, ct);
        return request.ImageTag;
    }

    public async Task PullImageAsync(string image, IProgress<string> log, CancellationToken ct)
    {
        using var res = await Client().PostAsJsonAsync("agent/images/pull", new { image }, ct);
        res.EnsureSuccessStatusCode();
        await StreamLines(res, log, ct);
    }

    public async Task<string> RunContainerAsync(DockerRunRequest request, CancellationToken ct)
    {
        var res = await Client().PostAsJsonAsync("agent/containers/run", request, Json, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.GetProperty("id").GetString()!;
    }

    public Task StopContainerAsync(string id, CancellationToken ct) => Post($"agent/containers/{id}/stop", ct);
    public Task RemoveContainerAsync(string id, bool force, CancellationToken ct) => Post($"agent/containers/{id}/remove?force={force}", ct);
    public Task RestartContainerAsync(string id, CancellationToken ct) => Post($"agent/containers/{id}/restart", ct);

    public async Task StreamLogsAsync(string id, IProgress<string> sink, CancellationToken ct)
    {
        using var res = await Client().GetAsync($"agent/containers/{id}/logs",
            HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        await StreamLines(res, sink, ct);
    }

    public async Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? labelFilter, CancellationToken ct)
    {
        var url = "agent/containers" + (string.IsNullOrEmpty(labelFilter) ? "" : $"?label={Uri.EscapeDataString(labelFilter)}");
        return await Client().GetFromJsonAsync<List<ContainerInfo>>(url, ct) ?? new();
    }

    public async Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct)
    {
        var res = await Client().GetAsync($"agent/containers/{id}/stats", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<ContainerStats>(ct);
    }

    public Task EnsureNetworkAsync(string name, CancellationToken ct) => PostJson("agent/networks/ensure", new { name }, ct);
    public Task ConnectNetworkAsync(string containerNameOrId, string network, CancellationToken ct) =>
        PostJson("agent/networks/connect", new { container = containerNameOrId, network }, ct);
    public Task EnsureVolumeAsync(string name, CancellationToken ct) => PostJson("agent/volumes/ensure", new { name }, ct);
    public Task RemoveVolumeAsync(string name, CancellationToken ct) => PostJson("agent/volumes/remove", new { name }, ct);

    public async Task<int> RunOneOffAsync(DockerOneOffRequest request, IProgress<string>? log, CancellationToken ct)
    {
        var res = await Client().PostAsJsonAsync("agent/oneoff", request, Json, ct);
        res.EnsureSuccessStatusCode();
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        return doc.GetProperty("exitCode").GetInt32();
    }

    public async Task<HostInfo> GetHostInfoAsync(CancellationToken ct) =>
        await Client().GetFromJsonAsync<HostInfo>("agent/host", ct)
        ?? throw new InvalidOperationException("Agent returned no host info.");

    // --- helpers ---

    private async Task Post(string url, CancellationToken ct)
    {
        var res = await Client().PostAsync(url, null, ct);
        res.EnsureSuccessStatusCode();
    }

    private async Task PostJson(string url, object body, CancellationToken ct)
    {
        var res = await Client().PostAsJsonAsync(url, body, ct);
        res.EnsureSuccessStatusCode();
    }

    private static async Task StreamLines(HttpResponseMessage res, IProgress<string>? sink, CancellationToken ct)
    {
        if (sink is null) return;
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
            sink.Report(line);
    }
}
