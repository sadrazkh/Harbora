using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
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
/// 1.7 (pgvector-as-option plan): pgvector as an option on a PostgreSQL instance — deliberately not a
/// vector-database product, just the extension itself. Three layers, each proven on its own:
/// <see cref="DatabaseGrantSql"/> builds the right statement and refuses the wrong engine before any
/// value reaches SQL; <see cref="DatabaseGrantExecutor"/> proves that statement actually reaches the
/// fake engine and that its own words come back on a refusal; <see cref="ManagedServiceEngine"/>
/// proves which image a rebuild actually runs. <c>LogicalDatabaseServiceTests</c> carries the
/// per-database <c>EnableVectorExtensionAsync</c> proof, reusing its own harness rather than a
/// parallel one.
/// </summary>
public class PgVectorSqlTests
{
    [Theory]
    [InlineData(ManagedServiceType.PostgreSql, true)]
    [InlineData(ManagedServiceType.MySql, false)]
    [InlineData(ManagedServiceType.MariaDb, false)]
    [InlineData(ManagedServiceType.Redis, false)]
    [InlineData(ManagedServiceType.MongoDb, false)]
    [InlineData(ManagedServiceType.RabbitMq, false)]
    [InlineData(ManagedServiceType.Nats, false)]
    public void Only_postgresql_has_a_vector_extension_story(ManagedServiceType type, bool expected) =>
        DatabaseGrantSql.SupportsVectorExtension(type).Should().Be(expected);

    [Fact]
    public void The_unsupported_reason_names_the_engine()
    {
        var reason = DatabaseGrantSql.VectorExtensionUnsupportedReason(ManagedServiceType.Redis);
        reason.Should().Contain("Redis", "the refusal must name which engine, not just say no");
    }

    [Fact]
    public void Building_the_statement_for_postgresql_targets_the_logical_database_and_is_idempotent()
    {
        var command = DatabaseGrantSql.CreateVectorExtension(
            ManagedServiceType.PostgreSql, "harbora-svc-shop", 5432, "harbora", "orders");

        command.Should().NotBeNull();
        command!.Command.Should().Contain("-d").And.Contain("orders");
        string.Join(' ', command.Command).Should().Contain("CREATE EXTENSION IF NOT EXISTS vector",
            "pressing the button twice, or reaching a database that already has it, must be a success");
    }

    [Theory]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    [InlineData(ManagedServiceType.Redis)]
    public void No_statement_is_built_for_an_engine_with_no_vector_story(ManagedServiceType type) =>
        DatabaseGrantSql.CreateVectorExtension(type, "host", 5432, "admin", "db").Should().BeNull();

    [Fact]
    public void An_unsafe_database_name_refuses_to_build_a_statement() =>
        DatabaseGrantSql.CreateVectorExtension(
            ManagedServiceType.PostgreSql, "host", 5432, "admin", "orders\"; DROP TABLE users; --").Should().BeNull();
}

/// <summary>Proves the statement actually reaches the fake engine, and that a refusal carries the
/// engine's own words rather than a generic "operation failed".</summary>
public class PgVectorExecutorTests
{
    private readonly FakeDockerEngine _docker = new();
    private readonly PassthroughProtector _protector = new();
    private FakeServerEngineFactory Engines() => new(_docker);

    private DatabaseGrantExecutor Executor() =>
        new(Engines(), _protector, NullLogger<DatabaseGrantExecutor>.Instance);

    private ManagedService Service() => new()
    {
        Id = Guid.CreateVersion7(),
        WorkspaceId = Guid.CreateVersion7(),
        ServerId = Guid.Empty,
        Name = "shop-db",
        Type = ManagedServiceType.PostgreSql,
        ContainerName = "harbora-svc-shop",
        DatabaseName = "shop",
        Username = "harbora",
        EncryptedPassword = _protector.Protect("admin_secret"),
        InternalPort = 5432
    };

    [Fact]
    public async Task A_successful_create_extension_reaches_the_engine_and_is_answered()
    {
        var (ok, error, answered) = await Executor().CreateVectorExtensionAsync(
            Service(), "harbora-env-net", "orders", default);

        ok.Should().BeTrue();
        error.Should().BeNull();
        answered.Should().BeTrue();
        _docker.OneOffCommands.Should().ContainSingle().Which.Should().Contain("CREATE EXTENSION");
    }

