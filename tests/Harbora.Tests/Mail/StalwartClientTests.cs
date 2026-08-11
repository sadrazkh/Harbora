using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Harbora.Infrastructure.Mail;
using Xunit;

namespace Harbora.Tests.Mail;

public sealed class StalwartClientTests
{
    [Fact]
    public async Task Creating_a_domain_uses_the_management_jmap_contract_and_returns_the_id()
    {
        var handler = new CaptureHandler("""
            {"methodResponses":[["x:Domain/set",{"created":{"new":{"id":"domain-1"}}},"create"]]}
            """);
        var client = new StalwartClient(new Factory(handler));

        var result = await client.CreateDomainAsync(
            "https://mail.example.com", "admin", "secret", "example.org", default);

        result.Should().Be(new StalwartResult(true, "domain-1", null));
        handler.Path.Should().Be("/api");
        handler.Authorization.Should().Be("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("admin:secret")));
        using var json = JsonDocument.Parse(handler.Body!);
        json.RootElement.GetProperty("using")[1].GetString().Should().Be("urn:stalwart:jmap");
        var call = json.RootElement.GetProperty("methodCalls")[0];
        call[0].GetString().Should().Be("x:Domain/set");
        call[1].GetProperty("create").GetProperty("new").GetProperty("name").GetString()
            .Should().Be("example.org");
    }

    [Fact]
    public async Task A_provider_level_rejection_is_not_reported_as_success()
    {
        var handler = new CaptureHandler("""
            {"methodResponses":[["x:Account/set",{"notCreated":{"new":{"type":"invalidProperties"}}},"create"]]}
            """);
        var client = new StalwartClient(new Factory(handler));

        var result = await client.CreateMailboxAsync(
            "https://mail.example.com", "admin", "secret", "domain-1",
            "hello", "password", "Hello", 1024, default);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Contain("invalidProperties");
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class CaptureHandler(string response) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Authorization { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Path = request.RequestUri!.AbsolutePath;
            Authorization = request.Headers.Authorization?.ToString();
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
