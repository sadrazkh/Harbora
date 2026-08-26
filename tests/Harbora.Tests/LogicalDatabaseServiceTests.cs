using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Creating and removing a logical database (D1, 2026-08-25 shared-databases plan), against the fake
/// Docker engine rather than mocking <see cref="LogicalDatabaseService"/>'s own calls — the thing
/// worth proving is what actually reaches the engine and in what order, because a row that exists in
/// Harbora and not on the engine (or the reverse) is this codebase's defining defect class.
/// </summary>
public class LogicalDatabaseServiceTests
{
    private sealed class Clock(DateTimeOffset now) : Harbora.Application.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Start = new(2026, 8, 26, 9, 0, 0, TimeSpan.Zero);

    private sealed record Stack(
        BrittleContext Db, LogicalDatabaseService Service, FakeDockerEngine Docker, ManagedService Instance);

    /// <summary>The single-server install this feature ships on — the panel talks to the same Docker
    /// daemon the database runs on, mirroring <c>DatabaseAccessLifecycleTests.BuildLocal</c>.</summary>
    private static Stack BuildLocal(ManagedServiceType type = ManagedServiceType.PostgreSql)
    {
        var db = new BrittleContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("logicaldb-" + Guid.NewGuid()).Options);

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Name = "Acme", Slug = "acme" };
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Add(workspace);
        db.Add(project);
        db.Add(environment);

        var protector = new PassthroughProtector();
        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            EnvironmentId = environment.Id,
            ServerId = Guid.Empty,
            Name = "Shop DB",
            ContainerName = "harbora-svc-shop",
            DatabaseName = "shop",
            Username = "harbora",
            EncryptedPassword = protector.Protect("admin_secret"),
            InternalPort = 5432,
            Status = ServiceStatus.Running,
            Type = type
        };
        db.Add(instance);
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var engines = new FakeServerEngineFactory(docker);
        var clock = new Clock(Start);

        var grants = new DatabaseGrantExecutor(docker, protector, NullLogger<DatabaseGrantExecutor>.Instance);
        var engine = new ManagedServiceEngine(
            db, engines, protector, new NoopJobQueue(),
            new Harbora.Infrastructure.Billing.BillingGate(
                db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
            Options.Create(new HarboraRuntimeOptions()), clock, NullLogger<ManagedServiceEngine>.Instance);

        var service = new LogicalDatabaseService(
            db, protector, NullLogger<LogicalDatabaseService>.Instance, grants, engine);

        return new Stack(db, service, docker, instance);
    }

    /// <summary>An installation with no local reach at all — <c>DatabaseAccessLifecycleTests.Build</c>'s
    /// counterpart for this service.</summary>
    private static (BrittleContext Db, LogicalDatabaseService Service, ManagedService Instance) BuildUnreachable()
    {
        var db = new BrittleContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("logicaldb-unreachable-" + Guid.NewGuid()).Options);

        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = Guid.CreateVersion7(), ServerId = Guid.CreateVersion7(),
            Name = "Shop DB", ContainerName = "harbora-svc-shop", DatabaseName = "shop",
            Username = "harbora", InternalPort = 5432, Type = ManagedServiceType.PostgreSql
        };
        db.Add(instance);
        db.SaveChanges();

        var service = new LogicalDatabaseService(
            db, new PassthroughProtector(), NullLogger<LogicalDatabaseService>.Instance);

        return (db, service, instance);
    }

    // -------------------------------------------------------------------------------------------
    // Creation
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_database_issues_a_create_and_a_grant_and_writes_the_row_only_after_both_succeed()
    {
        var (db, service, docker, instance) = BuildLocal();

        var (created, error) = await service.CreateAsync(instance.Id, "orders", default);

        error.Should().BeNull();
        created.Should().NotBeNull();
        created!.Name.Should().Be("orders");
        created.IsDefault.Should().BeFalse();
        docker.OneOffCommands.Should().HaveCount(2, "one statement creates the database, a second creates its login");
        docker.OneOffCommands[0].Should().Contain("CREATE DATABASE");
        docker.OneOffCommands[1].Should().Contain("CREATE USER");

        (await db.ManagedServiceDatabases.SingleAsync()).Id.Should().Be(created.Id);
    }

    [Fact]
    public async Task A_database_that_fails_to_create_leaves_no_row_behind()
    {
        var (db, service, docker, instance) = BuildLocal();
        docker.OneOffExitCode = 1;

        var (created, error) = await service.CreateAsync(instance.Id, "orders", default);

        created.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(0,
            "a row that exists in Harbora and not on the engine is the defect this design exists to prevent");
    }

    [Fact]
    public async Task A_login_that_fails_after_the_database_was_created_drops_the_database_again()
    {
        var (db, service, docker, instance) = BuildLocal();
        // First one-off (CREATE DATABASE) succeeds, second (CREATE USER/GRANT) fails.
        docker.OneOffExitCodes.Enqueue(0);
        docker.OneOffExitCodes.Enqueue(1);

        var (created, error) = await service.CreateAsync(instance.Id, "orders", default);

        created.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        docker.OneOffCommands.Should().HaveCount(3,
            "create database, the failed login attempt, and the rollback that drops the database again");
        docker.OneOffCommands[2].Should().Contain("DROP DATABASE",
            "an empty database nobody can reach is not a safer failure than a missing row");
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public async Task Engines_without_a_clean_per_database_grant_story_are_refused_by_name_rather_than_faked(
        ManagedServiceType type)
    {
        var (db, service, docker, instance) = BuildLocal(type);

        var (created, error) = await service.CreateAsync(instance.Id, "orders", default);

        created.Should().BeNull();
        error.Should().Contain(type.ToString(), "the refusal must name which engine, not just say no");
        docker.OneOffCommands.Should().BeEmpty("an unsupported engine must never be asked to do anything");
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_installation_with_no_local_reach_refuses_rather_than_pretending_to_create_one()
    {
        var (db, service, instance) = BuildUnreachable();

        var (created, error) = await service.CreateAsync(instance.Id, "orders", default);

        created.Should().BeNull();
        error.Should().NotBeNullOrEmpty();
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Two_databases_asking_for_the_same_name_on_one_instance_do_not_collide()
    {
        var (db, service, _, instance) = BuildLocal();

        var (first, firstError) = await service.CreateAsync(instance.Id, "app", default);
        var (second, secondError) = await service.CreateAsync(instance.Id, "app", default);

        firstError.Should().BeNull();
        secondError.Should().BeNull();
        first!.Name.Should().Be("app");
        second!.Name.Should().Be("app_2", "the second request for the same name gets the next free one, never a silent share");
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(2);
    }

    // -------------------------------------------------------------------------------------------
    // Deletion
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_instances_own_default_database_cannot_be_deleted_on_its_own()
    {
        var (db, service, docker, instance) = BuildLocal();
        var logical = ManagedServiceDatabase.DefaultFor(instance)!;
        db.ManagedServiceDatabases.Add(logical);
        await db.SaveChangesAsync();

        var error = await service.DeleteAsync(logical.Id, default);

        error.Should().NotBeNullOrEmpty();
        docker.OneOffCommands.Should().BeEmpty("the default database is refused before anything touches the engine");
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_a_database_still_attached_to_an_app_is_refused_and_names_the_app()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var app = new App
        {
            WorkspaceId = instance.WorkspaceId, EnvironmentId = instance.EnvironmentId, ServerId = Guid.CreateVersion7(),
            Name = "checkout-app", Slug = "checkout-app", SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "ghcr.io/example/app:1.0"
        };
        db.Add(app);
        db.Add(new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = instance.Id, ManagedServiceDatabaseId = logical!.Id, Alias = "ORDERS"
        });
        await db.SaveChangesAsync();

        var error = await service.DeleteAsync(logical.Id, default);

        error.Should().Contain("checkout-app", "the person deleting may not know who is attached");
        docker.OneOffCommands.Should().BeEmpty();
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Deleting_an_unattached_database_drops_the_login_before_the_database_itself()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var error = await service.DeleteAsync(logical!.Id, default);

        error.Should().BeNull();
        docker.OneOffCommands.Should().HaveCount(2);
        docker.OneOffCommands[0].Should().Contain("DROP ROLE",
            "the login and its owned objects must go first, while the database it owns them in still exists");
        docker.OneOffCommands[1].Should().Contain("DROP DATABASE");
        (await db.ManagedServiceDatabases.CountAsync()).Should().Be(0);
    }
}
