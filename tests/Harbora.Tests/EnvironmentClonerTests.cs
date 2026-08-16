using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Environment = Harbora.Domain.Projects.Environment;

namespace Harbora.Tests;

/// <summary>
/// Copying an environment, end to end against a real database context.
///
/// <see cref="ClonePlanTests"/> covers the names. These cover the thing a name cannot: that the copy
/// does not reach into the original. A copy whose database password was carried over is a staging
/// deploy that writes to production, and it fails silently — everything comes up, everything
/// connects, and the queries go to the wrong server.
/// </summary>
public class EnvironmentClonerTests
{
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Server = Guid.CreateVersion7();

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow { get; } = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Scheduler : ISchedulerService
    {
        public PlacementResult Result = PlacementResult.Placed(Server);
        public Task<PlacementResult> PlaceAsync(long memoryBytes, double cpu, string? pool, CancellationToken ct)
            => Task.FromResult(Result);
        public Task<PlacementResult> CheckAsync(Guid serverId, long memoryBytes, double cpu, CancellationToken ct)
            => Task.FromResult(Result);
    }

    private sealed class Quota : IQuotaService
    {
        public WorkspaceUsage Usage = new("Default", 1, 0, 1, 0, 0, 0, 0, 0, false);
        public QuotaCheck Single = QuotaCheck.Ok;

