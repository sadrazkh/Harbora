using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The whole life of an external database grant, against the node contract.
///
/// These go through the fake agent rather than mocking the service's own calls, because the thing
/// worth proving is that the node is really told to clean up. A test that asserts only on the row
/// would pass while leaving a live login on a customer's database — which is exactly the failure
/// this feature must not have.
/// </summary>
public class DatabaseAccessLifecycleTests
{
    private sealed class Clock(DateTimeOffset now) : Harbora.Application.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 4, 9, 0, 0, TimeSpan.Zero);

    private static (HarboraDbContext Db, DatabaseAccessService Service, FakeNodeAgentClient Node, Clock Clock, ManagedService Database)
        Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbaccess-" + Guid.NewGuid()).Options);

        var database = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = Guid.CreateVersion7(),
            ServerId = Guid.CreateVersion7(),
            Name = "Shop DB",
            ContainerName = "harbora-svc-shop",
            DatabaseName = "shop",
            InternalPort = 5432,
            Type = ManagedServiceType.PostgreSql
        };
        db.Add(database);
        db.SaveChanges();

        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);
        var clock = new Clock(Start);
        var service = new DatabaseAccessService(db, node, clock, NullLogger<DatabaseAccessService>.Instance);

        return (db, service, node, clock, database);
    }

    [Fact]
    public async Task Issuing_access_returns_the_password_exactly_once_and_stores_only_a_hash()
    {
        var (db, service, _, _, database) = Build();

        var result = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, "me@example.com", default);

        result.Ok.Should().BeTrue();
        result.Issued!.Password.Should().NotBeNullOrWhiteSpace();

        var stored = await db.DatabaseAccessGrants.SingleAsync();
        stored.PasswordHash.Should().NotContain(result.Issued.Password);
        DatabaseCredentialManager.Verify(result.Issued.Password, stored.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task The_connection_string_points_at_the_gateway_never_the_node()
    {
        // The entire security design: an outside client learns the gateway's address and nothing
        // about where the database actually runs.
        var (_, service, _, _, database) = Build();

        var result = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, null, default);

        result.Issued!.ConnectionString.Should().Contain("gateway.invalid");
        result.Issued.ConnectionString.Should().NotContain(database.ContainerName);
    }

    [Fact]
    public async Task A_tunnel_that_cannot_be_opened_leaves_no_login_behind()
    {
        // The half-made state that matters. A login created for a tunnel that never opened is an
        // account on a customer's database that nothing in Harbora is tracking.
        var (db, service, node, _, database) = Build();

        var ok = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, null, default);
        ok.Ok.Should().BeTrue();
        node.OpenGrants.Should().Be(1);

        await service.CloseAsync(
            await db.DatabaseAccessGrants.SingleAsync(), DatabaseAccessStatus.Revoked, "done", null, default);

        node.OpenGrants.Should().Be(0, "the login must be removed from the database");
        node.OpenTunnels.Should().Be(0, "the tunnel must be taken down");
    }

    [Fact]
    public async Task An_expired_grant_is_closed_by_the_sweep_and_the_node_is_told()
    {
        var (db, service, node, clock, database) = Build();

        await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromMinutes(15), null, null, null, default);

        node.OpenTunnels.Should().Be(1);

        clock.UtcNow = Start.AddHours(1);

        var expired = await service.ExpiredAsync(default);
        expired.Should().ContainSingle();

        foreach (var grant in expired)
            await service.CloseAsync(grant, DatabaseAccessStatus.Expired, "Access window ended.", null, default);

        node.OpenTunnels.Should().Be(0);
        node.OpenGrants.Should().Be(0);

        var stored = await db.DatabaseAccessGrants.SingleAsync();
        stored.Status.Should().Be(DatabaseAccessStatus.Expired);
        stored.GatewayPort.Should().BeNull("a closed grant must not still advertise an endpoint");
    }

    [Fact]
    public async Task A_persistent_grant_is_not_swept_away()
    {
        var (_, service, _, clock, database) = Build();

        await service.IssueAsync(
            database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        clock.UtcNow = Start.AddYears(1);

        (await service.ExpiredAsync(default)).Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_twice_is_safe()
    {
        // The sweeper and a person pressing revoke can race. Both should end with it closed.
        var (db, service, node, _, database) = Build();

        await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, null, default);

        var grant = await db.DatabaseAccessGrants.SingleAsync();
        await service.CloseAsync(grant, DatabaseAccessStatus.Revoked, "first", null, default);
        await service.CloseAsync(grant, DatabaseAccessStatus.Revoked, "second", null, default);

        node.OpenGrants.Should().Be(0);
    }

    [Fact]
    public async Task Rotating_replaces_the_password_and_invalidates_the_old_one()
    {
        var (db, service, _, _, database) = Build();

        var issued = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await db.DatabaseAccessGrants.SingleAsync();
        var (rotated, error) = await service.RotateAsync(grant, "me@example.com", default);

        error.Should().BeNull();
        rotated.Should().NotBeNullOrWhiteSpace();
        rotated.Should().NotBe(issued.Issued!.Password);

        DatabaseCredentialManager.Verify(rotated!, grant.PasswordHash).Should().BeTrue();
        DatabaseCredentialManager.Verify(issued.Issued.Password, grant.PasswordHash)
            .Should().BeFalse("the old password must stop working");
    }

    [Fact]
    public async Task Every_step_leaves_an_audit_record_and_none_of_them_hold_a_password()
    {
        var (db, service, _, _, database) = Build();

        var issued = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, "me@example.com", default);

        var grant = await db.DatabaseAccessGrants.SingleAsync();
        await service.CloseAsync(grant, DatabaseAccessStatus.Revoked, "finished", "me@example.com", default);

        var trail = await db.DatabaseAccessAudits.ToListAsync();
        trail.Select(a => a.Action).Should().Contain(["created", "activated", "revoked"]);

        foreach (var entry in trail)
            (entry.Detail ?? "").Should().NotContain(issued.Issued!.Password);
    }

    [Fact]
    public async Task A_window_the_policy_refuses_never_reaches_the_node()
    {
        // The refusal has to happen before anything is created, or a rejected request still leaves
        // a login behind.
        var (_, service, node, _, database) = Build();

        var result = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Temporary, TimeSpan.FromDays(365), null, null, null, default);

        result.Ok.Should().BeFalse();
        node.OpenGrants.Should().Be(0);
        node.OpenTunnels.Should().Be(0);
    }

    [Fact]
    public async Task Access_to_a_database_that_no_longer_exists_is_refused()
    {
        var (_, service, _, _, _) = Build();

        var result = await service.IssueAsync(
            Guid.CreateVersion7(), DatabaseAccessKind.Temporary, TimeSpan.FromHours(1), null, null, null, default);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("no longer exists");
    }
}
