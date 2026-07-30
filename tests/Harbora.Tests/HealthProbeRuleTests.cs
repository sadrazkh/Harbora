using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// What the HTTP probe should accept.
///
/// Reported from a real deploy: a working ASP.NET Core API, built and started by `harbora deploy`,
/// was refused because the probe went to <c>/</c> — which an API with no root route answers with 404.
/// The app was serving perfectly. A missing root route is not a sick app.
/// </summary>
public class HealthProbeRuleTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(204)]
    [InlineData(302)]
    public void A_success_passes_whatever_the_path(int status)
    {
        HealthProbeRule.Accepts("/healthz", status).Should().BeTrue();
        HealthProbeRule.Accepts("/", status).Should().BeTrue();
    }

    [Fact]
    public void A_404_on_the_root_counts_as_alive()
    {
        // The exact reported case. Nothing asked for the root to exist; the probe only had to
        // establish that something is listening and answering HTTP.
        HealthProbeRule.Accepts("/", 404).Should().BeTrue();
        HealthProbeRule.Accepts(null, 404).Should().BeTrue();
        HealthProbeRule.Accepts("", 404).Should().BeTrue();
    }

    [Fact]
    public void A_404_on_a_configured_path_still_fails()
    {
        // Choosing a health path is an assertion that it works. A 404 there means the app is not
        // what its owner said it would be, and traffic should not move to it.
        HealthProbeRule.Accepts("/healthz", 404).Should().BeFalse();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public void A_server_error_on_the_root_is_not_alive_enough(int status)
    {
        // The line between "this route does not exist" and "this app is broken". A 5xx is the app
        // failing, not a missing route, and switching traffic onto it would be the whole point of the
        // health gate thrown away.
        HealthProbeRule.Accepts("/", status).Should().BeFalse();
    }

    [Fact]
    public void Accepting_a_404_is_explained_but_a_200_is_not()
    {
        // A normal deploy should stay quiet; an unusual acceptance should say why it was allowed.
        HealthProbeRule.ExplainAcceptance("/", 404).Should().Contain("no health path");
        HealthProbeRule.ExplainAcceptance("/", 200).Should().BeNull();
        HealthProbeRule.ExplainAcceptance("/healthz", 404).Should().BeNull("that one is not accepted at all");
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("", true)]
    [InlineData("  ", true)]
    [InlineData(null, true)]
    [InlineData("/healthz", false)]
    [InlineData("/api/health", false)]
    public void The_root_is_recognised_however_it_is_written(string? path, bool expected)
        => HealthProbeRule.IsRoot(path).Should().Be(expected);
}
