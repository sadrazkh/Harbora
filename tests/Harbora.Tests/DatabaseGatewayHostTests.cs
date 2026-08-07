using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Which machine the external-access gateway is opened on.
///
/// <para>
/// The gateway is a proxy container publishing a port on the panel's own machine and forwarding over
/// the private network the database sits on. That only works when the database is on this machine —
/// the constraint <see cref="DatabaseAccessService.CanOpenLocally"/> is built around, and which
/// nothing in the gateway itself ever said. Asked about a database somewhere else it would start a
/// proxy here, forwarding to a container name that resolves to nothing, and hand out a connection
/// string for it.
/// </para>
/// </summary>
public sealed class DatabaseGatewayHostTests : IDisposable
{
    private readonly HarboraDbContext _db = new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("db-gateway-" + Guid.NewGuid()).Options);

    private readonly FakeDockerEngine _panel = new();
    private readonly FakeServerEngineFactory _engines;

    public DatabaseGatewayHostTests() => _engines = new FakeServerEngineFactory(_panel);

    private DockerTcpGateway Gateway() =>
        new(_db, _engines, NullLogger<DockerTcpGateway>.Instance);

    private static ManagedService Database(Guid serverId) => new()
    {
        Id = Guid.NewGuid(),
        WorkspaceId = Guid.NewGuid(),
        ServerId = serverId,
        Name = "orders",
        Type = ManagedServiceType.PostgreSql,
        ContainerName = "harbora-orders",
        InternalPort = 5432,
        Status = ServiceStatus.Running
    };

    private static DatabaseAccessGrant Grant(ManagedService service) => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = service.WorkspaceId,
        ManagedServiceId = service.Id,
        Username = "harbora_tmp",
        PasswordHash = "hash",
        Status = DatabaseAccessStatus.Pending
    };

    [Fact]
    public async Task A_database_on_this_machine_still_gets_an_endpoint()
    {
        var service = Database(Guid.Empty);

        var (endpoint, error) = await Gateway().OpenAsync(Grant(service), service, "harbora-ws-acme", default);

        error.Should().BeNull();
        endpoint.Should().NotBeNull();
        endpoint!.Port.Should().Be(TcpGatewayPlan.FirstPort);
        _panel.Calls.Should().Contain(c => c.Operation == "RunContainerAsync");
    }

    [Fact]
    public async Task A_database_on_another_machine_is_refused_with_a_reason()
    {
        var serverId = Guid.NewGuid();
        _engines.On(serverId, new FakeDockerEngine());
        var service = Database(serverId);

        var (endpoint, error) = await Gateway().OpenAsync(Grant(service), service, "harbora-ws-acme", default);

        endpoint.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().Contain("orders");
        _panel.Calls.Should().BeEmpty(
            "a proxy started here would forward to a name that resolves to nothing on this machine");
    }

    /// <summary>
    /// The factory throws for a server with no agent endpoint and no enrolled node. That must become
    /// a refusal the caller can act on: <c>IssueAsync</c> has already created the login by this point
    /// and undoes it on a refusal, but an exception would leave it behind on the database.
    /// </summary>
    [Fact]
    public async Task A_server_that_cannot_be_resolved_becomes_a_refusal_not_an_exception()
    {
        var serverId = Guid.NewGuid();
        _engines.Unreachable(serverId, "no agent endpoint and no node is enrolled on it");
        var service = Database(serverId);

        var (endpoint, error) = await Gateway().OpenAsync(Grant(service), service, "harbora-ws-acme", default);

        endpoint.Should().BeNull();
        error.Should().Contain("no agent endpoint");
        _panel.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Closing stays on this machine unconditionally: the proxy container was only ever created here,
    /// and a revoke or the expiry sweeper must not fail because the database's server has gone away.
    /// </summary>
    [Fact]
    public async Task Closing_a_grant_removes_the_panels_own_proxy_whatever_the_database_is_on()
    {
        var service = Database(Guid.NewGuid());
        var grant = Grant(service);
        grant.TunnelId = TcpGatewayPlan.ContainerName(grant.Id);

        await Gateway().CloseAsync(grant, default);

        _panel.Calls.Should().Contain(c =>
            c.Operation == "RemoveContainerAsync" && c.Target == grant.TunnelId);
    }

    public void Dispose() => _db.Dispose();
}
