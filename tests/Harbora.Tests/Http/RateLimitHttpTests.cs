using System.Net;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The two per-IP limiters Program.cs registers: <c>auth</c> at 10 a minute and <c>webhook</c> at 60.
///
/// <para>
/// A limiter is a middleware and a partition key, and neither exists inside a controller. What these
/// prove that nothing else can: that the routes carry the policies they are meant to, that the
/// partition is the caller's address rather than one platform-wide bucket, and that a refusal is a
/// 429 rather than a 500 or a quiet success.
/// </para>
///
/// <para>
/// The windows are a minute wide and the limiter is a singleton, so every test here uses an address
/// of its own. Sharing one would make the second test to run fail for the first test's reasons.
/// </para>
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class RateLimitHttpTests(HarboraHttpFixture fixture)
{
    private HarboraWebFactory Panel => fixture.Panel;

    private const int AuthPermits = 10;
    private const int WebhookPermits = 60;

    private static HttpContent WrongCredentials() =>
        HttpConversation.Json(new { email = "nobody@example.com", password = "nope" });

    [Fact]
    public async Task The_eleventh_login_attempt_from_one_address_in_a_minute_is_a_429()
    {
        var client = Panel.ClientFrom("203.0.113.60");

        for (var attempt = 1; attempt <= AuthPermits; attempt++)
        {
            var allowed = await client.PostAsync("/api/v1/auth/token", WrongCredentials());
            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"attempt {attempt} is inside the window's {AuthPermits} permits");
        }

        var refused = await client.PostAsync("/api/v1/auth/token", WrongCredentials());

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task Exhausting_one_address_does_not_lock_out_another()
    {
        // The partition key is Connection.RemoteIpAddress. When the panel sits behind Traefik and the
        // forwarded headers are not unwound, every caller collapses into a single bucket and one
        // brute-force attempt locks out the platform — which is the failure this asserts against.
        var attacker = Panel.ClientFrom("203.0.113.61");
        var bystander = Panel.ClientFrom("203.0.113.62");

        for (var attempt = 0; attempt <= AuthPermits; attempt++)
            await attacker.PostAsync("/api/v1/auth/token", WrongCredentials());

        var attackerAgain = await attacker.PostAsync("/api/v1/auth/token", WrongCredentials());
        var otherPerson = await bystander.PostAsync("/api/v1/auth/token", WrongCredentials());

        attackerAgain.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        otherPerson.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "somebody else's address has its own window");
    }

    [Fact]
    public async Task A_rate_limited_address_can_still_reach_routes_the_policy_does_not_cover()
    {
        var client = Panel.ClientFrom("203.0.113.63");
        for (var attempt = 0; attempt <= AuthPermits; attempt++)
            await client.PostAsync("/api/v1/auth/token", WrongCredentials());

        var stillLimited = await client.PostAsync("/api/v1/auth/token", WrongCredentials());
        var unrelated = await client.GetAsync("/api/v1/version");
        var probe = await client.GetAsync("/healthz");

        stillLimited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        unrelated.StatusCode.Should().Be(HttpStatusCode.OK, "only the login route carries the auth policy");
        probe.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_login_form_shares_the_auth_window_with_the_CLI_token_endpoint()
    {
        // docs/cli-deploy.md says POST /auth/token is "rate-limited per IP like the panel's own
        // login". They are the same named policy, so spending the window on one spends it on the
        // other — which is only visible from outside.
        var client = Panel.ClientFrom("203.0.113.64");
        var token = await client.AntiforgeryTokenFrom("/account/login");

        for (var attempt = 0; attempt <= AuthPermits; attempt++)
            await client.PostAsync("/api/v1/auth/token", WrongCredentials());

        var form = await client.PostFormAsync("/account/login", token,
            ("Email", "nobody@example.com"), ("Password", "nope"));

        form.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_sixty_first_webhook_from_one_address_in_a_minute_is_a_429()
    {
        var client = Panel.ClientFrom("203.0.113.65");
        var unknownRepository = Guid.CreateVersion7();

        for (var attempt = 1; attempt <= WebhookPermits; attempt++)
        {
            var allowed = await client.PostAsync($"/webhooks/git/{unknownRepository}",
                HttpConversation.Json(new { @ref = "refs/heads/main" }));
            allowed.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"delivery {attempt} is inside the window's {WebhookPermits} permits");
        }

        var refused = await client.PostAsync($"/webhooks/git/{unknownRepository}",
            HttpConversation.Json(new { @ref = "refs/heads/main" }));

        refused.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task The_webhook_window_is_wider_than_the_login_one()
    {
        // Not a restatement of the numbers: it is the assertion that the two routes are on different
        // policies at all. One shared limiter would let a busy repository lock out every login.
        var client = Panel.ClientFrom("203.0.113.66");
        var unknownRepository = Guid.CreateVersion7();

        for (var attempt = 0; attempt < AuthPermits + 5; attempt++)
            await client.PostAsync($"/webhooks/git/{unknownRepository}",
                HttpConversation.Json(new { @ref = "refs/heads/main" }));

        var webhook = await client.PostAsync($"/webhooks/git/{unknownRepository}",
            HttpConversation.Json(new { @ref = "refs/heads/main" }));

        webhook.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "fifteen deliveries are well inside the webhook window even though they exceed the auth one");
    }
}