    [Fact]
    public async Task A_refusal_from_the_engine_is_surfaced_in_the_engines_own_words()
    {
        _docker.OneOffExitCode = 1;
        _docker.OneOffOutput.Add("ERROR:  could not open extension control file \"/…/vector.control\": No such file or directory");

        var (ok, error, answered) = await Executor().CreateVectorExtensionAsync(
            Service(), "harbora-env-net", "orders", default);

        ok.Should().BeFalse();
        answered.Should().BeTrue("the client ran and gave a verdict — this is not a lost connection");
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_non_postgresql_engine_is_refused_by_name_and_nothing_is_sent()
    {
        var service = Service();
        service.Type = ManagedServiceType.MySql;

        var (ok, error, answered) = await Executor().CreateVectorExtensionAsync(
            service, "harbora-env-net", "orders", default);

        ok.Should().BeFalse();
        answered.Should().BeTrue("refused before anything was ever attempted");
        error.Should().Contain("MySql", "the refusal must name which engine, not just say no");
        _docker.Calls.Should().BeEmpty("an unsupported engine must never be asked to do anything");
    }

    [Fact]
    public async Task Losing_contact_with_the_engine_is_not_answered()
    {
        _docker.OneOffThrows = new InvalidOperationException("connection reset");

        var (ok, _, answered) = await Executor().CreateVectorExtensionAsync(
            Service(), "harbora-env-net", "orders", default);

        ok.Should().BeFalse();
        answered.Should().BeFalse(
            "a dropped connection proves nothing either way — the statement may still have landed");
    }
}

/// <summary>Proves which image a rebuild actually runs — the only place capability is decided; a
/// logical database's own CREATE EXTENSION never guesses at it (see <see cref="PgVectorExecutorTests"/>).</summary>
public class PgVectorProvisionTests
{
    [Theory]
    [InlineData("16-alpine", "pgvector/pgvector:pg16")]
    [InlineData("15-alpine", "pgvector/pgvector:pg15")]
    public async Task An_instance_with_pgvector_enabled_is_rebuilt_onto_the_matching_pgvector_image(
        string version, string expectedImage)
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync(version: version, pgVectorEnabled: true);

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Image.Should().Be(expectedImage);
    }

    [Fact]
    public async Task An_instance_that_never_asked_for_pgvector_keeps_running_the_plain_image()
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync(version: "16-alpine", pgVectorEnabled: false);

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Image.Should().Be("postgres:16-alpine",
            "an instance nobody asked for pgvector on must not be silently switched to a different image");
    }

    [Fact]
    public async Task Enabling_pgvector_marks_the_instance_unpublished_until_the_next_rebuild()
    {
        using var h = new PgEngineHarness();
        var svc = await h.SeedAsync(pgVectorEnabled: false);
        svc.PgVectorEnabled = true;
        svc.HasUnpublishedChanges = true;
        await h.SaveAsync();

        var before = await h.ReadServiceAsync(svc.Id);
        before.HasUnpublishedChanges.Should().BeTrue("saved, but only a rebuild makes it real");

        await h.Engine().ProvisionAsync(svc.Id, default);

        var after = await h.ReadServiceAsync(svc.Id);
        after.HasUnpublishedChanges.Should().BeFalse(
            "the container was just rebuilt from this row's own settings, so pgvector is no longer merely requested");
        after.RunningImage.Should().Be("pgvector/pgvector:pg16");
    }
}

/// <summary>
/// The instance-level toggle's off-switch guard: turning pgvector off is not itself destructive
/// (nothing here drops anything), but rebuilding onto the plain image afterward silently breaks
/// every query touching a <c>vector</c> column on a database that still has the extension installed
/// — so it is refused by name rather than accepted quietly. Neither test reaches the engine: the
/// toggle only reads/writes rows, so a bare <see cref="HarboraDbContext"/> is enough.
/// </summary>
public class PgVectorInstanceToggleTests
{
    private static HarboraDbContext NewDb() => new BrittleContext(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("pgvector-toggle-" + Guid.NewGuid()).Options);

    private static LogicalDatabaseService Service(HarboraDbContext db) =>
        new(db, new PassthroughProtector(), NullLogger<LogicalDatabaseService>.Instance, new FixedClock());

