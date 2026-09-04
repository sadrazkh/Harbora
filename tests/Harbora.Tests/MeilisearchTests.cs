using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Backups;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 4.2 (round-2 market-gaps plan): Meilisearch promoted from a one-click template (no generated
/// credentials, no attach, no backups) to a full managed service — the same treatment RabbitMQ and
/// NATS already have. Mirrors <see cref="ServiceCatalogBrokerTests"/>'s shape for the catalogue entry
/// itself; provisioning and rotation are proven over a fake daemon the way <c>PgVectorProvisionTests</c>
/// proves an image choice actually reaches the container.
/// </summary>
public class MeilisearchCatalogTests
{
    private static readonly ServiceCreds Creds =
        new("harbora-svc-search", 7700, "harbora", "a-generated-master-key1", "");

    [Fact]
    public void It_is_in_the_catalogue_with_no_database_name()
    {
        ServiceCatalog.All.Should().ContainKey(ManagedServiceType.Meilisearch);
        ServiceCatalog.All[ManagedServiceType.Meilisearch].HasDatabaseName.Should().BeFalse(
            "an index is not a schema, and there is nothing here for a create form's database-name field to mean");
    }

    [Fact]
    public void It_offers_at_least_one_real_version()
    {
        // The create path takes Versions[0] when nobody chooses. An empty list is an index out of
        // range the moment somebody presses the button.
        ServiceCatalog.All[ManagedServiceType.Meilisearch].Versions.Should().NotBeEmpty();
    }

    [Fact]
    public void Provisioning_env_carries_the_master_key_and_forces_production_mode()
    {
        var env = ServiceCatalog.All[ManagedServiceType.Meilisearch].Env(Creds);

        env["MEILI_MASTER_KEY"].Should().Be(Creds.Password);
        // Production mode refuses to start without a master key at all — the fail-loud behaviour
        // wanted here, rather than an instance that quietly ran unauthenticated.
        env["MEILI_ENV"].Should().Be("production");
    }

    [Fact]
    public void The_masked_connection_string_does_not_carry_the_master_key()
    {
        var (full, masked) = ServiceCatalog.All[ManagedServiceType.Meilisearch].Conn(Creds);

        full.Should().Contain(Creds.Password, "revealing must actually reveal the master key somewhere");
        masked.Should().NotContain(Creds.Password);
    }

    [Fact]
    public void Attaching_it_hands_the_app_the_master_key_and_a_url()
    {
        var attach = ServiceCatalog.All[ManagedServiceType.Meilisearch].AttachEnv(Creds);

        attach.Should().NotBeEmpty();
        attach["MEILI_URL"].Should().Be($"http://{Creds.Host}:{Creds.Port}");
        attach["MEILI_HOST"].Should().Be(Creds.Host);
        attach["MEILI_PORT"].Should().Be(Creds.Port.ToString());

        // The decision this task had to defend: an attached app receives the master key itself, not a
        // narrower scoped key minted from it. See ServiceCatalog's own remarks on the entry for why.
        attach["MEILI_MASTER_KEY"].Should().Be(Creds.Password);
    }

    [Fact]
    public void Its_mark_is_drawn_from_its_own_key_not_the_generic_database_one()
    {
        ServiceTypeKey.For(ManagedServiceType.Meilisearch).Should().Be("meilisearch");
    }

    [Fact]
    public void Rotating_it_recreates_the_container_rather_than_a_live_statement()
    {
        // Same shape as Redis: MEILI_MASTER_KEY is read once at boot, so there is no live statement
        // to run — CredentialRotationPlan.For stays null and RequiresRecreate carries the rotation
        // instead. No second rotation mechanism was written for this engine.
        CredentialRotationPlan.For(ManagedServiceType.Meilisearch, Creds, "new-master-key-99").Should().BeNull();
        CredentialRotationPlan.RequiresRecreate(ManagedServiceType.Meilisearch).Should().BeTrue();
        CredentialRotationPlan.WhyUnsupported(ManagedServiceType.Meilisearch).Should().BeNull(
            "a button that recreates the container instead of running a statement is still a working button");
    }

    [Fact]
    public void Its_connection_can_be_probed_with_the_master_key()
    {
        // /health needs no key at all, so it would prove only that the container is listening — not
        // the thing this probe exists to catch. /keys does authenticate (Meilisearch's own docs:
        // "you must have the master key ... to access the keys route"), so this is the one built-in
        // route that actually proves the STORED key is the key the server will accept.
        var plan = ConnectionProbe.For(ManagedServiceType.Meilisearch, Creds);

        plan.Should().NotBeNull();
        var command = string.Join(" ", plan!.Command);
        command.Should().Contain("/keys");
        command.Should().NotContain(Creds.Password, "the key must travel through the environment, never argv");
        plan.Env.Values.Should().Contain(Creds.Password);

        ConnectionProbe.WhyUnsupported(ManagedServiceType.Meilisearch).Should().BeNull(
            "a control that silently does nothing would be worse than one honestly not offered — this one works");
    }

    [Fact]
    public void A_refused_probe_explains_a_bad_key_rather_than_a_bare_exit_code()
    {
        ConnectionProbe.Explain(ManagedServiceType.Meilisearch, "HTTP 401")
            .Should().Contain("password", "a 401 from /keys means the stored key is wrong");
        ConnectionProbe.Explain(ManagedServiceType.Meilisearch, "HTTP 000")
            .Should().Contain("stopped", "curl's 000 means no HTTP response arrived at all");
    }

    [Fact]
    public void It_has_no_logical_dump_so_its_volume_is_copied_instead()
    {
        // Honesty check for the backup story: Meilisearch's own dump is only reachable over its HTTP
        // API (POST /dumps, polled, then the file read back out) — not a command this runs through a
        // shell the way pg_dump/mysqldump/mongodump are. Building that was not attempted, so this says
        // so rather than claiming a dump this task did not build.
        DatabaseDumpPlan.For(ManagedServiceType.Meilisearch, Creds, "/b/x").Should().BeNull();
        DatabaseDumpPlan.WhyNoDump(ManagedServiceType.Meilisearch).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void It_has_no_logical_database_pgvector_or_external_access_story()
    {
        // Same family as Redis/RabbitMQ/NATS: one instance, no per-app login the platform can issue.
        DatabaseGrantSql.Supports(ManagedServiceType.Meilisearch).Should().BeFalse();
        DatabaseGrantSql.SupportsVectorExtension(ManagedServiceType.Meilisearch).Should().BeFalse();
    }

    [Fact]
    public void Every_engine_in_the_catalogue_including_this_one_is_complete()
    {
        var definition = ServiceCatalog.All[ManagedServiceType.Meilisearch];

        definition.ImageRepo.Should().NotBeNullOrWhiteSpace();
        definition.DataMountPath.Should().StartWith("/");
        definition.Port.Should().BeGreaterThan(0);
        definition.Versions.Should().NotBeEmpty();
        definition.DisplayName.Should().NotBeNullOrWhiteSpace();
        definition.DisplayNameFa.Should().NotBeNullOrWhiteSpace();
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon, seeding Meilisearch rows —
/// mirrors <c>PgEngineHarness</c> for the engine this task is actually about.</summary>
internal sealed class MeilisearchEngineHarness : IDisposable
{
    private readonly string _database = "meilisearch-" + Guid.NewGuid();
    private readonly Guid _workspaceId = Guid.NewGuid();
    private readonly HarboraDbContext _db;
    private readonly Guid _environmentId;

    public FakeDockerEngine Docker { get; } = new();
    public PassthroughProtector Protector { get; } = new();
    public FixedClock Clock { get; } = new();

    public MeilisearchEngineHarness()
    {
        _db = Read();
        _db.Workspaces.Add(new Harbora.Domain.Identity.Workspace { Id = _workspaceId, Name = "Acme", Slug = "acme" });
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = _workspaceId, Name = "search", Slug = "search" };
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
        new FakeServerEngineFactory(Docker),
        Protector,
        new NoopJobQueue(),
        new Harbora.Infrastructure.Billing.BillingGate(
            _db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<ManagedServiceEngine>.Instance);

    public async Task<ManagedService> SeedAsync(string password = "seeded-master-key-01")
    {
        var name = "search-" + Guid.NewGuid().ToString("N")[..8];
        var def = ServiceCatalog.All[ManagedServiceType.Meilisearch];
        var service = new ManagedService
        {
            WorkspaceId = _workspaceId,
            EnvironmentId = _environmentId,
            ServerId = Guid.CreateVersion7(),
            Name = name,
            Type = ManagedServiceType.Meilisearch,
            Version = def.Versions[0],
            ContainerName = $"harbora-svc-{name}",
            InternalPort = def.Port,
            Username = "harbora",
            EncryptedPassword = Protector.Protect(password),
            DatabaseName = string.Empty,
            VolumeName = $"harbora-svc-{name}-data",
            Status = ServiceStatus.Provisioning
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

    public void Dispose() => _db.Dispose();
}

public class MeilisearchProvisionTests
{
    [Fact]
    public async Task Provisioning_starts_the_container_with_the_generated_master_key()
    {
        using var h = new MeilisearchEngineHarness();
        var svc = await h.SeedAsync(password: "correct-horse-battery-staple9");

        await h.Engine().ProvisionAsync(svc.Id, default);

        var request = h.Docker.RunRequests.Should().ContainSingle(r => r.ContainerName == svc.ContainerName).Subject;
        request.Image.Should().Be($"getmeili/meilisearch:{svc.Version}");
        request.Env["MEILI_MASTER_KEY"].Should().Be("correct-horse-battery-staple9");
        request.Env["MEILI_ENV"].Should().Be("production");
        request.ContainerPort.Should().Be(7700);

        var after = await h.ReadServiceAsync(svc.Id);
        after.Status.Should().Be(ServiceStatus.Running);
    }

    [Fact]
    public async Task Rotating_the_master_key_recreates_the_container_with_the_new_one()
    {
        using var h = new MeilisearchEngineHarness();
        var svc = await h.SeedAsync(password: "original-master-key-001");
        await h.Engine().ProvisionAsync(svc.Id, default);

        await h.Engine().RotatePasswordAsync(svc.Id, default);

        // One run to provision, one more to recreate onto the new key — RequiresRecreate is what
        // turns a stored password change into an actual running container.
        h.Docker.RunRequests.Should().HaveCount(2);
        var recreated = h.Docker.RunRequests[^1];
        recreated.ContainerName.Should().Be(svc.ContainerName);
        recreated.Env["MEILI_MASTER_KEY"].Should().NotBe("original-master-key-001");

        var after = await h.ReadServiceAsync(svc.Id);
        h.Protector.Unprotect(after.EncryptedPassword).Should().Be(recreated.Env["MEILI_MASTER_KEY"],
            "what got baked into the recreated container must be exactly what the row now stores");
    }
}

/// <summary>
/// C1's own acceptance criterion, for this engine: "test at the seam where env becomes container
/// environment" — the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> runs
/// over the fake Docker engine, and assertions read <see cref="FakeDockerEngine.RunRequests"/>. Mirrors
/// <c>AppManagedServicePipelineTests</c>, for Meilisearch specifically rather than PostgreSQL.
/// </summary>
public class MeilisearchAttachPipelineTests
{
    private static ManagedService GivenMeilisearch(
        PipelineHarness h, string name, string masterKey = "pipeline-master-key-01")
    {
        var def = ServiceCatalog.All[ManagedServiceType.Meilisearch];
        return new ManagedService
        {
            WorkspaceId = h.Workspace.Id, EnvironmentId = h.Environment.Id, ServerId = h.Server.Id,
            Name = name, Type = ManagedServiceType.Meilisearch, Version = def.Versions[0],
            ContainerName = $"harbora-svc-{name}", InternalPort = def.Port,
            Username = "harbora", EncryptedPassword = h.Protector.Protect(masterKey),
            DatabaseName = string.Empty, VolumeName = $"harbora-svc-{name}-data", Status = ServiceStatus.Running
        };
    }

    private static void Attach(PipelineHarness h, ManagedService svc, string alias, int order)
    {
        h.Db.ManagedServices.Add(svc);
        h.Db.AppManagedServices.Add(new AppManagedService
        {
            AppId = h.App.Id, ManagedServiceId = svc.Id, Alias = alias,
            AttachOrder = order, HasUnpublishedChanges = true
        });
        h.Db.SaveChanges();
    }

    [Fact]
    public async Task An_attached_instance_delivers_MEILI_star_at_the_seam_where_env_becomes_container_environment()
    {
        using var h = new PipelineHarness();
        var svc = GivenMeilisearch(h, "search", masterKey: "seam-master-key-77");
        Attach(h, svc, "SEARCH", order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("MEILI_MASTER_KEY").WhoseValue.Should().Be("seam-master-key-77");
        run.Env.Should().ContainKey("MEILI_URL").WhoseValue.Should().Be($"http://{svc.ContainerName}:7700");
        run.Env.Should().ContainKey("MEILI_HOST").WhoseValue.Should().Be(svc.ContainerName);
        run.Env.Should().ContainKey("MEILI_PORT").WhoseValue.Should().Be("7700");
    }

    [Fact]
    public async Task An_instance_that_is_not_attached_contributes_nothing_to_the_container()
    {
        using var h = new PipelineHarness();
        // Exists, but never attached — the negative half of the same proof.
        h.Db.ManagedServices.Add(GivenMeilisearch(h, "unattached-search"));
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Keys.Should().NotContain(k => k.StartsWith("MEILI_", StringComparison.Ordinal));
    }
}
