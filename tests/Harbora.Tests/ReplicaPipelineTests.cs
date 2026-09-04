using FluentAssertions;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Tests.Fakes;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// 3.2 (round-2 market-gaps plan): the plan's own acceptance criterion — "a replica's connection
/// string reaches an attached app at the container-environment seam and the primary's still does
/// too" — proven the same way <see cref="AppManagedServicePipelineTests"/> proves it for an ordinary
/// database attach: the real <see cref="Harbora.Infrastructure.Deployments.DeploymentPipeline"/> runs
/// over the fake Docker engine, and assertions read <see cref="FakeDockerEngine.RunRequests"/>, never
/// a helper's return value.
/// </summary>
public class ReplicaPipelineTests
{
    private static ManagedService GivenPostgres(
        PipelineHarness h, string name, string password = "s3cret-pw-01", Guid? primaryId = null) => new()
    {
        WorkspaceId = h.Workspace.Id, EnvironmentId = h.Environment.Id, ServerId = h.Server.Id,
        Name = name, Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
        ContainerName = $"harbora-svc-{name}", InternalPort = 5432,
        Username = "harbora", EncryptedPassword = h.Protector.Protect(password),
        DatabaseName = name, VolumeName = $"harbora-svc-{name}-data", Status = ServiceStatus.Running,
        PrimaryManagedServiceId = primaryId
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
    public async Task A_running_replicas_url_reaches_the_container_and_the_primarys_own_url_still_does_too()
    {
        using var h = new PipelineHarness();
        var primary = GivenPostgres(h, "orders", password: "correct-horse-battery-staple");
        Attach(h, primary, "ORDERS", order: 1);
        var replica = GivenPostgres(h, "orders-standby", primaryId: primary.Id);
        h.Db.ManagedServices.Add(replica);
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        var result = await h.RunAsync(deployment);

        result.Status.Should().Be(DeploymentStatus.Succeeded);
        var run = h.Docker.RunRequests.Should().ContainSingle().Which;

        // The primary's own connection is exactly what it always was — a replica riding along must
        // never displace it.
        run.Env.Should().ContainKey("DATABASE_URL").WhoseValue.Should().Contain("harbora-svc-orders:")
            .And.Contain("correct-horse-battery-staple");
        run.Env.Should().ContainKey("ORDERS_DATABASE_URL");

        // The replica's own connection — unmistakably read-only in its variable name, never
        // DATABASE_URL a second time — reaches the SAME container, under both the magic and the
        // alias-prefixed name, pointed at the REPLICA's own container, not the primary's.
        run.Env.Should().ContainKey("REPLICA_URL");
        run.Env.Should().ContainKey("ORDERS_REPLICA_URL");
        run.Env["REPLICA_URL"].Should().Contain("harbora-svc-orders-standby:",
            "the replica's own connection string must point at the REPLICA's container, not the primary's");
        run.Env["REPLICA_URL"].Should().Contain("correct-horse-battery-staple",
            "a physical replica shares the primary's own login byte-for-byte");
        run.Env["ORDERS_REPLICA_URL"].Should().Be(run.Env["REPLICA_URL"]);
    }

    [Fact]
    public async Task A_replica_that_is_still_provisioning_never_reaches_the_container()
    {
        using var h = new PipelineHarness();
        var primary = GivenPostgres(h, "orders");
        Attach(h, primary, "ORDERS", order: 1);
        var replica = GivenPostgres(h, "orders-standby", primaryId: primary.Id);
        replica.Status = ServiceStatus.Provisioning; // still seeding — no data an app could safely read
        h.Db.ManagedServices.Add(replica);
        h.Db.SaveChanges();

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().NotContainKey("REPLICA_URL",
            "a replica that has not finished seeding has nothing an app could safely read yet");
        run.Env.Should().NotContainKey("ORDERS_REPLICA_URL");
    }

    [Fact]
    public async Task An_app_with_no_replica_gets_no_replica_url_at_all()
    {
        using var h = new PipelineHarness();
        var primary = GivenPostgres(h, "orders");
        Attach(h, primary, "ORDERS", order: 1);

        var deployment = h.QueueDeployment(number: 1);
        await h.RunAsync(deployment);

        var run = h.Docker.RunRequests.Should().ContainSingle().Which;
        run.Env.Should().NotContainKey("REPLICA_URL");
        run.Env.Should().ContainKey("DATABASE_URL");
    }
}