    private static ManagedService SeedInstance(HarboraDbContext db)
    {
        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = Guid.CreateVersion7(),
            ServerId = Guid.Empty,
            Name = "shop-db",
            Type = ManagedServiceType.PostgreSql,
            ContainerName = "harbora-svc-shop",
            DatabaseName = "shop",
            Username = "harbora",
            InternalPort = 5432,
            PgVectorEnabled = true,
            HasUnpublishedChanges = false
        };
        db.Add(instance);
        db.SaveChanges();
        return instance;
    }

    [Fact]
    public async Task Turning_it_off_while_a_database_still_has_the_extension_is_refused_and_names_it()
    {
        var db = NewDb();
        var instance = SeedInstance(db);
        db.Add(new ManagedServiceDatabase
        {
            WorkspaceId = instance.WorkspaceId, ManagedServiceId = instance.Id,
            Name = "orders", Username = "orders_user", HasVectorExtension = true
        });
        await db.SaveChangesAsync();

        var error = await Service(db).SetPgVectorEnabledAsync(instance.Id, false, default);

        error.Should().NotBeNullOrWhiteSpace(
            "a toggle that silently accepts this leaves a database whose vector queries are about to break with no warning");
        error.Should().Contain("orders", "the refusal must name which database, not just say it is in use");

        var stored = await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == instance.Id);
        stored.PgVectorEnabled.Should().BeTrue("a refused request must change nothing");
        stored.HasUnpublishedChanges.Should().BeFalse();
    }

    [Fact]
    public async Task Turning_it_off_when_no_database_has_the_extension_installed_succeeds()
    {
        var db = NewDb();
        var instance = SeedInstance(db);
        db.Add(new ManagedServiceDatabase
        {
            WorkspaceId = instance.WorkspaceId, ManagedServiceId = instance.Id,
            Name = "orders", Username = "orders_user", HasVectorExtension = false
        });
        await db.SaveChangesAsync();

        var error = await Service(db).SetPgVectorEnabledAsync(instance.Id, false, default);

        error.Should().BeNull();

        var stored = await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == instance.Id);
        stored.PgVectorEnabled.Should().BeFalse();
        stored.HasUnpublishedChanges.Should().BeTrue("saved, but only a rebuild makes the plain image real");
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon, seeding PostgreSQL rows —
/// mirrors <c>RedisEngineHarness</c> for the engine this feature is actually about.</summary>
internal sealed class PgEngineHarness : IDisposable
{
    private readonly string _database = "pgvector-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly HarboraDbContext _db;
    private readonly Guid _environmentId;

    public FakeDockerEngine Docker { get; } = new();
    public PassthroughProtector Protector { get; } = new();
    public FixedClock Clock { get; } = new();

    public PgEngineHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Name = "shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = _workspaceId, ProjectId = project.Id,
            Name = "prod", Slug = "prod", IsDefault = true
        };
        _db.Projects.Add(project);
        _db.Environments.Add(environment);
        _db.SaveChanges();
        _environmentId = environment.Id;
    }

    private HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    public ManagedServiceEngine Engine() => new(
        _db,
        new SingleEngineFactory(Docker),
        Protector,
        new NoopJobQueue(),
        new Harbora.Infrastructure.Billing.BillingGate(
            _db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<ManagedServiceEngine>.Instance);

    public async Task<ManagedService> SeedAsync(string version = "16-alpine", bool pgVectorEnabled = false)
    {
        var name = "db-" + Guid.NewGuid().ToString("N")[..8];
        var service = new ManagedService
        {
            WorkspaceId = _workspaceId,
            EnvironmentId = _environmentId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.PostgreSql,
            Version = version,
            ContainerName = $"harbora-svc-{name}",
            InternalPort = 5432,
            Username = "harbora",
            EncryptedPassword = Protector.Protect($"{name}-original-pw12"),
            DatabaseName = name.Replace('-', '_'),
            VolumeName = $"harbora-svc-{name}-data",
            Status = ServiceStatus.Provisioning,
            PgVectorEnabled = pgVectorEnabled
        };
        _db.ManagedServices.Add(service);
        await _db.SaveChangesAsync();
        return service;
    }

    public async Task<ManagedService> ReadServiceAsync(Guid id)
    {
        using var db = Read();
        return await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public Task SaveAsync() => _db.SaveChangesAsync();

    public void Dispose() => _db.Dispose();
}
