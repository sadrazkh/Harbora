using FluentAssertions;
using Harbora.Infrastructure.Deployments;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which port inside the container actually gets probed and routed to.
///
/// Reported from a real deploy: a stock ASP.NET Core 8 project pushed with `harbora deploy` built
/// cleanly, started cleanly, logged "Application started" — and failed its health check. Nothing was
/// wrong with the app. .NET 8 listens on 8080 and the image declares `EXPOSE 8080`, while the app had
/// been created with port 80, so Harbora spent the whole timeout probing a port nothing was on.
/// </summary>
public class PortSelectionTests
{
    [Fact]
    public void The_image_wins_when_it_contradicts_the_configured_port()
    {
        var choice = PortSelection.Choose(configured: 80, exposed: [8080]);

        choice.Port.Should().Be(8080);
        choice.Changed.Should().BeTrue();
        choice.Reason.Should().Contain("8080").And.Contain("80");
    }

    [Fact]
    public void A_configured_port_the_image_agrees_with_is_left_alone()
    {
        var choice = PortSelection.Choose(configured: 8080, exposed: [8080, 8081]);

        choice.Port.Should().Be(8080);
        choice.Changed.Should().BeFalse("there is nothing to correct, so there is nothing to say");
    }

    [Fact]
    public void An_image_that_declares_nothing_changes_nothing()
    {
        // Plenty of images have no EXPOSE at all. That is "we cannot tell", not "it listens nowhere",
        // and overriding the user's port on the strength of no information would be a guess.
        var choice = PortSelection.Choose(configured: 3000, exposed: []);

        choice.Port.Should().Be(3000);
        choice.Changed.Should().BeFalse();
    }

    [Fact]
    public void A_web_port_is_preferred_when_several_are_exposed()
    {
        // The real case: aspnet images expose 8080 (HTTP) and 8081 (HTTPS), and traffic must go to
        // the HTTP one. 8081 is not a recognised web port, so it is never a candidate.
        PortSelection.Choose(configured: 80, exposed: [8081, 8080]).Port.Should().Be(8080);
    }

    [Fact]
    public void A_recognised_web_port_wins_over_a_lower_unrecognised_one()
    {
        // The reason for preferring at all: an image exposing a debug or metrics port alongside its
        // HTTP port must not have traffic sent to the wrong one just because its number is smaller.
        PortSelection.Choose(configured: 80, exposed: [1234, 8080]).Port.Should().Be(8080);
        PortSelection.Choose(configured: 80, exposed: [9090, 3000]).Port.Should().Be(3000);
    }

    [Fact]
    public void An_image_exposing_only_unusual_ports_still_gets_a_usable_answer()
    {
        // No recognised web port, so there is nothing to prefer — but the configured port is known to
        // be wrong, and any exposed port has a better chance than one nothing listens on.
        var choice = PortSelection.Choose(configured: 80, exposed: [9443, 9090]);

        choice.Port.Should().Be(9090);
        choice.Changed.Should().BeTrue();
    }

    [Fact]
    public void The_reason_names_both_numbers_so_the_log_explains_itself()
    {
        var reason = PortSelection.Choose(configured: 80, exposed: [8080, 8081]).Reason;

        reason.Should().Contain("8080").And.Contain("8081").And.Contain("80");
    }
}
