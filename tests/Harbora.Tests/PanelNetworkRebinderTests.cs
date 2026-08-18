using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Networking;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Defect 2 of the 2026-08-18 functions design: the panel's route to a locally-deployed app is an
/// imperative <c>docker network connect</c> made once, at deploy time
/// (<c>DeploymentPipeline.cs:375-379</c>) — nothing in <c>deploy/docker-compose.yml</c> declares it,
/// because a tenant's network is created dynamically and compose cannot know its name up front. The
/// documented upgrade rebuilds and therefore recreates the panel container, which drops every
/// membership that was never written down, and the only caller who notices between deploys is
/// <c>FunctionInvoker.ResolveAddressAsync</c> — cron and event invocations record "Could not reach the
/// function app." for ever, on an app that never actually stopped.
///
/// <para>
/// <see cref="PanelNetworkRebinder"/> is the owner's chosen fix (§8 Q1a): re-attach on every boot,
/// self-healing, using the exact two calls the pipeline already makes at deploy time. These tests
/// assert on what was actually written to the fake Docker engine's attachment log — not on the method
/// returning without throwing — because a rebinder that silently does nothing looks identical to one
/// that worked, right up until the next restart.
/// </para>
/// </summary>
public class PanelNetworkRebinderTests
{
    private static (ServiceProvider Services, FakeDockerEngine Docker) BuildProvider(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        var docker = new FakeDockerEngine();
        services.AddSingleton<IServerEngineFactory>(new FakeServerEngineFactory(docker));
        return (services.BuildServiceProvider(), docker);
    }

