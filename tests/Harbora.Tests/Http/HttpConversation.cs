using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Harbora.Tests;

/// <summary>
/// The small vocabulary the HTTP tests share: reading a page, spending an antiforgery token, signing
/// in the way a browser does, and looking inside a JSON body.
/// </summary>
public static class HttpConversation
{
    /// <summary>The hidden field MVC writes into every form guarded by antiforgery.</summary>
    public const string AntiforgeryField = "__RequestVerificationToken";

    private static readonly Regex TokenField = new(
        """<input[^>]*name="__RequestVerificationToken"[^>]*value="(?<token>[^"]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Fetches a page and takes its antiforgery token. The matching cookie lands in the client's own
    /// cookie jar, so the pair only works together — which is the property under test.
    /// </summary>
    public static async Task<string> AntiforgeryTokenFrom(this HttpClient client, string path)
    {
        var page = await client.GetAsync(path);
        page.StatusCode.Should().Be(HttpStatusCode.OK, "the form has to render before it can be posted");

        var match = TokenField.Match(await page.Content.ReadAsStringAsync());
        match.Success.Should().BeTrue($"{path} should carry an antiforgery field");
        return match.Groups["token"].Value;
    }

    /// <summary>A form POST carrying a token this client was actually given.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string path, string antiforgeryToken, params (string Key, string Value)[] fields)
    {
        var body = fields.ToDictionary(f => f.Key, f => f.Value);
        body[AntiforgeryField] = antiforgeryToken;
        return await client.PostAsync(path, new FormUrlEncodedContent(body));
    }

    /// <summary>A form POST with no token at all — what a cross-site submission looks like.</summary>
    public static async Task<HttpResponseMessage> PostFormWithoutTokenAsync(
        this HttpClient client, string path, params (string Key, string Value)[] fields) =>
        await client.PostAsync(path, new FormUrlEncodedContent(fields.ToDictionary(f => f.Key, f => f.Value)));

    /// <summary>
    /// Signs in over the real login form: render, take the token, post the credentials, keep the auth
    /// cookie. Nothing here is faked, so the returned client is one the cookie scheme has authenticated.
    /// </summary>
    public static async Task<HttpClient> SignedInAs(
        this HarboraWebFactory panel, string remoteIp, string email,
        string password = HarboraWebFactory.TestPassword)
    {
        var client = panel.ClientFrom(remoteIp);
        var token = await client.AntiforgeryTokenFrom("/account/login");

        var response = await client.PostFormAsync("/account/login", token,
            ("Email", email), ("Password", password));

        response.StatusCode.Should().Be(HttpStatusCode.Found,
            "a successful sign-in redirects; a rejected one re-renders the form with 200");
        return client;
    }

    /// <summary>
    /// Where a redirect points, without its query string. Framework challenges answer with an
    /// absolute URL and an encoded returnUrl, while <c>RedirectToAction</c> answers with a relative
    /// path — the path is the part a test means either way.
    /// </summary>
    public static string RedirectPath(this HttpResponseMessage response)
    {
        var location = response.Headers.Location;
        location.Should().NotBeNull("the response should be a redirect");
        return location!.IsAbsoluteUri ? location.AbsolutePath : location.OriginalString.Split('?')[0];
    }

    /// <summary>JSON body of a response, for asserting on the documented error shape.</summary>
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    /// <summary>The <c>error</c> string docs/cli-deploy.md promises on every refusal.</summary>
    public static async Task<string?> DocumentedErrorAsync(this HttpResponseMessage response)
    {
        var body = await response.JsonAsync();
        body.ValueKind.Should().Be(JsonValueKind.Object);
        body.TryGetProperty("error", out var error).Should().BeTrue(
            "docs/cli-deploy.md publishes {\"error\": \"…\"} as the API v1 refusal body");
        return error.GetString();
    }

    /// <summary>A JSON request body, the way the CLI sends one.</summary>
    public static StringContent Json(object body) =>
        new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
}
