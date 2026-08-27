using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Nodes;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Docker;
using Harbora.Infrastructure.Nodes;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Placing work on a v1 node: the Server projection that makes it visible to the scheduler, the
/// routing decision that keeps its workloads off the panel's own Docker, and the reference pinning
/// a node insists on.
/// </summary>
public sealed class NodeSchedulingTests : IDisposable
{
    private readonly HarboraDbContext _db;

    public NodeSchedulingTests()
    {
        _db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("sched-" + Guid.NewGuid()).Options);
    }

    public void Dispose() => _db.Dispose();

    // --- the Server projection ---

    [Fact]
    public async Task An_enrolled_node_becomes_a_scheduling_target()
    {
        var node = await AddNodeAsync();

        var serverId = await Link().SyncAsync(node.NodeId, CancellationToken.None);

        serverId.Should().NotBeNull();

        var server = await _db.Servers.IgnoreQueryFilters().SingleAsync();
        server.Name.Should().Be("web-01");
        server.CpuCores.Should().Be(8);
        server.TotalMemoryBytes.Should().Be(32L * 1024 * 1024 * 1024);
        server.Status.Should().Be(ServerStatus.Online);
    }

    /// <summary>
    /// The null endpoint is load-bearing: it is what tells the engine factory this is a v1 node, and
    /// a projection that filled it in would send the node's traffic to an HTTP agent that is not
    /// listening.
    /// </summary>
    [Fact]
    public async Task The_projected_server_has_no_inbound_agent_endpoint()
    {
        var node = await AddNodeAsync();
        await Link().SyncAsync(node.NodeId, CancellationToken.None);

        var server = await _db.Servers.IgnoreQueryFilters().SingleAsync();

        server.AgentEndpoint.Should().BeNull();
        server.AgentTokenHash.Should().BeNull();
        server.IsLocal.Should().BeFalse();
    }

    [Fact]
    public async Task Syncing_twice_refreshes_one_row_rather_than_adding_another()
    {
        var node = await AddNodeAsync();
        var link = Link();

        await link.SyncAsync(node.NodeId, CancellationToken.None);

        node.CpuCores = 16;
        await _db.SaveChangesAsync();

        await link.SyncAsync(node.NodeId, CancellationToken.None);

        var servers = await _db.Servers.IgnoreQueryFilters().ToListAsync();
        servers.Should().ContainSingle();
        servers[0].CpuCores.Should().Be(16);
    }

    [Theory]
    // Draining is Degraded, not Offline: the scheduler passes over anything that is not Online, and
    // Offline would additionally tell the panel the node is unreachable, which it is not.
    [InlineData(NodeStatus.Online, "healthy", false, false, ServerStatus.Online)]
    [InlineData(NodeStatus.Online, "degraded", false, false, ServerStatus.Degraded)]
    [InlineData(NodeStatus.Online, "healthy", true, false, ServerStatus.Degraded)]
    [InlineData(NodeStatus.Offline, "unknown", false, false, ServerStatus.Offline)]
    [InlineData(NodeStatus.Online, "healthy", false, true, ServerStatus.Offline)]
    public async Task Node_state_projects_onto_server_status(
        NodeStatus status, string health, bool draining, bool revoked, ServerStatus expected)
    {
        var node = await AddNodeAsync(n =>
        {
            n.Status = status;
            n.Health = health;
            n.Draining = draining;
            if (revoked) n.RevokedAt = DateTimeOffset.UtcNow;
        });

        await Link().SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Status.Should().Be(expected);
    }

    [Fact]
    public async Task Auto_registration_can_be_turned_off()
    {
        var node = await AddNodeAsync();

        var serverId = await Link(auto: false).SyncAsync(node.NodeId, CancellationToken.None);

        serverId.Should().BeNull();
        (await _db.Servers.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    /// <summary>An operator attaches by hand on an install that turned auto-registration off.</summary>
    [Fact]
    public async Task Attaching_by_hand_works_even_when_auto_registration_is_off()
    {
        var node = await AddNodeAsync();

        var serverId = await Link(auto: false).AttachAsync(node.NodeId, CancellationToken.None);

        serverId.Should().NotBeNull();
        (await _db.Nodes.IgnoreQueryFilters().SingleAsync()).ServerId.Should().Be(serverId);
    }

    [Fact]
    public async Task Detaching_is_refused_while_an_app_is_placed_on_the_node()
    {
        var node = await AddNodeAsync();
        var link = Link();
        var serverId = await link.SyncAsync(node.NodeId, CancellationToken.None);

        _db.Apps.Add(new App { Name = "shop", Slug = "shop", ServerId = serverId!.Value, WorkspaceId = Guid.NewGuid() });
        await _db.SaveChangesAsync();

        var result = await link.DetachAsync(node.NodeId, CancellationToken.None);

        result.Ok.Should().BeFalse();
        // Removing the Server row here would leave the app deployed with nothing able to reach it.
        (await _db.Servers.IgnoreQueryFilters().CountAsync()).Should().Be(1);
        (await _db.Nodes.IgnoreQueryFilters().SingleAsync()).ServerId.Should().Be(serverId);
    }

    /// <summary>
    /// Another workspace's app blocks a detach as firmly as this one's. The count has to ignore the
    /// tenant filter, or an admin would be told the node is empty and orphan somebody else's app.
    /// </summary>
    [Fact]
    public async Task Detaching_counts_apps_across_every_workspace()
    {
        var node = await AddNodeAsync();
        var link = Link();
        var serverId = await link.SyncAsync(node.NodeId, CancellationToken.None);

        _db.Apps.Add(new App { Name = "a", Slug = "a", ServerId = serverId!.Value, WorkspaceId = Guid.NewGuid() });
        _db.Apps.Add(new App { Name = "b", Slug = "b", ServerId = serverId.Value, WorkspaceId = Guid.NewGuid() });
        await _db.SaveChangesAsync();

        var result = await link.DetachAsync(node.NodeId, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Reason.Should().Contain("2");
    }

    [Fact]
    public async Task Detaching_an_empty_node_removes_the_server_and_leaves_the_node_enrolled()
    {
        var node = await AddNodeAsync();
        var link = Link();
        await link.SyncAsync(node.NodeId, CancellationToken.None);

        var result = await link.DetachAsync(node.NodeId, CancellationToken.None);

        result.Ok.Should().BeTrue();
        (await _db.Servers.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        var after = await _db.Nodes.IgnoreQueryFilters().SingleAsync();
        after.ServerId.Should().BeNull();
        after.RevokedAt.Should().BeNull();
    }

    // --- the upstream address ---

    [Fact]
    public async Task The_upstream_address_prefers_a_routable_v4_address()
    {
        var node = await AddNodeAsync(n =>
            n.IpAddressesJson = JsonSerializer.Serialize(new[] { "127.0.0.1", "10.0.3.7", "203.0.113.9" }));

        await Link().SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Hostname.Should().Be("203.0.113.9");
    }

    [Fact]
    public async Task A_private_address_is_used_when_there_is_nothing_public()
    {
        var node = await AddNodeAsync(n =>
            n.IpAddressesJson = JsonSerializer.Serialize(new[] { "127.0.0.1", "10.0.3.7" }));

        await Link().SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Hostname.Should().Be("10.0.3.7");
    }

    /// <summary>
    /// An operator who points a DNS name at the node keeps it. Overwriting it on the next heartbeat
    /// would silently move every route back to a raw address.
    /// </summary>
    [Fact]
    public async Task An_operator_set_hostname_survives_the_next_heartbeat()
    {
        var node = await AddNodeAsync(n => n.IpAddressesJson = JsonSerializer.Serialize(new[] { "203.0.113.9" }));
        var link = Link();
        await link.SyncAsync(node.NodeId, CancellationToken.None);

        var server = await _db.Servers.IgnoreQueryFilters().SingleAsync();
        server.Hostname = "node-1.example.com";
        await _db.SaveChangesAsync();

        await link.SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Hostname.Should().Be("node-1.example.com");
    }

    [Fact]
    public async Task A_node_derived_hostname_follows_the_node_when_its_address_changes()
    {
        var node = await AddNodeAsync(n => n.IpAddressesJson = JsonSerializer.Serialize(new[] { "203.0.113.9" }));
        var link = Link();
        await link.SyncAsync(node.NodeId, CancellationToken.None);

        node.IpAddressesJson = JsonSerializer.Serialize(new[] { "203.0.113.40" });
        await _db.SaveChangesAsync();

        await link.SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Hostname.Should().Be("203.0.113.40");
    }

    [Fact]
    public async Task A_node_that_reports_no_address_falls_back_to_its_name()
    {
        var node = await AddNodeAsync(n => n.IpAddressesJson = "not json at all");

        await Link().SyncAsync(node.NodeId, CancellationToken.None);

        (await _db.Servers.IgnoreQueryFilters().SingleAsync()).Hostname.Should().Be("web-01");
    }

    // --- engine routing ---

    /// <summary>
    /// The failure this whole ordering exists to prevent. A node-backed Server has no agent
    /// endpoint, and a factory that read that as "the local machine" would deploy a customer's app
    /// onto the panel's own Docker daemon.
    /// </summary>
    [Fact]
    public async Task A_node_backed_server_never_resolves_to_the_local_engine()
    {
        var node = await AddNodeAsync();
        var serverId = await Link().SyncAsync(node.NodeId, CancellationToken.None);

        var engine = await Factory().ResolveAsync(serverId!.Value, CancellationToken.None);

        engine.Should().BeOfType<NodeWorkloadEngine>();
    }

    [Fact]
    public async Task A_revoked_node_refuses_to_hand_out_an_engine_at_all()
    {
        var node = await AddNodeAsync();
        var serverId = await Link().SyncAsync(node.NodeId, CancellationToken.None);

        node.RevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        var resolve = async () => await Factory().ResolveAsync(serverId!.Value, CancellationToken.None);

        await resolve.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*revoked*");
    }

    [Fact]
    public async Task The_local_server_still_resolves_to_the_local_engine()
    {
        var server = new Server { IsLocal = true, Name = "panel" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();

        var local = new StubEngine();

        (await Factory(local).ResolveAsync(server.Id, CancellationToken.None)).Should().BeSameAs(local);
    }

    /// <summary>
    /// A remote server with neither an endpoint nor a node is a misconfiguration, and the old
    /// fallback answered it by running the workload here. Failing is the safe answer.
    /// </summary>
    [Fact]
    public async Task A_remote_server_with_no_endpoint_and_no_node_fails_loudly()
    {
        var server = new Server { IsLocal = false, Name = "orphan", AgentEndpoint = null };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync();

        var resolve = async () => await Factory().ResolveAsync(server.Id, CancellationToken.None);

        await resolve.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*orphan*");
    }

    // --- image pinning ---

    [Theory]
    [InlineData("nginx", "docker.io", "library/nginx", "latest")]
    [InlineData("nginx:1.27", "docker.io", "library/nginx", "1.27")]
    [InlineData("acme/app:v2", "docker.io", "acme/app", "v2")]
    [InlineData("ghcr.io/acme/app:v2", "ghcr.io", "acme/app", "v2")]
    [InlineData("registry.example.com:5000/team/app:dev", "registry.example.com:5000", "team/app", "dev")]
    [InlineData("localhost:5000/app", "localhost:5000", "app", "latest")]
    public void An_image_reference_splits_the_way_docker_splits_it(
        string reference, string registry, string repository, string tag)
    {
        var parsed = ImageDigestResolver.Parse(reference);

        parsed.Registry.Should().Be(registry);
        parsed.Repository.Should().Be(repository);
        parsed.Tag.Should().Be(tag);
    }

    [Fact]
    public void A_bearer_challenge_splits_into_realm_service_and_scope()
    {
        var parsed = ImageDigestResolver.ParseChallenge(
            "realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/nginx:pull\"");

        parsed["realm"].Should().Be("https://auth.docker.io/token");
        parsed["service"].Should().Be("registry.docker.io");
        parsed["scope"].Should().Be("repository:library/nginx:pull");
    }

    [Fact]
    public void A_malformed_challenge_yields_nothing_rather_than_throwing()
    {
        ImageDigestResolver.ParseChallenge(null).Should().BeEmpty();
        ImageDigestResolver.ParseChallenge("Basic").Should().BeEmpty();
    }

    // --- workload naming ---

    [Theory]
    [InlineData("harbora-shop-7", "harbora-shop-7")]
    [InlineData("Harbora-Shop-7", "harbora-shop-7")]
    [InlineData("-leading-and-trailing-", "leading-and-trailing")]
    [InlineData("has_underscores.and.dots", "has-underscores-and-dots")]
    [InlineData("", "workload")]
    [InlineData("---", "workload")]
    public void A_container_name_becomes_a_name_the_node_will_accept(string input, string expected) =>
        NodeWorkloadEngine.SanitiseName(input).Should().Be(expected);

    [Fact]
    public void A_very_long_name_is_cut_to_a_dns_label_without_a_trailing_dash()
    {
        var name = NodeWorkloadEngine.SanitiseName(new string('a', 62) + "-bbbb");

        name.Length.Should().BeLessThanOrEqualTo(63);
        name.Should().NotEndWith("-");
    }

    // --- helpers ---

    private async Task<Node> AddNodeAsync(Action<Node>? configure = null)
    {
        var node = new Node
        {
            NodeId = "n-" + Guid.NewGuid().ToString("n")[..8],
            Name = "web-01",
            Status = NodeStatus.Online,
            Health = "healthy",
            CpuCores = 8,
            TotalMemoryBytes = 32L * 1024 * 1024 * 1024,
            TotalDiskBytes = 500L * 1024 * 1024 * 1024,
            FreeDiskBytes = 300L * 1024 * 1024 * 1024,
            ContainerRuntimeVersion = "27.1.1",
            LastHeartbeatAt = DateTimeOffset.UtcNow,
        };

        configure?.Invoke(node);

        _db.Nodes.Add(node);
        await _db.SaveChangesAsync();

        return node;
    }

    private NodeServerLink Link(bool auto = true) =>
        new(_db,
            Options.Create(new NodeAgentControlPlaneOptions { AutoRegisterAsServer = auto }),
            NullLogger<NodeServerLink>.Instance);

    private ServerEngineFactory Factory(IDockerEngine? local = null) =>
        new(local ?? new StubEngine(),
            _db,
            new PassthroughProtector(),
            new StubHttpClientFactory(),
            null!,   // NodeCommandService: the engine holds it but resolution never calls it
            null!,   // ImageDigestResolver: same
            new NodeHostFacts(_db),
            NullLogger<NodeWorkloadEngine>.Instance,
            NullLogger<ServerEngineFactory>.Instance);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>Stands in for the local engine; resolution never calls through it.</summary>
    private sealed class StubEngine : IDockerEngine
    {
        public Task<string> BuildImageAsync(DockerBuildRequest r, IProgress<string> l, CancellationToken ct) => throw new NotSupportedException();
        public Task PullImageAsync(string image, IProgress<string> l, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<ImageInfo>> ListImagesAsync(string? p, CancellationToken ct) => Task.FromResult<IReadOnlyList<ImageInfo>>([]);
        public Task<bool> ImageExistsAsync(string imageRef, CancellationToken ct) => Task.FromResult(false);
        public Task RemoveImageAsync(string imageRef, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<int>> GetImagePortsAsync(string imageRef, CancellationToken ct) => Task.FromResult<IReadOnlyList<int>>([]);
        public Task<string> RunContainerAsync(DockerRunRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task StopContainerAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveContainerAsync(string id, bool force, CancellationToken ct) => Task.CompletedTask;
        public Task RestartContainerAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task StreamLogsAsync(string id, IProgress<string> sink, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetLogsAsync(string id, int tail, CancellationToken ct) => Task.FromResult("");
        public Task<IReadOnlyList<ContainerInfo>> ListContainersAsync(string? f, CancellationToken ct) => Task.FromResult<IReadOnlyList<ContainerInfo>>([]);
        public Task<ContainerStats?> GetStatsAsync(string id, CancellationToken ct) => Task.FromResult<ContainerStats?>(null);
        public Task<ContainerDetail?> InspectAsync(string id, CancellationToken ct) => Task.FromResult<ContainerDetail?>(null);
        public Task<ContainerLifecycle?> GetLifecycleAsync(string id, CancellationToken ct) => Task.FromResult<ContainerLifecycle?>(null);
        public Task EnsureNetworkAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task ConnectNetworkAsync(string container, string network, CancellationToken ct) => Task.CompletedTask;
        public Task EnsureVolumeAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveVolumeAsync(string name, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<VolumeInfo>> ListVolumesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<VolumeInfo>>([]);
        public Task<int> RunOneOffAsync(DockerOneOffRequest r, IProgress<string>? l, CancellationToken ct) => Task.FromResult(0);
        /// <summary>A fake offers no shell — a test that reaches here meant something else.</summary>
        public Task<IContainerExec> ExecAsync(
            string containerId, IReadOnlyList<string> command, int columns, int rows, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<HostInfo> GetHostInfoAsync(CancellationToken ct) => Task.FromResult(new HostInfo(1, 0, 0, 0, "", 0));
    }
}