    private static PanelNetworkRebinder BuildRebinder(ServiceProvider sp, HarboraRuntimeOptions? options = null) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new HarboraRuntimeOptions()),
            NullLogger<PanelNetworkRebinder>.Instance);

    private sealed record Placement(Guid ServerId, Guid EnvironmentId, string Network);

    /// <summary>Seeds one deployed app on a local server, in its own project/environment, and returns
    /// the network <see cref="EnvironmentNetworkResolver"/> would compute for it.</summary>
    private static async Task<Placement> GivenDeployedLocalAppAsync(
        ServiceProvider sp, string slug, bool local = true, bool active = true)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var server = new Server { Id = Guid.NewGuid(), Name = slug + "-server", Hostname = slug, IsLocal = local };
        var workspaceId = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, ProjectId = projectId,
            Name = slug, Slug = slug, IsDefault = true
        };
        db.Servers.Add(server);
        db.Projects.Add(new Harbora.Domain.Projects.Project
        {
            Id = projectId, WorkspaceId = workspaceId, Name = slug, Slug = slug
        });
        db.Environments.Add(environment);
        db.Apps.Add(new App
        {
            WorkspaceId = workspaceId, EnvironmentId = environment.Id, ServerId = server.Id,
            Name = slug, Slug = slug, SourceType = AppSourceType.PrebuiltImage,
            PrebuiltImage = "nginx:1.27",
            ActiveDeploymentId = active ? Guid.NewGuid() : null
        });
        await db.SaveChangesAsync();

        var network = await EnvironmentNetworkResolver.ForAsync(db, environment.Id, default);
        return new Placement(server.Id, environment.Id, network);
    }

    [Fact]
    public async Task Booting_reattaches_the_panel_and_the_proxy_to_a_locally_deployed_apps_network()
    {
        var (sp, docker) = BuildProvider("panel-rebind-" + Guid.NewGuid());
        var placement = await GivenDeployedLocalAppAsync(sp, "blog");
        var options = new HarboraRuntimeOptions { PanelContainerName = "harbora-panel", ProxyContainerName = "harbora-traefik" };

        await BuildRebinder(sp, options).StartAsync(default);

        docker.ConnectedNetworks("harbora-panel").Should().Contain(placement.Network,
            "the panel must regain exactly the membership DeploymentPipeline grants it at deploy time");
        docker.ConnectedNetworks("harbora-traefik").Should().Contain(placement.Network,
            "the proxy shares the same imperative attach, so it needs the same repair");
    }

    [Fact]
    public async Task An_app_on_a_remote_server_is_never_joined_to_a_docker_network_here()
    {
        var (sp, docker) = BuildProvider("panel-rebind-" + Guid.NewGuid());
        await GivenDeployedLocalAppAsync(sp, "remote-app", local: false);

        await BuildRebinder(sp).StartAsync(default);

        // A remote node's apps are reached over a published host port
        // (FunctionInvoker.ResolveAddressAsync), never by container name on this machine's Docker —
        // so nothing about a remote placement should ever touch the local engine's network calls.
        docker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_app_that_has_never_been_deployed_grants_no_network_membership()
    {
        var (sp, docker) = BuildProvider("panel-rebind-" + Guid.NewGuid());
        await GivenDeployedLocalAppAsync(sp, "never-deployed", active: false);

        await BuildRebinder(sp).StartAsync(default);

        // No ActiveDeploymentId means no running container to reach — connecting the panel to that
        // network yet would just be work with nothing behind it.
        docker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Two_apps_on_the_same_environment_only_rebind_that_network_once()
    {
        var (sp, docker) = BuildProvider("panel-rebind-" + Guid.NewGuid());

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var server = new Server { Id = Guid.NewGuid(), Name = "s", Hostname = "s", IsLocal = true };
            var workspaceId = Guid.NewGuid();
            var projectId = Guid.NewGuid();
            var environment = new Harbora.Domain.Projects.Environment
            {
                Id = Guid.NewGuid(), WorkspaceId = workspaceId, ProjectId = projectId,
                Name = "shared", Slug = "shared", IsDefault = true
            };
            db.Servers.Add(server);
            db.Projects.Add(new Harbora.Domain.Projects.Project
            {
                Id = projectId, WorkspaceId = workspaceId, Name = "shared", Slug = "shared"
            });
            db.Environments.Add(environment);
            db.Apps.AddRange(
                new App
                {
                    WorkspaceId = workspaceId, EnvironmentId = environment.Id, ServerId = server.Id,
                    Name = "one", Slug = "one", SourceType = AppSourceType.PrebuiltImage,
                    PrebuiltImage = "nginx:1.27", ActiveDeploymentId = Guid.NewGuid()
                },
                new App
                {
                    WorkspaceId = workspaceId, EnvironmentId = environment.Id, ServerId = server.Id,
                    Name = "two", Slug = "two", SourceType = AppSourceType.PrebuiltImage,
                    PrebuiltImage = "nginx:1.27", ActiveDeploymentId = Guid.NewGuid()
                });
            await db.SaveChangesAsync();
        }

        var options = new HarboraRuntimeOptions { PanelContainerName = "harbora-panel" };
        await BuildRebinder(sp, options).StartAsync(default);

        // Cost stated in the design (§8 Q1a): "a boot-time loop over networks, which grows with the
        // fleet" — grows with the number of NETWORKS, not the number of apps on them.
        docker.CountOf(nameof(IDockerEngine.ConnectNetworkAsync))
            .Should().Be(2, "one call for the panel and one for the proxy — not one pair per app");
    }

    [Fact]
    public async Task A_failure_rebinding_one_environment_does_not_stop_the_rest()
    {
        var (sp, docker) = BuildProvider("panel-rebind-" + Guid.NewGuid());
        var good = await GivenDeployedLocalAppAsync(sp, "good");

        // A second app whose EnvironmentId points at a row that no longer exists — the shape a
        // corrupted or half-migrated install would leave, and something EnvironmentNetworkResolver
        // throws over rather than guessing at a network name.
        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var server = new Server { Id = Guid.NewGuid(), Name = "s2", Hostname = "s2", IsLocal = true };
            db.Servers.Add(server);
            db.Apps.Add(new App
            {
                WorkspaceId = Guid.NewGuid(), EnvironmentId = Guid.NewGuid(), ServerId = server.Id,
                Name = "orphan", Slug = "orphan", SourceType = AppSourceType.PrebuiltImage,
                PrebuiltImage = "nginx:1.27", ActiveDeploymentId = Guid.NewGuid()
            });
            await db.SaveChangesAsync();
        }

        var options = new HarboraRuntimeOptions { PanelContainerName = "harbora-panel" };
        var act = async () => await BuildRebinder(sp, options).StartAsync(default);

        // Never fatal: a panel that cannot rebind is a panel with some apps unreachable between
        // deploys; a panel that refuses to start over it is a panel with everything down.
        await act.Should().NotThrowAsync();

        docker.ConnectedNetworks("harbora-panel").Should().Contain(good.Network,
            "the orphaned app's broken environment must not stop a good one from being rebound");
    }
}
