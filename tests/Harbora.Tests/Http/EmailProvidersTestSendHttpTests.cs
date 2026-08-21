using System.Net;
using FluentAssertions;
using Harbora.Domain.Common;
using Harbora.Domain.Email;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The one honesty requirement this whole sub-project turns on (F6, 2026-08-21
/// functions-and-services plan): the test-send button must report the provider's real answer, never
/// "sent" for a refusal. <see cref="EmailProviderMailerTests"/> proves this against a fake at the
/// transport seam with a controlled message; this class proves it end to end through the real HTTP
/// route with the real <c>SystemNetSmtpTransport</c> — no Docker and no real SMTP server exist on
/// this dev machine, so the refusal proven here is a genuine TCP connection refusal against a closed
/// local port (grabbed and released immediately before the request, so nothing is listening there),
/// not a live provider's own rejection. That gap is stated plainly rather than papered over: nobody
/// on this machine can prove what a real SMTP server says back.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class EmailProvidersTestSendHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    /// <summary>A port nothing is listening on, right now, on this machine — bound and immediately
    /// released so the OS will not hand it to anything else before the request below reaches it.</summary>
    private static int ClosedLocalPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private Guid SeedUnreachableProvider(string name)
    {
        var provider = new EmailProvider
        {
            WorkspaceId = fixture.WorkspaceId, Name = name, Host = "127.0.0.1", Port = ClosedLocalPort(),
            Username = "apikey", EncryptedPassword = "cipher:unreachable",
            FromAddress = "noreply@acme.example", UseSsl = false
        };
        Panel.Seed(db => db.EmailProviders.Add(provider));
        return provider.Id;
    }

    [Fact]
    public async Task A_refused_connection_is_reported_as_a_failure_never_as_sent()
    {
        var providerId = SeedUnreachableProvider("unreachable");
        Panel.GivenUser(fixture.WorkspaceId, "ep-test-refused@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.160", "ep-test-refused@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        var response = await client.PostFormAsync($"/email-providers/{providerId}/test", token);

        response.StatusCode.Should().Be(HttpStatusCode.Found);
        var html = await (await client.GetAsync(response.RedirectPath())).Content.ReadAsStringAsync();

        // The panel renders Persian by default in tests, so this asserts on the data- attribute the
        // persisted LastTestSucceeded flag renders as, never on the banner's own sentence — the same
        // rule every other test in this suite follows.
        html.Should().NotContain("data-email-provider-test-state=\"ok\"",
            "a connection that was refused must never be reported as a success");
        html.Should().Contain("data-email-provider-test-state=\"failed\"",
            "the honest-failure state AdminSettingsController.TestSmtp's own idiom already gives the platform's own mail");
    }

    [Fact]
    public async Task The_refusal_is_persisted_on_the_provider_so_the_page_can_show_it_without_re_testing()
    {
        var providerId = SeedUnreachableProvider("unreachable-persisted");
        Panel.GivenUser(fixture.WorkspaceId, "ep-test-persist@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.161", "ep-test-persist@example.com");

        var token = await client.AntiforgeryTokenFrom("/email-providers");
        await client.PostFormAsync($"/email-providers/{providerId}/test", token);

        var stored = Panel.Read(db => db.EmailProviders.Single(p => p.Id == providerId));
        stored.LastTestSucceeded.Should().BeFalse();
        stored.LastTestMessage.Should().NotBeNullOrWhiteSpace(
            "the provider's own words belong on the row, not just in a TempData banner that vanishes on the next request");
        stored.LastTestedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_never_tested_provider_shows_as_not_tested_rather_than_a_fabricated_pass()
    {
        var providerId = SeedUnreachableProvider("untested");
        Panel.GivenUser(fixture.WorkspaceId, "ep-untested@example.com", SystemRole.Owner);
        var client = await Panel.SignedInAs("203.0.113.162", "ep-untested@example.com");

        var html = await (await client.GetAsync("/email-providers")).Content.ReadAsStringAsync();

        html.Should().Contain("data-email-provider-test-state=\"untested\"");
        html.Should().NotContain("data-email-provider-test-state=\"ok\"");
    }
}
