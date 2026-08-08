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
        // A guard on the substitution itself. If DependencyInjection ever registers its workers in a
        // shape this factory does not recognise, the count collapses and every `dotnet test` run
        // starts talking to Docker and GitHub — slowly, and then flakily.
        fixture.Panel.RemovedBackgroundWorkers.Should().BeGreaterThan(15);
    }
}
