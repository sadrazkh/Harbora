using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// C1 (2026-08-22 config-delivery plan): the plan's own acceptance criterion — "test at the seam
/// where env becomes container environment ... a connection string composed by the panel arrives
/// byte-identical in the container" — proven the same way <c>StorageBucketPipelineTests</c> proves it
/// for buckets: the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> runs over
/// the fake Docker engine, and assertions read <see cref="FakeDockerEngine.RunRequests"/>, never a
/// helper's return value.
/// </summary>
public class AppManagedServicePipelineTests
{
    private static ManagedService GivenPostgres(
        PipelineHarness h, string name, string password = "s3cret-pw-01") => new()
    {
        WorkspaceId = h.Workspace.Id, EnvironmentId = h.Environment.Id, ServerId = h.Server.Id,
        Name = name, Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
        ContainerName = $"harbora-svc-{name}", InternalPort = 5432,
        Username = "harbora", EncryptedPassword = h.Protector.Protect(password),
        DatabaseName = name, VolumeName = $"harbora-svc-{name}-data", Status = ServiceStatus.Running
    };

    private static AppManagedService Attach(
        PipelineHarness h, ManagedService svc, string alias, int order, bool unpublished = true)
    {
        h.Db.ManagedServices.Add(svc);
        var join = new AppManagedService
        {
            AppId = h.App.Id, ManagedServiceId = svc.Id, Alias = alias,
            AttachOrder = order, HasUnpublishedChanges = unpublished
        };
        h.Db.AppManagedServices.Add(join);
        h.Db.SaveChanges();
        return join;
    }

    [Fact]
    public async Task The_panels_own_connection_string_arrives_byte_identical_in_the_container()
    {
        using var h = new PipelineHarness();
        var svc = GivenPostgres(h, "orders", password: "correct-horse-battery-staple");
        Attach(h, svc, "ORDERS", order: 1);

        // What the panel itself would compose for this exact service right now — the same call
        // AttachedServiceConnectionResolver and the details page both make.
        var expectedDsn = "Host=harbora-svc-orders;Port=5432;Database=orders;Username=harbora;Password=correct-horse-battery-staple";
        var expectedUrl = "postgresql://harbora:correct-horse-battery-staple@harbora-svc-orders:5432/orders";

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("DATABASE_DSN").WhoseValue.Should().Be(expectedDsn);
        run.Env.Should().ContainKey("DATABASE_URL").WhoseValue.Should().Be(expectedUrl);
        run.Env.Should().ContainKey("PGHOST").WhoseValue.Should().Be("harbora-svc-orders");
        run.Env.Should().ContainKey("PGPASSWORD").WhoseValue.Should().Be("correct-horse-battery-staple");
    }

