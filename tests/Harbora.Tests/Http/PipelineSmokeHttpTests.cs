using System.Net;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The harness proving itself. If these fail, nothing else in the HTTP lane means anything.
/// </summary>
[Collection(HarboraHttpCollection.Name)]
public class PipelineSmokeHttpTests(HarboraHttpFixture fixture)
{
    [Fact]
    public async Task The_liveness_probe_answers_through_the_real_pipeline()
    {
        var client = fixture.Panel.ClientFrom("203.0.113.10");

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement.GetProperty("status").GetString().Should().Be("ok");
    }

    [Fact]
    public async Task Razor_renders_a_page_rather_than_the_action_returning_a_view_name()
    {
        // The distinction this whole lane exists for: a ViewResult is not a page. The view executes
        // after the action has returned, against whatever the request still has — which is where a
        // controller test stops looking and where the panel's users start.
        var client = fixture.Panel.ClientFrom("203.0.113.11");

        var response = await client.GetAsync("/account/login");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<form", "the login page is a rendered document, not a view name");
        html.Should().Contain("__RequestVerificationToken");
    }

    [Fact]
    public void The_harness_removed_Harboras_background_workers_and_kept_the_web_host()
    {
        // A guard on the substitution itself, stated three ways because the count alone is weak: a
        // loose lower bound would let a third of the registrations stop being recognised in silence,
        // and every `dotnet test` run would quietly start talking to Docker, a registry and GitHub.
        //
        // The count is the floor as it stands today (24). It is a floor rather than an equality on
        // purpose — a task that adds a hosted service should not fail here — but the second
        // assertion is the real one: whatever the shape of the registration, nothing of Harbora's
        // may be left running.
        fixture.Panel.RemovedBackgroundWorkers.Should().BeGreaterThanOrEqualTo(24,
            "every worker DependencyInjection and the two modules register has to be recognised");

        fixture.Panel.RemainingHostedServices.Should().NotContain(
            name => name.StartsWith("Harbora", StringComparison.Ordinal),
            "a worker the removal did not recognise is one that runs during every test run");

        fixture.Panel.RemainingHostedServices.Should().Contain(
            name => name.EndsWith("GenericWebHostService", StringComparison.Ordinal),
            "and taking the host's own service out would leave a server that never listens");
    }
}
