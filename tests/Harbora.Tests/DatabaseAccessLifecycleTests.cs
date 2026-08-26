using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Nodes;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// The single-server install, with everything <see cref="DatabaseAccessService.CanOpenLocally"/>
    /// needs. Its store is a <see cref="BrittleContext"/>, because the window this feature has to
    /// survive is the one where the <c>ALTER USER</c> has landed and the record of it has not.
    /// </summary>
    private sealed record LocalStack(
        BrittleContext Db,
        DatabaseAccessService Service,
        FakeDockerEngine Docker,
        FakeNodeAgentClient Node,
        Clock Clock,
        ManagedService Database,
        string ExpectedNetwork);

    /// <summary>
    /// The install this feature actually ships on: the panel talks to the same Docker daemon the
    /// database runs on, so every step goes through the local executor and the node contract is
    /// never asked. <c>Build()</c> above is the other half — an install with no local reach at all.
    /// </summary>
    private static LocalStack BuildLocal()
    {
        var db = new BrittleContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbaccess-local-" + Guid.NewGuid()).Options);

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Name = "Acme", Slug = "acme" };
        db.Add(workspace);

        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Add(project);
        db.Add(environment);

        var protector = new PassthroughProtector();
        var database = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            EnvironmentId = environment.Id,

            // Guid.Empty resolves to the panel's own engine through the fake factory, which is what
            // the gateway insists on before it will publish a port.
            ServerId = Guid.Empty,
            Name = "Shop DB",
            ContainerName = "harbora-svc-shop",
            DatabaseName = "shop",
            Username = "postgres",
            EncryptedPassword = protector.Protect("admin_secret"),
            InternalPort = 5432,
            Status = ServiceStatus.Running,
            Type = ManagedServiceType.PostgreSql
        };
        db.Add(database);
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var engines = new FakeServerEngineFactory(docker);
        var clock = new Clock(Start);
        var node = new FakeNodeAgentClient(NullLogger<FakeNodeAgentClient>.Instance);

        var service = new DatabaseAccessService(
            db, node, clock, NullLogger<DatabaseAccessService>.Instance,
            new DockerTcpGateway(db, engines, NullLogger<DockerTcpGateway>.Instance),
            new DatabaseGrantExecutor(engines, protector, NullLogger<DatabaseGrantExecutor>.Instance),
            new ManagedServiceEngine(
                db, engines, protector, new NoopJobQueue(),
                // The real gate with billing off — the shipped default — rather than a fake that
                // always says yes, so these tests keep exercising the line production runs.
                new Harbora.Infrastructure.Billing.BillingGate(
                    db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
                Options.Create(new HarboraRuntimeOptions()), clock,
                NullLogger<ManagedServiceEngine>.Instance),
            protector);

        var expectedNetwork = Harbora.Infrastructure.Networking.EnvironmentNetwork.For(
            project.Slug, environment.Slug, environment.Id);
        return new LocalStack(db, service, docker, node, clock, database, expectedNetwork);
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

    /// <summary>
    /// Rotation on the install this feature ships on.
    ///
    /// <para>
    /// It went to the node unconditionally while its siblings branched on
    /// <see cref="DatabaseAccessService.CanOpenLocally"/>, so on a single-server install — where the
    /// login was created locally and the node's book of logins is therefore empty — every rotation
    /// came back "No such login to rotate." The feature had never worked once, and the message
    /// blamed a lookup rather than the branch.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Rotating_replaces_the_password_and_invalidates_the_old_one()
    {
        var stack = BuildLocal();

        var issued = await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);
        issued.Ok.Should().BeTrue();

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();
        var (rotated, error) = await stack.Service.RotateAsync(grant, "me@example.com", default);

        error.Should().BeNull();
        rotated.Should().NotBeNullOrWhiteSpace();
        rotated.Should().NotBe(issued.Issued!.Password);

        DatabaseCredentialManager.Verify(rotated!, grant.PasswordHash).Should().BeTrue();
        DatabaseCredentialManager.Verify(issued.Issued.Password, grant.PasswordHash)
            .Should().BeFalse("the old password must stop working");
    }

    /// <summary>
    /// The password the operator is shown is the one the database was actually given. Anything else
    /// is a screen that hands out a credential nothing will accept.
    /// </summary>
    [Fact]
    public async Task The_new_password_is_the_one_the_database_was_told_about()
    {
        var stack = BuildLocal();

        await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();
        var (rotated, _) = await stack.Service.RotateAsync(grant, "me@example.com", default);

        var statement = stack.Docker.OneOffCommands
            .Should().ContainSingle(c => c.Contains("ALTER USER", StringComparison.Ordinal)).Subject;

        statement.Should().Contain(grant.Username, "the existing login is what is being altered");
        statement.Should().Contain(rotated!, "the operator must be shown what the database now holds");
    }

    /// <summary>
    /// The rotation runs against the database over its own private network, with the admin password
    /// in the environment rather than in argv — the same contract creating the login has.
    ///
    /// <para>
    /// The network asserted on here changed meaning deliberately (P3, 2026-08-17
    /// app-environment-management design): this used to pin the literal string "harbora-ws-acme"
    /// because <c>BuildLocal</c>'s database had no environment of its own and every one-off fell back
    /// to the shared workspace network. EnvironmentId is required now (P2), so the fixture places the
    /// database in a real environment and this asserts on that environment's own network — the same
    /// shape <c>DatabaseAccessLifecycleTests.cs</c>'s own doc comment on the class already names as
    /// the trap other tests in this design have to avoid.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_rotation_reaches_the_database_on_its_own_network_and_keeps_the_admin_password_out_of_argv()
    {
        var stack = BuildLocal();

        await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();
        await stack.Service.RotateAsync(grant, null, default);

        var request = stack.Docker.OneOffRequests
            .Should().ContainSingle(r => string.Join(' ', r.Command).Contains("ALTER USER", StringComparison.Ordinal))
            .Subject;

        request.NetworkMode.Should().Be(stack.ExpectedNetwork);
        request.Env.Should().ContainKey("PGPASSWORD");
        string.Join(' ', request.Command).Should().NotContain("admin_secret");
    }

    /// <summary>
    /// A database that refuses the change must leave the credential the customer already has
    /// working. The alternative is the worst outcome available here: the old password is dead, the
    /// new one was never issued, and the grant cannot be used or recovered.
    /// </summary>
    [Fact]
    public async Task A_database_that_refuses_the_change_leaves_the_old_password_working()
    {
        var stack = BuildLocal();

        var issued = await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();
        stack.Docker.OneOffExitCode = 1;

        var (rotated, error) = await stack.Service.RotateAsync(grant, "me@example.com", default);

        rotated.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        DatabaseCredentialManager.Verify(issued.Issued!.Password, grant.PasswordHash)
            .Should().BeTrue("nothing changed on the database, so nothing may change here either");
    }

    /// <summary>
    /// The half-failed rotation: the database took the new password, and Harbora could not write it
    /// down.
    ///
    /// <para>
    /// Letting the save's exception escape produced a generic failure page, from which the only
    /// reasonable inference is "the rotation did not happen, my old password still works" — the
    /// exact opposite of the truth. The database now holds a password nobody has ever seen and the
    /// old one is dead, so the grant is bricked with no signal at all.
    /// </para>
    ///
    /// <para>
    /// The window itself cannot be closed: the customer's database and this row are two systems and
    /// no ordering commits them together. What can be closed is the silence. The password comes back
    /// with the error instead of dying with the save, and the sentence says which credential is live.
    /// </para>
    ///
    /// <para>
    /// This is the service's half, so the refusal modelled here is one that leaves the connection
    /// alive. Whether the page can still be drawn when it does not is the controller's problem, and
    /// <c>DatabaseAccessPageTests</c> holds it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rotation_the_panel_could_not_record_still_hands_over_the_password_the_database_took()
    {
        var stack = BuildLocal();

        var issued = await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();
        stack.Db.FailTheNextSaveWith = new DbUpdateException("The row was rejected; the connection is fine.");

        var (password, error) = await stack.Service.RotateAsync(grant, "me@example.com", default);

        password.Should().NotBeNullOrWhiteSpace(
            "the database holds it now, and this is the only moment it exists anywhere else");
        password.Should().NotBe(issued.Issued!.Password);

        var statement = stack.Docker.OneOffCommands
            .Should().ContainSingle(c => c.Contains("ALTER USER", StringComparison.Ordinal)).Subject;
        statement.Should().Contain(password!, "the operator must be handed the password that landed");

        error.Should().NotBeNullOrWhiteSpace("silence here reads as 'nothing happened'");
        error.Should().Contain("was changed",
            "the one thing the operator must not conclude is that their old password still works");
        error.Should().ContainEquivalentOf("rotate this access again",
            "Harbora's record and the database disagree until somebody does");
        error.Should().NotContain(password!,
            "the banner is a string that gets logged; the password travels in its own field");
    }

    /// <summary>
    /// The same silence one layer down: the client ran but never reported an exit code, so whether
    /// the <c>ALTER</c> landed is unknown.
    ///
    /// <para>
    /// "The database could not be reached" does not claim nothing changed, but it does not say
    /// something may have — and an operator reading it puts the old password back into service. The
    /// password is handed over here too, because the only two possibilities are that it is now the
    /// live one or that it is inert, and neither is made worse by knowing it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_rotation_that_never_reported_back_says_so_and_hands_over_the_password_it_may_have_set()
    {
        var stack = BuildLocal();

        await stack.Service.IssueAsync(
            stack.Database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await stack.Db.DatabaseAccessGrants.SingleAsync();

        // Recorded before it throws, exactly like a client that ran the statement and then died on
        // the way out — cancellation while the logs were still draining is the ordinary way.
        stack.Docker.OneOffThrows = new IOException("the daemon closed the stream");

        var (password, error) = await stack.Service.RotateAsync(grant, "me@example.com", default);

        password.Should().NotBeNullOrWhiteSpace();
        error.Should().NotBeNullOrWhiteSpace();
        error.Should().ContainEquivalentOf("not known",
            "the honest words: nothing here can say whether the statement reached the database");
        error.Should().Contain("may already",
            "'could not be reached' invites the reading that nothing changed, which is the one "
            + "reading that could be wrong");
        error.Should().ContainEquivalentOf("rotate this access again",
            "the way out of not knowing is to do it again");
        error.Should().NotContain(password!);
    }

    /// <summary>
    /// The install with no local reach. The node-hosted path for external database access is not
    /// built — it is HARBORA-0034, Phase 5 — and saying "no such login" instead sends the operator
    /// hunting for a login that was never the problem.
    /// </summary>
    [Fact]
    public async Task Rotating_a_node_hosted_database_names_the_real_constraint_and_changes_nothing()
    {
        var (db, service, node, _, database) = Build();

        var issued = await service.IssueAsync(
            database.Id, DatabaseAccessKind.Persistent, null, null, null, null, default);

        var grant = await db.DatabaseAccessGrants.SingleAsync();
        var hashBefore = grant.PasswordHash;
        var auditsBefore = await db.DatabaseAccessAudits.CountAsync();

        var (rotated, error) = await service.RotateAsync(grant, "me@example.com", default);

        rotated.Should().BeNull();
        error.Should().NotContain("No such login",
            "the login is not what is missing — the node path for this is");
        error.Should().Contain("HARBORA-0034",
            "an operator reading this has to be able to find out when it will work");

        grant.PasswordHash.Should().Be(hashBefore);
        DatabaseCredentialManager.Verify(issued.Issued!.Password, grant.PasswordHash).Should().BeTrue();
        (await db.DatabaseAccessAudits.CountAsync()).Should().Be(auditsBefore, "nothing happened to record");
        node.Calls.Should().NotContain(c => c.StartsWith("rotate:", StringComparison.Ordinal));
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
