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

        // The factory, not the engine: D4 (HARBORA-0059) moved this off the local Docker engine so a
        // database placed on another server is reached through that server's own. `engines` wraps
        // this same fake, so what is under test here is unchanged — only the seam it arrives through.
        var grants = new DatabaseGrantExecutor(engines, protector, NullLogger<DatabaseGrantExecutor>.Instance);
        var engine = new ManagedServiceEngine(
            db, engines, protector, new NoopJobQueue(),
            new Harbora.Infrastructure.Billing.BillingGate(
                db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
            Options.Create(new HarboraRuntimeOptions()), clock, NullLogger<ManagedServiceEngine>.Instance);

        var service = new LogicalDatabaseService(
            db, protector, NullLogger<LogicalDatabaseService>.Instance, clock, grants, engine);

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
            db, new PassthroughProtector(), NullLogger<LogicalDatabaseService>.Instance, new Clock(Start));

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

    // -------------------------------------------------------------------------------------------
    // Renaming (D3, 2026-08-25 shared-databases plan)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Renaming_a_non_default_database_issues_one_alter_statement_and_updates_the_row()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var error = await service.RenameAsync(logical!.Id, "invoices", default);

        error.Should().BeNull();
        docker.OneOffCommands.Should().ContainSingle().Which.Should().Contain("ALTER DATABASE");
        (await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id)).Name.Should().Be("invoices");
    }

    [Fact]
    public async Task Renaming_to_a_name_already_taken_on_the_instance_gets_the_next_free_one_instead()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (first, _) = await service.CreateAsync(instance.Id, "orders", default);
        var (second, _) = await service.CreateAsync(instance.Id, "invoices", default);
        docker.OneOffCommands.Clear();

        var error = await service.RenameAsync(second!.Id, "orders", default);

        error.Should().BeNull();
        (await db.ManagedServiceDatabases.SingleAsync(d => d.Id == second.Id)).Name.Should().Be("orders_2",
            "renaming into a name a neighbour already has must never silently collide with it");
        (await db.ManagedServiceDatabases.SingleAsync(d => d.Id == first!.Id)).Name.Should().Be("orders");
    }

    [Fact]
    public async Task The_instances_own_default_database_cannot_be_renamed()
    {
        var (db, service, docker, instance) = BuildLocal();
        var logical = ManagedServiceDatabase.DefaultFor(instance)!;
        db.ManagedServiceDatabases.Add(logical);
        await db.SaveChangesAsync();

        var error = await service.RenameAsync(logical.Id, "renamed", default);

        error.Should().NotBeNullOrEmpty();
        docker.OneOffCommands.Should().BeEmpty("the default database is refused before anything touches the engine");
        (await db.ManagedServiceDatabases.SingleAsync()).Name.Should().Be(instance.DatabaseName);
    }

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    public async Task Renaming_on_an_engine_with_no_lossless_rename_is_refused_by_name(ManagedServiceType type)
    {
        var (db, service, docker, instance) = BuildLocal(type);
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var error = await service.RenameAsync(logical!.Id, "invoices", default);

        error.Should().Contain(type.ToString(), "the refusal must name which engine, not just say no");
        docker.OneOffCommands.Should().BeEmpty("an engine with no lossless rename must never be asked to attempt one");
        (await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id)).Name.Should().Be("orders");
    }

    [Fact]
    public async Task A_rename_the_engine_refuses_leaves_the_old_name_in_place()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();
        docker.OneOffExitCode = 1;

        var error = await service.RenameAsync(logical!.Id, "invoices", default);

        error.Should().NotBeNullOrEmpty();
        (await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id)).Name.Should().Be("orders",
            "a rename the engine refused must not be reflected in Harbora's own row");
    }

    [Fact]
    public async Task Renaming_marks_every_app_attached_to_that_database_as_having_unpublished_changes()
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
        var attachment = new AppManagedService
        {
            AppId = app.Id, ManagedServiceId = instance.Id, ManagedServiceDatabaseId = logical!.Id,
            Alias = "ORDERS", HasUnpublishedChanges = false
        };
        db.Add(attachment);
        await db.SaveChangesAsync();

        var error = await service.RenameAsync(logical.Id, "invoices", default);

        error.Should().BeNull();
        (await db.AppManagedServices.SingleAsync(a => a.Id == attachment.Id)).HasUnpublishedChanges.Should().BeTrue(
            "the running container's connection string names the old database, exactly like a password rotation");
    }

    [Fact]
    public async Task Renaming_to_the_name_it_already_has_succeeds_without_touching_the_engine()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var error = await service.RenameAsync(logical!.Id, "orders", default);

        error.Should().BeNull();
        docker.OneOffCommands.Should().BeEmpty("nothing changed, so nothing needed telling the engine");
    }

    // -------------------------------------------------------------------------------------------
    // pgvector (1.7, pgvector-as-option plan)
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Enabling_pgvector_issues_a_create_extension_statement_and_records_the_engines_answer()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var (present, error) = await service.EnableVectorExtensionAsync(logical!.Id, default);

        present.Should().BeTrue();
        error.Should().BeNull();
        docker.OneOffCommands.Should().ContainSingle().Which.Should().Contain("CREATE EXTENSION",
            "the CREATE EXTENSION must actually reach the engine, not just flip a flag Harbora invented");

        var stored = await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id);
        stored.HasVectorExtension.Should().BeTrue();
        stored.VectorExtensionCheckedAt.Should().Be(Start, "read from the engine's own answer, not left unset");
    }

    [Fact]
    public async Task A_refusal_from_the_engine_is_surfaced_in_its_own_words_and_recorded_as_absent()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();
        docker.OneOffExitCode = 1;
        docker.OneOffOutput.Add("ERROR:  could not open extension control file \"vector.control\": No such file or directory");

        var (present, error) = await service.EnableVectorExtensionAsync(logical!.Id, default);

        present.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace("a toggle that reports success while the engine refused is this codebase's defining defect class");

        var stored = await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id);
        stored.HasVectorExtension.Should().BeFalse("the engine was asked and said no — this is a known negative, not 'never checked'");
        stored.VectorExtensionCheckedAt.Should().Be(Start);
    }

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    [InlineData(ManagedServiceType.Redis)]
    [InlineData(ManagedServiceType.MongoDb)]
    [InlineData(ManagedServiceType.RabbitMq)]
    [InlineData(ManagedServiceType.Nats)]
    public async Task A_non_postgresql_engine_is_refused_by_name_and_nothing_reaches_it(ManagedServiceType type)
    {
        var (db, service, docker, instance) = BuildLocal(type);
        var logical = ManagedServiceDatabase.DefaultFor(instance)!;
        db.ManagedServiceDatabases.Add(logical);
        await db.SaveChangesAsync();

        var (present, error) = await service.EnableVectorExtensionAsync(logical.Id, default);

        present.Should().BeNull();
        error.Should().Contain(type.ToString(), "the refusal must name which engine, not just say no");
        docker.OneOffCommands.Should().BeEmpty("an unsupported engine must never be asked to do anything");

        var stored = await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id);
        stored.HasVectorExtension.Should().BeNull("refused before the engine was ever asked, so nothing was learned");
    }

    [Fact]
    public async Task An_installation_with_no_local_reach_refuses_rather_than_pretending_to_enable_it()
    {
        var (db, service, instance) = BuildUnreachable();
        var logical = ManagedServiceDatabase.DefaultFor(instance)!;
        db.ManagedServiceDatabases.Add(logical);
        await db.SaveChangesAsync();

        var (present, error) = await service.EnableVectorExtensionAsync(logical.Id, default);

        present.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();

        var stored = await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id);
        stored.HasVectorExtension.Should().BeNull();
    }

    [Fact]
    public async Task Losing_contact_with_the_engine_does_not_overwrite_what_was_last_known()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();
        // A prior successful check, so this proves the lost-contact run does not clobber it.
        logical!.HasVectorExtension = true;
        logical.VectorExtensionCheckedAt = Start;
        await db.SaveChangesAsync();
        docker.OneOffThrows = new InvalidOperationException("connection reset");

        var (present, error) = await service.EnableVectorExtensionAsync(logical.Id, default);

        present.Should().BeNull("nothing was risked, so nothing is known — not the same as 'no'");
        error.Should().NotBeNullOrWhiteSpace();

        var stored = await db.ManagedServiceDatabases.SingleAsync(d => d.Id == logical.Id);
        stored.HasVectorExtension.Should().BeTrue(
            "a run that lost contact must not tell a customer their extension vanished when it may still be sitting there");
    }

    [Fact]
    public async Task Enabling_pgvector_twice_is_idempotent()
    {
        var (db, service, docker, instance) = BuildLocal();
        var (logical, _) = await service.CreateAsync(instance.Id, "orders", default);
        docker.OneOffCommands.Clear();

        var first = await service.EnableVectorExtensionAsync(logical!.Id, default);
        var second = await service.EnableVectorExtensionAsync(logical.Id, default);

        first.Present.Should().BeTrue();
        second.Present.Should().BeTrue("pressing this on a database that already has pgvector is success, not an error");
        docker.OneOffCommands.Should().HaveCount(2);
    }
}