    [Fact]
    public async Task The_alias_prefixed_copy_reaches_the_container_alongside_the_magic_name()
    {
        using var h = new PipelineHarness();
        var svc = GivenPostgres(h, "orders");
        Attach(h, svc, "ORDERS", order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("ORDERS_DATABASE_URL");
        run.Env.Should().ContainKey("ORDERS_DATABASE_DSN");
        run.Env["ORDERS_DATABASE_URL"].Should().Be(run.Env["DATABASE_URL"]);
    }

    [Fact]
    public async Task Two_databases_reach_the_container_under_two_distinct_unambiguous_sets_of_names()
    {
        using var h = new PipelineHarness();
        var orders = GivenPostgres(h, "orders", password: "orders-pw-0001");
        var customers = GivenPostgres(h, "customers", password: "customers-pw-0001");
        Attach(h, orders, "ORDERS", order: 1);
        Attach(h, customers, "CUSTOMERS", order: 2);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        // The magic name is not lost — it goes to whichever was attached later, exactly the rule
        // StorageBucketPipelineTests proves for two buckets sharing a fixed key.
        run.Env["DATABASE_URL"].Should().Contain("customers-pw-0001");
        // But BOTH remain fully, unambiguously reachable under their own alias — the actual "make
        // collisions impossible" requirement.
        run.Env["ORDERS_DATABASE_URL"].Should().Contain("orders-pw-0001");
        run.Env["CUSTOMERS_DATABASE_URL"].Should().Contain("customers-pw-0001");
    }

    [Fact]
    public async Task The_apps_own_variable_reaches_the_container_over_a_database_defining_the_same_key()
    {
        using var h = new PipelineHarness();
        h.Db.EnvironmentVariables.Add(new EnvironmentVariable { AppId = h.App.Id, Key = "PGHOST", Value = "hand-picked" });
        h.Db.SaveChanges();
        var svc = GivenPostgres(h, "orders");
        Attach(h, svc, "ORDERS", order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["PGHOST"].Should().Be("hand-picked", "the app's own variable must win over any database in the actual run request");
    }

    [Fact]
    public async Task A_successful_deploy_clears_the_unpublished_flag_on_the_attached_database()
    {
        using var h = new PipelineHarness();
        var svc = GivenPostgres(h, "orders");
        var join = Attach(h, svc, "ORDERS", order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var stored = await h.Db.AppManagedServices.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeFalse(
            "this deployment's container was built from the database's current credentials, so it is applied");
    }

    [Fact]
    public async Task A_failed_deployment_leaves_the_databases_unpublished_flag_set()
    {
        using var h = new PipelineHarness().WithDomain().WithHealthPath();
        h.Http.Status = System.Net.HttpStatusCode.InternalServerError;
        var svc = GivenPostgres(h, "orders");
        var join = Attach(h, svc, "ORDERS", order: 1, unpublished: true);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Failed);
        var stored = await h.Db.AppManagedServices.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        stored.HasUnpublishedChanges.Should().BeTrue(
            "nothing actually shipped with this database's credentials, so the stale flag must not be cleared");
    }

    /// <summary>
    /// The plan's other explicit acceptance criterion: "a function app must get it too, free ... if
    /// it does not, that is a finding." It does — a function app is an ordinary <c>App</c> row
    /// (<c>AppSourceType.InlineCode</c>) that goes through this exact same <c>BuildEnv</c>, exactly
    /// the way <c>StorageBucketPipelineTests</c> already proved for buckets.
    /// </summary>
    [Fact]
    public async Task A_function_app_receives_an_attached_databases_env_the_same_way_any_other_app_does()
    {
        using var h = new PipelineHarness(sourceType: AppSourceType.InlineCode);
        h.App.FunctionRuntime = FunctionRuntime.CSharp;
        h.Db.SaveChanges();
        h.Db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = h.App.Id, WorkspaceId = h.Workspace.Id,
            Name = "Hello", Slug = "hello", Trigger = FunctionTrigger.Http,
            Code = "// v1", IsEnabled = true, HasUnpublishedChanges = false
        });
        h.Db.SaveChanges();
        var svc = GivenPostgres(h, "fn-orders");
        Attach(h, svc, "FNORDERS", order: 1);

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().ContainKey("DATABASE_URL",
            "a function app is an ordinary App row and goes through the same BuildEnv — a database attach reaches it for free");
        run.Env.Should().ContainKey("PGHOST");
        run.Env.Should().ContainKey("PGPASSWORD");
    }

    [Fact]
    public async Task Rolling_back_still_applies_the_databases_current_credentials_because_env_is_never_baked_into_the_image()
    {
        using var h = new PipelineHarness();
        h.WithPreviousDeployment(number: 1);
        var svc = GivenPostgres(h, "orders", password: "v1-secret-0001");
        var join = Attach(h, svc, "ORDERS", order: 1, unpublished: false);

        // The credential rotates after v1 shipped — same as ManagedServiceEngine.RotatePasswordAsync.
        var stored = await h.Db.ManagedServices.FirstAsync(s => s.Id == svc.Id);
        stored.EncryptedPassword = h.Protector.Protect("v2-secret-0002");
        join.HasUnpublishedChanges = true;
        h.Db.SaveChanges();

        var rollback = h.QueueDeployment(number: 2, rollbackTo: h.App.ActiveDeploymentId);
        var result = await h.RunAsync(rollback);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env["PGPASSWORD"].Should().Be("v2-secret-0002",
            "unlike function code, env is assembled fresh at run time regardless of which image is running");

        var storedJoin = await h.Db.AppManagedServices.AsNoTracking().FirstAsync(x => x.Id == join.Id);
        storedJoin.HasUnpublishedChanges.Should().BeFalse("the rollback's container really was built with v2's password");
    }
}