        public Task<WorkspaceUsage> GetUsageAsync(Guid workspaceId, CancellationToken ct)
            => Task.FromResult(Usage);
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? size, Guid? exclude, CancellationToken ct)
            => Task.FromResult(Single);
        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct)
            => Task.FromResult(Single);
        public Task<QuotaCheck> CanAddWorkloadsAsync(Guid w, WorkloadQuotaDelta delta, CancellationToken ct)
        {
            if (Usage.MaxApps > 0 && Usage.Apps + delta.Apps > Usage.MaxApps)
                return Task.FromResult(QuotaCheck.Deny(
                    $"This copy needs {delta.Apps} applications; {Usage.Apps} / {Usage.MaxApps} are in use."));
            if (Usage.MaxServices > 0 && Usage.Services + delta.Services > Usage.MaxServices)
                return Task.FromResult(QuotaCheck.Deny(
                    $"This copy needs {delta.Services} databases; {Usage.Services} / {Usage.MaxServices} are in use."));
            if (Usage.MaxMemoryBytes > 0 && Usage.MemoryUsedBytes + delta.MemoryBytes > Usage.MaxMemoryBytes)
                return Task.FromResult(QuotaCheck.Deny("Memory quota exceeded."));
            if (Usage.MaxCpuCores > 0 && Usage.CpuUsed + delta.CpuCores > Usage.MaxCpuCores)
                return Task.FromResult(QuotaCheck.Deny("CPU quota exceeded."));
            return Task.FromResult(QuotaCheck.Ok);
        }

        /// <summary>
        /// Only the domain term is modelled — that is what <c>EnvironmentCloner.QuotaRefusalAsync</c>
        /// computes from the plan rather than taking as an input, and the one this fixture needs a real
        /// opinion about to prove that term is actually reached.
        /// </summary>
        public Task<QuotaCheck> CanAddGovernedResourcesAsync(
            Guid w, GovernanceQuotaDelta delta, CancellationToken ct)
        {
            if (Usage.MaxDomains > 0 && Usage.Domains + delta.Domains > Usage.MaxDomains)
                return Task.FromResult(QuotaCheck.Deny(
                    $"This copy needs {delta.Domains} domain(s); {Usage.Domains} / {Usage.MaxDomains} are in use."));
            return Task.FromResult(QuotaCheck.Ok);
        }
    }

    /// <summary>
    /// Hands out a connection string that names the service it was asked about, so a variable
    /// pointing at the wrong one is visible in the assertion rather than inferred.
    /// </summary>
    private sealed class Engine(HarboraDbContext db) : IManagedServiceEngine
    {
        public readonly List<Guid> Provisioned = [];
        public bool Throws;

        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = [];

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
        {
            Provisioned.Add(serviceId);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct)
        {
            if (Throws) throw new InvalidOperationException("no");

            var row = db.ManagedServices.AsNoTracking().First(s => s.Id == serviceId);
            IReadOnlyDictionary<string, string> env = new Dictionary<string, string>
            {
                ["DATABASE_URL"] = $"postgres://{row.ContainerName}/{row.DatabaseName}",
                ["PGHOST"] = row.ContainerName
            };
            return Task.FromResult(env);
        }

        public Task StartAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(Guid id, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> MeasureStorageAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> RotatePasswordAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> TestConnectionAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record Harness(
        HarboraDbContext Db, EnvironmentCloner Cloner, Environment Source,
        Engine Engine, Quota Quota, Scheduler Scheduler,
        App SourceApp, ManagedService SourceService);

    private static Harness Build()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("clone-" + Guid.NewGuid()).Options);

        var project = new Harbora.Domain.Projects.Project
        { WorkspaceId = Workspace, Name = "Shop", Slug = "shop" };
        var source = new Environment
        {
            WorkspaceId = Workspace, Project = project,
            Name = "Production", Slug = "production", IsDefault = true, IsProtected = true
        };

        var service = new ManagedService
        {
            WorkspaceId = Workspace, Environment = source, ServerId = Server,
            Name = "main db", Type = ManagedServiceType.PostgreSql, Version = "16",
            ContainerName = "harbora-svc-main-db", VolumeName = "harbora-svc-main-db-data",
            DatabaseName = "main_db", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = "the-original-password", Status = ServiceStatus.Running,
            StorageBytes = 4096, StorageMeasuredAt = DateTimeOffset.UnixEpoch,
            RunningImage = "postgres:16"
        };

        var app = new App
        {
            WorkspaceId = Workspace, Environment = source, ServerId = Server,
            Name = "API", Slug = "api", SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "nginx:1", ContainerPort = 8080, Status = AppStatus.Running,
            PreviewsEnabled = true, ActiveDeploymentId = Guid.CreateVersion7(),
            PublishedHostPort = 31000
        };
        app.EnvironmentVariables.Add(new EnvironmentVariable
        { Key = "APP_SECRET", Value = "keep-me", IsSecret = true });
        app.EnvironmentVariables.Add(new EnvironmentVariable
        { Key = "DATABASE_URL", Value = "postgres://harbora-svc-main-db/main_db", IsSecret = true });
        app.EnvironmentVariables.Add(new EnvironmentVariable
        { Key = "MAIN_DB_DATABASE_URL", Value = "postgres://harbora-svc-main-db/main_db", IsSecret = true });
        app.Volumes.Add(new Volume
        {
            Name = "harbora-vol-api-data", MountPath = "/data",
            StorageBytes = 900_000, StorageMeasuredAt = DateTimeOffset.UnixEpoch
        });
        app.Domains.Add(new Harbora.Domain.Networking.DomainName
        { Host = "shop.example.com" });

        db.Projects.Add(project);
        db.Environments.Add(source);
        db.ManagedServices.Add(service);
        db.Apps.Add(app);
        db.SaveChanges();

        var engine = new Engine(db);
        var quota = new Quota();
        var scheduler = new Scheduler();

        var cloner = new EnvironmentCloner(
            db, engine, quota, scheduler, new Fakes.PassthroughProtector(), new Clock(),
            new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, new Clock(), Microsoft.Extensions.Options.Options.Create(
                    new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            NullLogger<EnvironmentCloner>.Instance,
            new Harbora.Infrastructure.Networking.AppAddressAssigner(db, new ConfigurationBuilder().Build()));

        return new Harness(db, cloner, source, engine, quota, scheduler, app, service);
    }

    // ---- what gets made ----

    [Fact]
    public async Task The_copy_holds_the_same_shape_as_the_original()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeTrue(outcome.Reason);
        var copy = await h.Db.Environments.FirstAsync(e => e.Id == outcome.EnvironmentId);
        copy.Slug.Should().Be("staging");
        copy.ProjectId.Should().Be(h.Source.ProjectId);

        (await h.Db.Apps.CountAsync(a => a.EnvironmentId == copy.Id)).Should().Be(1);
        (await h.Db.ManagedServices.CountAsync(s => s.EnvironmentId == copy.Id)).Should().Be(1);
    }

    [Fact]
    public async Task The_copy_is_neither_the_default_nor_protected()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Environments.FirstAsync(e => e.Id == outcome.EnvironmentId);

        copy.IsDefault.Should().BeFalse();
        copy.IsProtected.Should().BeFalse(
            "a copy must not inherit either of the flags that say this one is the real one");
    }

    [Fact]
    public async Task Every_database_in_the_copy_gets_a_new_password()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.ManagedServices.FirstAsync(s => s.EnvironmentId == outcome.EnvironmentId);

        copy.EncryptedPassword.Should().NotBe(h.SourceService.EncryptedPassword,
            "a copied password lets the copy reach the original's database with the original's " +
            "credentials, and nothing about that failure is visible");
    }

    [Fact]
    public async Task A_copied_database_reports_no_measured_size_and_no_running_image()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.ManagedServices.FirstAsync(s => s.EnvironmentId == outcome.EnvironmentId);

        copy.StorageBytes.Should().BeNull("that figure was measured on the original's volume");
        copy.StorageMeasuredAt.Should().BeNull();
        copy.RunningImage.Should().BeNull("it has never run");
        copy.Status.Should().Be(ServiceStatus.Provisioning);
    }

    [Fact]
    public async Task Every_copied_database_is_queued_for_provisioning_after_its_row_exists()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.ManagedServices.FirstAsync(s => s.EnvironmentId == outcome.EnvironmentId);

        h.Engine.Provisioned.Should().Equal([copy.Id]);
    }

    // ---- what does not come across ----

    [Fact]
    public async Task The_copy_gets_an_empty_volume_at_the_same_mount()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.Include(a => a.Volumes)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        var volume = copy.Volumes.Should().ContainSingle().Subject;
        volume.MountPath.Should().Be("/data");
        volume.Name.Should().NotBe("harbora-vol-api-data",
            "one name is one docker volume — the copy would be writing into the original's data");
        volume.StorageBytes.Should().BeNull("nothing has been written to it, let alone measured");
    }

    /// <summary>
    /// With no platform root domain configured — the fixture's default — <c>AssignAsync</c> has
    /// nothing to derive a name from and hands back <c>NoRootDomain</c>, so the copy stays addressless.
    ///
    /// <para>
    /// That used to be the only reason this assertion could pass, which is exactly the problem: a
    /// fixture that never configures a root domain can't tell "the copy got no address because none
    /// was configured" apart from "the copy got no address because cloning doesn't give addresses at
    /// all", the wrong belief this file used to encode. <see cref="A_clone_is_given_its_own_address_not_the_originals"/>
    /// is the test that a root domain being configured actually changes what a copy gets.
    /// </para>
    /// </summary>
    [Fact]
    public async Task With_no_root_domain_configured_the_copy_is_left_addressless()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.Include(a => a.Domains)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        copy.Domains.Should().BeEmpty(
            "the fixture sets no platform root domain, so AssignAsync has nothing to derive a name from");
        outcome.Plan!.DomainsLeftBehind.Should().Be(1,
            "and the screen has to say the original's own domain was not carried over");
    }

    /// <summary>
    /// The real coverage finding 4 asked for: with a root domain configured, a copy is given an
    /// address of its own — never the original's, because a hostname points at one place and the copy
    /// answering on the original's own domain would be two apps racing for one route.
    /// </summary>
    [Fact]
    public async Task A_clone_is_given_its_own_address_not_the_originals()
    {
        var h = Build();
        h.Db.Settings.Add(new Harbora.Domain.Settings.Setting
        { Key = Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, Value = "apps.example.com" });
        await h.Db.SaveChangesAsync();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.Include(a => a.Domains)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        var domain = copy.Domains.Should().ContainSingle(
            "a copy is a real app that answers on its own hostname now, the same as any other creation " +
            "path — before this it was created with no address at all").Subject;
        domain.Host.Should().Be("api-staging.apps.example.com");
        domain.Host.Should().NotBe(h.SourceApp.Domains.Single().Host,
            "a hostname points at one place — the copy answering on the original's own domain would " +
            "mean two apps racing for one route");
        domain.IsPrimary.Should().BeTrue();
        domain.SslEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_copy_does_not_inherit_the_originals_history_or_its_preview_habit()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        copy.ActiveDeploymentId.Should().BeNull();
        copy.PreviewsEnabled.Should().BeFalse("a copy that spawns environments of its own is a bill");
        copy.PublishedHostPort.Should().BeNull("that port is taken on the original's node");
        copy.Status.Should().Be(AppStatus.Created);
    }

    // ---- the variables, which is where the silent failure lives ----

    [Fact]
    public async Task The_applications_own_configuration_comes_across()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.Include(a => a.EnvironmentVariables)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        copy.EnvironmentVariables.Should().Contain(v => v.Key == "APP_SECRET",
            "carrying the application's configuration is the point of copying it");
    }

    [Fact]
    public async Task No_connection_variable_still_points_at_the_original()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copy = await h.Db.Apps.Include(a => a.EnvironmentVariables)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        copy.EnvironmentVariables
            .Where(v => v.Key.EndsWith("DATABASE_URL") || v.Key.EndsWith("PGHOST"))
            .Should().NotBeEmpty()
            .And.OnlyContain(v => !v.Value.Contains("harbora-svc-main-db/")
                                  && !v.Value.Equals("harbora-svc-main-db", StringComparison.Ordinal),
                "this is the failure the whole feature would otherwise ship with: the copy comes " +
                "up, connects to production, and looks like it worked");
    }

    [Fact]
    public async Task The_rewritten_variables_name_the_copys_own_database()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);
        var copyService = await h.Db.ManagedServices
            .FirstAsync(s => s.EnvironmentId == outcome.EnvironmentId);
        var copy = await h.Db.Apps.Include(a => a.EnvironmentVariables)
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);

        copy.EnvironmentVariables.Should().Contain(v =>
            v.Key == "MAIN_DB_PGHOST" && v.Value.Contains(copyService.ContainerName));
    }

    [Fact]
    public async Task Nothing_is_created_when_the_originals_connection_settings_cannot_be_read()
    {
        var h = Build();
        h.Engine.Throws = true;

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        (await h.Db.Environments.CountAsync()).Should().Be(1,
            "carrying every variable over blind would hand the copy the original's credentials");
        (await h.Db.Apps.CountAsync()).Should().Be(1);
        h.Engine.Provisioned.Should().BeEmpty();
    }

    // ---- refusals ----

    /// <summary>
    /// The real path finding 5 asked for: <c>QuotaRefusalAsync</c>'s own <c>Domains:</c> term, exercised
    /// through a real <c>CloneAsync</c> call rather than re-implemented as a standalone expression that
    /// could not fail if that term were ever deleted from production.
    /// </summary>
    [Fact]
    public async Task The_domain_the_copy_will_consume_is_weighed_against_the_plan_too()
    {
        var h = Build();
        h.Db.Settings.Add(new Harbora.Domain.Settings.Setting
        { Key = Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, Value = "apps.example.com" });
        await h.Db.SaveChangesAsync();
        // Already at the domain ceiling; the copy's one web app needs one more.
        h.Quota.Usage = new WorkspaceUsage("Small", Apps: 1, MaxApps: 9, Services: 1, MaxServices: 9,
            MemoryUsedBytes: 0, MaxMemoryBytes: 0, CpuUsed: 0, MaxCpuCores: 0, Suspended: false,
            Domains: 1, MaxDomains: 1);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse(
            "the plan's one web app needs one more domain, and the workspace is already at its limit");
        outcome.Reason.Should().Contain("domain");
        (await h.Db.Environments.CountAsync()).Should().Be(1, "half a copy is worse than none");
    }

    [Fact]
    public async Task The_whole_package_is_weighed_against_the_plan_at_once()
    {
        var h = Build();
        // Room for one more of each singly; the copy needs one of each and there is one of each.
        h.Quota.Usage = new WorkspaceUsage("Small", Apps: 1, MaxApps: 1, Services: 1, MaxServices: 2,
            MemoryUsedBytes: 0, MaxMemoryBytes: 0, CpuUsed: 0, MaxCpuCores: 0, Suspended: false);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        outcome.Reason.Should().Contain("1",
            "the refusal has to carry the numbers, or nobody knows what to change");
        (await h.Db.Environments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task The_databases_are_weighed_too_when_the_applications_fit()
    {
        var h = Build();
        h.Quota.Usage = new WorkspaceUsage("Small", Apps: 1, MaxApps: 9, Services: 1, MaxServices: 1,
            MemoryUsedBytes: 0, MaxMemoryBytes: 0, CpuUsed: 0, MaxCpuCores: 0, Suspended: false);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse("there is room for the applications but not for the database");
        outcome.Reason.Should().Contain("databases");
        (await h.Db.Environments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_refusal_from_the_ordinary_quota_check_is_passed_through_verbatim()
    {
        var h = Build();
        h.Quota.Single = QuotaCheck.Deny("This workspace is suspended.");

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        outcome.Reason.Should().Be("This workspace is suspended.");
    }

    [Fact]
    public async Task Nothing_is_written_when_no_node_has_room()
    {
        var h = Build();
        h.Scheduler.Result = PlacementResult.Fail("No server has capacity.");

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        outcome.Reason.Should().Be("No server has capacity.");
        (await h.Db.Environments.CountAsync()).Should().Be(1,
            "half a copy is worse than none");
        (await h.Db.ManagedServices.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task An_application_with_nowhere_to_run_stops_the_copy_on_its_own()
    {
        var h = Build();
        h.Db.ManagedServices.RemoveRange(h.Db.ManagedServices);
        await h.Db.SaveChangesAsync();
        h.Scheduler.Result = PlacementResult.Fail("No server has capacity.");

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        (await h.Db.Environments.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task A_database_with_nowhere_to_run_stops_the_copy_on_its_own()
    {
        var h = Build();
        h.Db.Apps.RemoveRange(h.Db.Apps);
        await h.Db.SaveChangesAsync();
        h.Scheduler.Result = PlacementResult.Fail("No server has capacity.");

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        (await h.Db.Environments.CountAsync()).Should().Be(1);
        h.Engine.Provisioned.Should().BeEmpty();
    }

    [Fact]
    public async Task Another_workspaces_environment_is_not_copyable()
    {
        var h = Build();

        var outcome = await h.Cloner.CloneAsync(Guid.CreateVersion7(), h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        (await h.Db.Environments.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// App slugs are unique across the whole platform (HarboraDbContext:
    /// <c>HasIndex(x => x.Slug).IsUnique()</c>) — 2026-08-15-unique-app-names-design. Before that,
    /// <c>EnvironmentCloner.PlanAsync</c> only read this workspace's own apps into the taken-slug set
    /// it hands to <c>ClonePlan.Of</c>, so a slug another workspace already held was invisible and the
    /// clone would try to insert a duplicate and fail the unique index instead of landing on "-2" the
    /// way <c>ClonePlan.Of</c>'s own <c>Unique()</c> already knows how to do.
    /// </summary>
    [Fact]
    public async Task A_clones_app_slug_dodges_one_another_workspace_already_holds()
    {
        var h = Build();
        // A completely unrelated workspace's app already sits at the exact slug this clone would
        // otherwise land on ("api" + "-staging").
        h.Db.Apps.Add(new App
        {
            WorkspaceId = Guid.CreateVersion7(), ServerId = Server, Name = "Somebody else's api",
            Slug = "api-staging", SourceType = AppSourceType.PrebuiltImage, PrebuiltImage = "nginx:1",
            ContainerPort = 80, Status = AppStatus.Running
        });
        await h.Db.SaveChangesAsync();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeTrue(outcome.Reason);
        var copy = await h.Db.Apps.FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId);
        copy.Slug.Should().Be("api-staging-2",
            "\"api-staging\" is already taken by a different workspace's app, and dodging it only " +
            "works if EnvironmentCloner hands ClonePlan.Of a platform-wide taken-slug set");
    }

    [Fact]
    public async Task An_empty_environment_is_refused_rather_than_copied_into_nothing()
    {
        var h = Build();
        h.Db.Apps.RemoveRange(h.Db.Apps);
        h.Db.ManagedServices.RemoveRange(h.Db.ManagedServices);
        await h.Db.SaveChangesAsync();

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeFalse();
        (await h.Db.Environments.CountAsync()).Should().Be(1);
    }

    // ---- function apps ----

    /// <summary>
    /// Adds a function app to the source environment. Separate from <c>Build</c> so every existing
    /// assertion about "one app, one service" keeps meaning what it meant.
    /// </summary>
    private static App AddFunctionApp(Harness h)
    {
        var app = new App
        {
            WorkspaceId = Workspace, EnvironmentId = h.Source.Id, ServerId = Server,
            Name = "hooks", Slug = "hooks", SourceType = AppSourceType.InlineCode,
            FunctionRuntime = Harbora.Domain.Functions.FunctionRuntime.Python,
            FunctionInvokeSecret = "the-original-secret",
            ContainerPort = 8080, Status = AppStatus.Running
        };
        h.Db.Apps.Add(app);
        h.Db.FunctionDefinitions.Add(new Harbora.Domain.Functions.FunctionDefinition
        {
            AppId = app.Id, WorkspaceId = Workspace, Name = "nightly", Slug = "nightly",
            Trigger = Harbora.Domain.Functions.FunctionTrigger.Cron, CronExpression = "0 3 * * *",
            Code = "def run(req, ctx):\n    return 'ok'", IsEnabled = true,
            HasUnpublishedChanges = false, NextRunAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        h.Db.SaveChanges();
        return app;
    }

    [Fact]
    public async Task A_cloned_function_app_brings_its_functions_with_it()
    {
        // Copying the shell and leaving the functions behind produced an app that looked like a
        // function app on every page and answered 404 to everything the original answered.
        var h = Build();
        AddFunctionApp(h);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        outcome.Ok.Should().BeTrue(outcome.Reason);
        var copy = await h.Db.Apps.IgnoreQueryFilters()
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId && a.SourceType == AppSourceType.InlineCode);

        var functions = await h.Db.FunctionDefinitions.IgnoreQueryFilters()
            .Where(f => f.AppId == copy.Id).ToListAsync();

        functions.Should().ContainSingle().Which.Slug.Should().Be("nightly");
        functions[0].Code.Should().Contain("return 'ok'");
    }

    [Fact]
    public async Task A_cloned_function_app_keeps_its_runtime()
    {
        // Without it the copy is SourceType.InlineCode with no runtime — an app the pipeline has
        // nothing to generate from, so it can never be published.
        var h = Build();
        AddFunctionApp(h);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        var copy = await h.Db.Apps.IgnoreQueryFilters()
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId && a.SourceType == AppSourceType.InlineCode);

        copy.FunctionRuntime.Should().Be(Harbora.Domain.Functions.FunctionRuntime.Python);
    }

    [Fact]
    public async Task A_cloned_function_app_gets_an_invoke_secret_of_its_own()
    {
        // The same rule as a cloned database's password: a staging copy holding production's secret
        // is a staging copy that can fire production's schedules.
        var h = Build();
        var origin = AddFunctionApp(h);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        var copy = await h.Db.Apps.IgnoreQueryFilters()
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId && a.SourceType == AppSourceType.InlineCode);

        copy.FunctionInvokeSecret.Should().NotBeNullOrEmpty();
        copy.FunctionInvokeSecret.Should().NotBe(origin.FunctionInvokeSecret);
    }

    [Fact]
    public async Task A_cloned_function_starts_unpublished_and_unscheduled()
    {
        // Nothing has been built for the copy, and a due time carried over belongs to the original's
        // history — it would fire the copy before its first publish.
        var h = Build();
        AddFunctionApp(h);

        var outcome = await h.Cloner.CloneAsync(Workspace, h.Source.Id, "Staging", default);

        var copy = await h.Db.Apps.IgnoreQueryFilters()
            .FirstAsync(a => a.EnvironmentId == outcome.EnvironmentId && a.SourceType == AppSourceType.InlineCode);
        var fn = await h.Db.FunctionDefinitions.IgnoreQueryFilters().FirstAsync(f => f.AppId == copy.Id);

        fn.HasUnpublishedChanges.Should().BeTrue();
        fn.NextRunAt.Should().BeNull();
    }
}
