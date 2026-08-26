using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Whether this installation may offer external access at all.
///
/// The failure being prevented is specific and quiet: a page issues a username, a password and a
/// connection string pointing at a gateway that was never opened. Harbora's records show a healthy
/// active grant; the customer gets a name-resolution error and reports a broken database. Everything
/// on our side looks correct, which is what makes it expensive.
/// </summary>
public class ExternalAccessAvailabilityTests
{
    /// <summary>A node client that claims to be real without being one. Nothing else about it matters here.</summary>
    private sealed class RealEnoughNode : INodeAgentClient
    {
        public Task<NodeCapabilities> GetCapabilitiesAsync(Guid s, CancellationToken ct) =>
            Task.FromResult(new NodeCapabilities(true, true, "1.0", "amd64"));

        public Task<NodeResult> DeployWorkloadAsync(Guid s, string w, string i, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));

        public Task<NodeResult> UpdateWorkloadAsync(Guid s, string w, string i, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));

        public Task<string?> GetWorkloadStatusAsync(Guid s, string w, CancellationToken ct) =>
            Task.FromResult<string?>("running");

        public Task<NodeResult> CreateDatabaseGrantAsync(Guid s, string c, string u, string p, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));

        public Task<NodeResult> RevokeDatabaseGrantAsync(Guid s, string c, string u, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));

        public Task<NodeResult> RotateDatabaseCredentialAsync(Guid s, string c, string u, string p, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));

        public Task<TcpTunnel?> CreateTcpTunnelAsync(Guid s, string c, int p, CancellationToken ct) =>
            Task.FromResult<TcpTunnel?>(new TcpTunnel("t", "gw.example.com", 15432));

        public Task<NodeResult> RemoveTcpTunnelAsync(Guid s, string t, CancellationToken ct) =>
            Task.FromResult(new NodeResult(true, null));
    }

    private static ManagedService Database(ServiceStatus status = ServiceStatus.Running) => new()
    {
        Id = Guid.CreateVersion7(), Name = "Shop DB", ContainerName = "harbora-svc-shop",
        Type = ManagedServiceType.PostgreSql, InternalPort = 5432, Status = status
    };

    private static FakeNodeAgentClient Fake() => new(NullLogger<FakeNodeAgentClient>.Instance);

    [Fact]
    public void The_fake_agent_says_plainly_that_it_is_a_simulation()
    {
        // The whole guard rests on this one answer being honest.
        Fake().IsSimulated.Should().BeTrue();
    }

    [Fact]
    public void A_client_that_does_not_answer_is_treated_as_real()
    {
        // The default must not disable the feature for a real agent that never thought to say so.
        // Read through the interface because that is where the default lives — and it is also how
        // every caller sees it.
        ((INodeAgentClient)new RealEnoughNode()).IsSimulated.Should().BeFalse();
    }

    [Fact]
    public void A_simulated_node_cannot_offer_external_access()
    {
        ExternalAccessAvailability.Refuse(Fake(), Database())
            .Should().NotBeNull("nothing it opens would be reachable");
    }

    [Fact]
    public void A_real_node_and_a_running_database_may_offer_access()
    {
        ExternalAccessAvailability.Refuse(new RealEnoughNode(), Database()).Should().BeNull();
    }

    [Fact]
    public void A_stopped_database_cannot_be_opened_to_the_outside()
    {
        // The grant's clock would start running against something nothing can connect to, and the
        // window would be spent before the database came back.
        ExternalAccessAvailability.Refuse(new RealEnoughNode(), Database(ServiceStatus.Stopped))
            .Should().NotBeNull();
    }

    [Fact]
    public void A_database_that_is_still_provisioning_cannot_be_opened()
    {
        ExternalAccessAvailability.Refuse(new RealEnoughNode(), Database(ServiceStatus.Provisioning))
            .Should().NotBeNull();
    }

    [Fact]
    public void A_database_that_no_longer_exists_cannot_be_opened()
    {
        ExternalAccessAvailability.Refuse(new RealEnoughNode(), null).Should().NotBeNull();
    }

    [Fact]
    public void A_simulated_agent_no_longer_blocks_an_install_that_can_open_the_port_itself()
    {
        // The node agent was blocking a feature that never needed it here: on a single-server
        // install the control plane already talks to the same Docker daemon the databases run on.
        ExternalAccessAvailability.Refuse(Fake(), Database(), canOpenLocally: true).Should().BeNull();
    }

    [Fact]
    public void A_stopped_database_is_still_refused_even_when_the_port_could_be_opened()
    {
        ExternalAccessAvailability.Refuse(Fake(), Database(ServiceStatus.Stopped), canOpenLocally: true)
            .Should().NotBeNull();
    }

    /// <summary>
    /// HARBORA-0059: an install that can open a port on its own machine still cannot open one for a
    /// database that lives on a different machine. This used to be missing entirely, so the button
    /// was offered and the only refusal ever arrived from <c>DockerTcpGateway.OpenAsync</c> — after a
    /// login had already been created on that database and had to be undone again.
    /// </summary>
    [Fact]
    public void A_database_on_another_server_is_refused_even_though_this_install_can_open_a_port_here()
    {
        var refusal = ExternalAccessAvailability.Refuse(
            Fake(), Database(), canOpenLocally: true, serviceRunsElsewhere: true);

        refusal.Should().NotBeNull("the gateway can only ever publish a port on this panel's own machine");
        refusal!.Reason.Should().Contain("Shop DB");
        refusal.ReasonFa.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void A_database_on_this_machine_is_not_refused_for_placement()
    {
        ExternalAccessAvailability.Refuse(
                Fake(), Database(), canOpenLocally: true, serviceRunsElsewhere: false)
            .Should().BeNull();
    }

    [Fact]
    public void Placement_is_moot_when_this_install_cannot_open_a_port_at_all()
    {
        // The node-agent branch above already refuses everything on this install; asserting
        // "elsewhere" too would just be a second reason for the same outcome, and the message a
        // person reads should still be the one about the missing agent, not about placement.
        var refusal = ExternalAccessAvailability.Refuse(
            Fake(), Database(), canOpenLocally: false, serviceRunsElsewhere: true);

        refusal!.Reason.Should().Contain("node agent");
    }

    [Theory]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    public void An_engine_with_no_way_to_make_a_login_is_refused_before_anything_else(ManagedServiceType type)
    {
        // Checked first and regardless of everything else. Opening a port onto an engine Harbora
        // cannot make a login for would publish it with only the platform's own admin credentials
        // behind it.
        var database = Database();
        database.Type = type;

        var refusal = ExternalAccessAvailability.Refuse(Fake(), database, canOpenLocally: true);

        refusal.Should().NotBeNull();
        refusal!.Reason.Should().Contain(type.ToString());
    }

    [Fact]
    public void Every_refusal_says_why_in_both_languages()
    {
        // A reason nobody can read is the same as no reason: the person retries, and retries.
        var refusals = new[]
        {
            ExternalAccessAvailability.Refuse(Fake(), Database()),
            ExternalAccessAvailability.Refuse(new RealEnoughNode(), Database(ServiceStatus.Stopped)),
            ExternalAccessAvailability.Refuse(new RealEnoughNode(), null)
        };

        refusals.Should().OnlyContain(r => r != null);
        foreach (var refusal in refusals)
        {
            refusal!.Reason.Should().NotBeNullOrWhiteSpace();
            refusal.ReasonFa.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Fact]
    public void The_simulated_refusal_comes_before_anything_about_the_database()
    {
        // Order matters for the message a person reads. Told "start the database first" they would
        // start it and try again, and the feature still would not work.
        var refusal = ExternalAccessAvailability.Refuse(Fake(), Database(ServiceStatus.Stopped));

        refusal!.Reason.Should().Contain("node agent");
    }
}
