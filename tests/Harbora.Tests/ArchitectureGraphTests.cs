using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Networking;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Networking;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Turning a project into a diagram.
///
/// The thing worth guarding is what the boxes claim. A diagram is read as a statement of fact about
/// the system, so a box that shows a metric nobody measured, or green for a service in an unknown
/// state, is worse than no diagram at all.
/// </summary>
public class ArchitectureGraphTests
{
    private static App Service(
        string name, ServiceKind kind = ServiceKind.Web, AppStatus status = AppStatus.Running) =>
        new() { Id = Guid.CreateVersion7(), Name = name, Slug = name.ToLowerInvariant(), Kind = kind, Status = status };

    private static ManagedService Database(
        string name, string container, ServiceStatus status = ServiceStatus.Running) =>
        new()
        {
            Id = Guid.CreateVersion7(), Name = name, ContainerName = container,
            Type = ManagedServiceType.PostgreSql, Version = "16", Status = status
        };

    [Fact]
    public void A_service_and_the_database_it_uses_are_connected()
    {
        var web = Service("web");
        var db = Database("Shop DB", "harbora-shop-db");
        var connections = new Dictionary<Guid, IReadOnlyList<string>> { [web.Id] = ["harbora-shop-db"] };

        var picture = ArchitectureGraph.Build([web], [db], connections);

        picture.Edges.Should().ContainSingle();
        picture.Nodes.Should().HaveCount(2);
        picture.Nodes.Single(n => n.Label == "web").Row.Should()
            .BeLessThan(picture.Nodes.Single(n => n.Label == "Shop DB").Row, "traffic reads downwards");
    }

    [Fact]
    public void A_domain_sits_above_the_service_it_points_at()
    {
        var web = Service("web");
        web.Domains.Add(new DomainName { Id = Guid.CreateVersion7(), Host = "shop.example.com", SslEnabled = true });

        var picture = ArchitectureGraph.Build([web], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Single(n => n.Label == "shop.example.com").Row.Should()
            .BeLessThan(picture.Nodes.Single(n => n.Label == "web").Row);
    }

    [Fact]
    public void A_worker_gets_no_domain_box_even_if_one_is_attached()
    {
        // A worker has no public traffic, so a domain on it is stale data rather than a route. The
        // diagram must not draw an entrance that does not exist.
        var worker = Service("worker", ServiceKind.Worker);
        worker.Domains.Add(new DomainName { Id = Guid.CreateVersion7(), Host = "stale.example.com" });

        var picture = ArchitectureGraph.Build([worker], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Should().ContainSingle().Which.Label.Should().Be("worker");
    }

    [Fact]
    public void An_unknown_state_is_not_drawn_as_healthy()
    {
        // The failure that matters: a wall of green boxes is a wall nobody checks.
        var created = Service("fresh", status: AppStatus.Created);
        var stopped = Service("halted", status: AppStatus.Stopped);

        var picture = ArchitectureGraph.Build([created, stopped], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Should().OnlyContain(n => n.Status != "ok");
    }

    [Fact]
    public void A_crashed_service_is_drawn_as_broken()
    {
        var picture = ArchitectureGraph.Build(
            [Service("web", status: AppStatus.Crashed)], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Single().Status.Should().Be("error");
    }

    [Fact]
    public void A_box_never_claims_a_measurement()
    {
        // Per-service metrics are not collected for managed services, and the mockup this is drawn
        // from puts a sparkline in every node. The second line carries identity, never numbers.
        var db = Database("Shop DB", "c");

        var picture = ArchitectureGraph.Build([], [db], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Single().Detail.Should().Be("PostgreSql 16");
    }

    [Fact]
    public void A_service_with_no_internal_address_says_nothing_rather_than_guessing()
    {
        // A release task runs once and exits without joining the network, so it has no name anything
        // can reach it by. Printing its slug there would invite someone to point a connection string
        // at an address that never answers. A cron job, by contrast, does join — so it does get one.
        var picture = ArchitectureGraph.Build(
            [Service("migrate", ServiceKind.ReleaseTask)], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Single().Detail.Should().BeNull();
    }

    [Fact]
    public void A_cron_job_does_get_an_internal_address()
    {
        var picture = ArchitectureGraph.Build(
            [Service("nightly", ServiceKind.Cron)], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Single().Detail.Should().Be("nightly");
    }

    [Fact]
    public void A_connection_naming_a_database_that_is_gone_is_dropped()
    {
        // Connections come from environment variables, which outlive the database they point at.
        var web = Service("web");
        var connections = new Dictionary<Guid, IReadOnlyList<string>> { [web.Id] = ["harbora-deleted-db"] };

        var picture = ArchitectureGraph.Build([web], [], connections);

        picture.Edges.Should().BeEmpty();
        picture.Nodes.Should().ContainSingle();
    }

    [Fact]
    public void An_empty_project_produces_an_empty_diagram()
    {
        var picture = ArchitectureGraph.Build([], [], new Dictionary<Guid, IReadOnlyList<string>>());

        picture.Nodes.Should().BeEmpty();
        picture.Edges.Should().BeEmpty();
    }
}
