using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Harbora.Cli;

/// <summary>Thin typed wrapper over the Harbora HTTP API used by every command.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(HarboraConfig config)
    {
        _http = new HttpClient { BaseAddress = new Uri(config.Server!.TrimEnd('/') + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", config.Token);
    }

    public async Task<JsonElement> GetAsync(string path)
    {
        var res = await _http.GetAsync("api/v1/" + path);
        return await ReadAsync(res);
    }

    public async Task<JsonElement> PostAsync(string path, object? body = null)
    {
        var res = await _http.PostAsJsonAsync("api/v1/" + path, body ?? new { });
        return await ReadAsync(res);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)res.StatusCode} {res.ReasonPhrase}: {text}");
        return string.IsNullOrWhiteSpace(text)
            ? default
            : JsonSerializer.Deserialize<JsonElement>(text);
    }
}
