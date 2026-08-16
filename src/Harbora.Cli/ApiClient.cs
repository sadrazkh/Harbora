using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Harbora.Cli;

/// <summary>Thin typed wrapper over the Harbora HTTP API used by every command.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(string server, string? token) : this(server, token, new HttpClientHandler()) { }

    /// <summary>
    /// The same client over a transport the caller supplies. A command is thin, and what is worth
    /// checking about one is which endpoint it calls and what it does with the answer — which needs a
    /// stand-in for the server rather than a server.
    /// </summary>
    public ApiClient(string server, string? token, HttpMessageHandler handler)
    {
        // Generous timeout: a push uploads the project and waits for the server to accept it.
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri(server.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10)
        };
        if (!string.IsNullOrWhiteSpace(token))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public ApiClient(Profile profile) : this(profile.Server, profile.Token) { }

    /// <summary>Which server this client talks to — used when writing a project config.</summary>
    public string Server => _http.BaseAddress!.ToString().TrimEnd('/');

    public async Task<JsonElement> GetAsync(string path, CancellationToken ct = default)
    {
        var res = await _http.GetAsync("api/v1/" + path, ct);
        return await ReadAsync(res);
    }

    public async Task<JsonElement> PostAsync(string path, object? body = null, CancellationToken ct = default)
    {
        var res = await _http.PostAsJsonAsync("api/v1/" + path, body ?? new { }, ct);
        return await ReadAsync(res);
    }

    /// <summary>
    /// Streams a file as the raw request body. Used to push a packed project without loading it into
    /// memory — a source tree can be tens of megabytes.
    /// </summary>
    public async Task<JsonElement> PostFileAsync(string path, string filePath)
    {
        await using var file = File.OpenRead(filePath);
        using var content = new StreamContent(file);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");

        // Uploading + building can take minutes; the default 100s timeout would cut it short.
        using var request = new HttpRequestMessage(HttpMethod.Post, "api/v1/" + path) { Content = content };

        // Let the server refuse before the body is on the wire. The archive endpoint answers 404 or
        // 403 without reading the body, and without this the rejection arrives mid-upload: the write
        // fails, and the caller is told about a stream instead of about an app name.
        request.Headers.ExpectContinue = true;

        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead);
        }
        catch (HttpRequestException ex)
        {
            // Expect: is a courtesy, not a guarantee — a proxy may drop it, and the server may still
            // reject mid-body. Name the request that failed; "Error while copying content to a
            // stream." on its own sent a real deploy round twice with nothing to go on.
            throw new HttpRequestException(
                $"Upload to {path} was cut off by the server — most often the app name is not one the "
                + $"server recognises, or the token cannot deploy. ({ex.Message})", ex);
        }

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
