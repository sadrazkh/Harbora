using FluentAssertions;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Commands.Handlers;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 7: each verb's own behaviour, including the tenant check every one of them applies and
/// the "already in this state" answers that make a retry harmless.
/// </summary>
public sealed class CommandHandlerTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeHostFacts _host = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly RecordingResponder _responder = new();
    private readonly WorkloadRegistry _registry;
    private readonly RouteRegistry _routes;

    public CommandHandlerTests()
    {
        _registry = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));
        _routes = new RouteRegistry(TestFactories.Store<RouteRegistryState>(_agent, "routes.json"));
    }

    private WorkloadDeployer Deployer() => new(
        _agent.Wrapped, _runtime, _registry, new PortAllocator(_agent.Options.Ports),
        new HealthProbe(_runtime, TimeProvider.System, TestFactories.Log<HealthProbe>()),
        _host, new SecretRedactor(), new NodeMetrics(_clock), new NullEvents(), TestFactories.Workspaces(_agent), _clock,
        TestFactories.Log<WorkloadDeployer>());

    private CommandContext Context(string command, object payload, string? tenantId = "tenant-1") =>
        new(TestFactories.Envelope(command, payload, tenantId: tenantId), _responder, _clock.GetUtcNow());

    private async Task<WorkloadRecord> DeployedAsync(Action<TestFactories.ContainerSpecBuilder>? container = null)
    {
        var spec = TestFactories.Workload(container: container);

        await Deployer().DeployAsync(
            Context(NodeCommands.DeployWorkload, new { }),
            new DeployWorkloadRequest { Spec = spec }, false, CancellationToken.None);

        return _registry.Find(spec.WorkloadId, spec.TenantId)!;
    }

    // --- lifecycle ---

    [Fact]
    public async Task Stopping_an_already_stopped_workload_is_a_no_op_rather_than_an_error()
    {
        var record = await DeployedAsync();
        var handler = new StopWorkloadHandler(_registry, Deployer());

        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = record.TenantId };

        var first = await handler.HandleAsync(Context(NodeCommands.StopWorkload, payload), CancellationToken.None);
        var second = await handler.HandleAsync(Context(NodeCommands.StopWorkload, payload), CancellationToken.None);

        first.Status.Should().Be(CommandStatus.Succeeded);
        second.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Starting_a_stopped_workload_brings_it_back()
    {
        var record = await DeployedAsync();
        var deployer = Deployer();
        await deployer.StopAsync(record, CancellationToken.None);

        var handler = new StartWorkloadHandler(_registry, deployer);
        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = record.TenantId };

        var result = await handler.HandleAsync(Context(NodeCommands.StartWorkload, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        _runtime.Containers.Values.Should().OnlyContain(c => c.State == "running");
    }

    [Fact]
    public async Task A_lifecycle_command_for_another_tenant_finds_nothing()
    {
        var record = await DeployedAsync();
        var handler = new RestartWorkloadHandler(_registry, Deployer());

        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = "tenant-b" };
        var result = await handler.HandleAsync(
            Context(NodeCommands.RestartWorkload, payload, tenantId: "tenant-b"), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Failed);
        result.Error!.Message.Should().Contain("No workload");
    }

    [Fact]
    public async Task Deleting_a_workload_that_is_not_here_reports_success()
    {
        // The desired end state already holds. Reporting failure would make a retried delete look
        // like something to investigate.
        var handler = new DeleteWorkloadHandler(_registry, Deployer(), TestFactories.Log<DeleteWorkloadHandler>());

        var payload = new DeleteWorkloadRequest { WorkloadId = "never-deployed", TenantId = "tenant-1" };
        var result = await handler.HandleAsync(Context(NodeCommands.DeleteWorkload, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Status_for_an_unknown_workload_is_absent_rather_than_an_error()
    {
        var handler = new GetWorkloadStatusHandler(_registry, Deployer());

        var payload = new WorkloadRequest { WorkloadId = "never-deployed", TenantId = "tenant-1" };
        var result = await handler.HandleAsync(Context(NodeCommands.GetWorkloadStatus, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("state").GetString().Should().Be("absent");
    }

    // --- statistics ---

    [Fact]
    public async Task Stats_report_what_the_runtime_gave_for_each_container()
    {
        var record = await DeployedAsync();
        var container = record.Spec.Containers[0];
        _runtime.Stats[record.ContainerName(container.Name)] =
            new RuntimeContainerStats(12.5, 256 * 1024 * 1024, 512 * 1024 * 1024, 900, 100);

        var handler = new GetWorkloadStatsHandler(_registry, _runtime, _clock);
        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = "tenant-1" };

        var result = await handler.HandleAsync(Context(NodeCommands.GetWorkloadStats, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        var first = result.Result!.Value.GetProperty("containers")[0];
        first.GetProperty("cpuPercent").GetDouble().Should().Be(12.5);
        first.GetProperty("memoryUsedBytes").GetInt64().Should().Be(256 * 1024 * 1024);
    }

    [Fact]
    public async Task A_container_the_runtime_would_not_read_reports_nothing_rather_than_zero()
    {
        // The ordinary case for a container that is starting or already gone. Zeroes here would
        // draw an idle application at exactly the moment something is wrong with it — which is the
        // reason the control plane's own charts were empty before this verb existed.
        var record = await DeployedAsync();

        var handler = new GetWorkloadStatsHandler(_registry, _runtime, _clock);
        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = "tenant-1" };

        var result = await handler.HandleAsync(Context(NodeCommands.GetWorkloadStats, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);

        // Read back through the contract type, which is what the control plane does. On the wire an
        // unreported figure is an absent property rather than an explicit null — the distinction the
        // caller has to end up with is null, and that is what this asserts.
        var stats = System.Text.Json.JsonSerializer.Deserialize<WorkloadStats>(
            result.Result!.Value.GetRawText(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        stats!.Containers.Should().ContainSingle();
        stats.Containers[0].CpuPercent.Should().BeNull();
        stats.Containers[0].MemoryUsedBytes.Should().BeNull();
    }

    [Fact]
    public async Task Stats_for_another_tenants_workload_are_not_returned()
    {
        // Read verbs leak. The tenant filter is the same one every other verb applies, and a
        // resource reading is still a disclosure about somebody else's workload.
        var record = await DeployedAsync();
        _runtime.Stats[record.ContainerName(record.Spec.Containers[0].Name)] =
            new RuntimeContainerStats(99, 1, 1, 1, 1);

        var handler = new GetWorkloadStatsHandler(_registry, _runtime, _clock);
        var payload = new WorkloadRequest { WorkloadId = record.WorkloadId, TenantId = "tenant-2" };

        var result = await handler.HandleAsync(
            Context(NodeCommands.GetWorkloadStats, payload, tenantId: "tenant-2"), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Stats_for_an_unknown_workload_are_empty_rather_than_an_error()
    {
        var handler = new GetWorkloadStatsHandler(_registry, _runtime, _clock);
        var payload = new WorkloadRequest { WorkloadId = "never-deployed", TenantId = "tenant-1" };

        var result = await handler.HandleAsync(Context(NodeCommands.GetWorkloadStats, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("containers").GetArrayLength().Should().Be(0);
    }

    // --- listing ---

    [Fact]
    public async Task Listing_workloads_returns_this_tenants_set()
    {
        await DeployedAsync();
        var handler = new ListWorkloadsHandler(_registry, Deployer());

        var payload = new ListWorkloadsRequest { TenantId = "tenant-1" };
        var result = await handler.HandleAsync(Context(NodeCommands.ListWorkloads, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);

        var workloads = result.Result!.Value.GetProperty("workloads");
        workloads.GetArrayLength().Should().Be(1);
        workloads[0].GetProperty("name").GetString().Should().Be("test-app");
        workloads[0].GetProperty("status").GetProperty("state").GetString().Should().Be("running");
    }

    [Fact]
    public async Task Listing_never_shows_another_tenants_workloads()
    {
        // A read verb leaks, and this is the one that leaks the most at once — an inventory rather
        // than a single lookup.
        await DeployedAsync();
        var handler = new ListWorkloadsHandler(_registry, Deployer());

        var payload = new ListWorkloadsRequest { TenantId = "tenant-b" };
        var result = await handler.HandleAsync(
            Context(NodeCommands.ListWorkloads, payload, tenantId: "tenant-b"), CancellationToken.None);

        result.Result!.Value.GetProperty("workloads").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Listing_for_a_tenant_the_command_does_not_act_for_is_refused()
    {
        await DeployedAsync();
        var handler = new ListWorkloadsHandler(_registry, Deployer());

        var payload = new ListWorkloadsRequest { TenantId = "tenant-1" };
        var result = await handler.HandleAsync(
            Context(NodeCommands.ListWorkloads, payload, tenantId: "tenant-b"), CancellationToken.None);

        result.Error!.Code.Should().Be(NodeErrorCode.Unauthorized);
    }

    [Fact]
    public async Task Listing_can_skip_the_status_read()
    {
        // A node holding fifty workloads should be able to answer cheaply when the caller only
        // wants names: status costs one runtime inspect each.
        await DeployedAsync();
        var handler = new ListWorkloadsHandler(_registry, Deployer());

        var payload = new ListWorkloadsRequest { TenantId = "tenant-1", IncludeStatus = false };
        var result = await handler.HandleAsync(Context(NodeCommands.ListWorkloads, payload), CancellationToken.None);

        var workload = result.Result!.Value.GetProperty("workloads")[0];
        workload.TryGetProperty("status", out var status).Should().BeFalse();
        _ = status;
    }

    [Fact]
    public async Task Listing_can_be_narrowed_to_one_app()
    {
        await DeployedAsync();
        var handler = new ListWorkloadsHandler(_registry, Deployer());

        var payload = new ListWorkloadsRequest { TenantId = "tenant-1", AppId = "some-other-app" };
        var result = await handler.HandleAsync(Context(NodeCommands.ListWorkloads, payload), CancellationToken.None);

        result.Result!.Value.GetProperty("workloads").GetArrayLength().Should().Be(0);
    }

    // --- logs ---

    [Fact]
    public async Task Streaming_logs_emits_chunks_and_a_final_marker()
    {
        var record = await DeployedAsync();
        var handler = new StreamLogsHandler(_registry, _runtime);

        var payload = new StreamLogsRequest { WorkloadId = record.WorkloadId, TenantId = record.TenantId };
        var result = await handler.HandleAsync(Context(NodeCommands.StreamLogs, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        _responder.Logs.Should().NotBeEmpty();
        _responder.Logs.Last().Final.Should().BeTrue();
    }

    [Fact]
    public async Task Streaming_logs_from_a_container_the_workload_lacks_is_refused()
    {
        var record = await DeployedAsync();
        var handler = new StreamLogsHandler(_registry, _runtime);

        var payload = new StreamLogsRequest
        {
            WorkloadId = record.WorkloadId, TenantId = record.TenantId, ContainerName = "ghost",
        };

        var result = await handler.HandleAsync(Context(NodeCommands.StreamLogs, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Failed);
        result.Error!.Code.Should().Be(NodeErrorCode.ValidationFailed);
    }

    // --- networks ---

    [Fact]
    public async Task Creating_a_network_twice_is_idempotent()
    {
        var handler = new CreateNetworkHandler(_runtime);
        var payload = new NetworkRequest { TenantId = "tenant-1", Network = new NetworkSpec { Name = "harbora-tenant-1" } };

        await handler.HandleAsync(Context(NodeCommands.CreateNetwork, payload), CancellationToken.None);
        var second = await handler.HandleAsync(Context(NodeCommands.CreateNetwork, payload), CancellationToken.None);

        second.Status.Should().Be(CommandStatus.Succeeded);
        _runtime.Networks.Should().ContainSingle();
    }

    [Fact]
    public async Task A_network_command_naming_another_tenant_is_refused()
    {
        var handler = new CreateNetworkHandler(_runtime);
        var payload = new NetworkRequest { TenantId = "tenant-b", Network = new NetworkSpec { Name = "harbora-tenant-b" } };

        var result = await handler.HandleAsync(
            Context(NodeCommands.CreateNetwork, payload, tenantId: "tenant-a"), CancellationToken.None);

        result.Error!.Code.Should().Be(NodeErrorCode.Unauthorized);
        _runtime.Networks.Should().BeEmpty();
    }

    [Fact]
    public async Task A_network_with_containers_still_attached_is_not_removed()
    {
        await DeployedAsync();

        var handler = new DeleteNetworkHandler(_runtime, TestFactories.Log<DeleteNetworkHandler>());
        var payload = new NetworkRequest { TenantId = "tenant-1", Network = new NetworkSpec { Name = "harbora-tenant-1" } };

        var result = await handler.HandleAsync(Context(NodeCommands.DeleteNetwork, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Failed);
        result.Error!.Message.Should().Contain("still has");
        _runtime.Networks.Should().Contain("harbora-tenant-1");
    }

    // --- volumes ---

    [Fact]
    public async Task Creating_a_volume_reports_whether_it_already_existed()
    {
        var handler = new CreateVolumeHandler(_runtime);
        var payload = new VolumeRequest { TenantId = "tenant-1", Volume = new VolumeSpec { Name = "app-data" } };

        var first = await handler.HandleAsync(Context(NodeCommands.CreateVolume, payload), CancellationToken.None);
        var second = await handler.HandleAsync(Context(NodeCommands.CreateVolume, payload), CancellationToken.None);

        first.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeFalse();
        second.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("../escape")]
    [InlineData("name:with:colons")]
    public async Task A_volume_name_that_is_really_a_path_is_refused_here_too(string name)
    {
        // The same rule as the deploy path. Two places that could disagree about what a volume name
        // is would be one place where the check is missing.
        var handler = new CreateVolumeHandler(_runtime);
        var payload = new VolumeRequest { TenantId = "tenant-1", Volume = new VolumeSpec { Name = name } };

        var result = await handler.HandleAsync(Context(NodeCommands.CreateVolume, payload), CancellationToken.None);

        result.Error!.Code.Should().Be(NodeErrorCode.PolicyDenied);
        _runtime.Volumes.Should().BeEmpty();
    }

    // --- routes ---

    [Fact]
    public async Task Registering_a_route_for_an_unpublished_port_explains_the_fix()
    {
        var record = await DeployedAsync();
        var handler = new RegisterHttpRouteHandler(
            _registry, _routes, _host, _clock, TestFactories.Log<RegisterHttpRouteHandler>());

        var payload = new RegisterHttpRouteRequest
        {
            TenantId = record.TenantId,
            WorkloadId = record.WorkloadId,
            Route = new HttpRouteSpec
            {
                RouteId = "r1", Domain = "app.test", TargetContainer = "app", TargetPort = 8080,
            },
        };

        var result = await handler.HandleAsync(Context(NodeCommands.RegisterHttpRoute, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Failed);
        result.Error!.Message.Should().Contain("publishToHost");
    }

    [Fact]
    public async Task Registering_a_route_for_a_published_port_returns_the_endpoint()
    {
        var record = await DeployedAsync(c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 8080, PublishToHost = true });
        });

        var handler = new RegisterHttpRouteHandler(
            _registry, _routes, _host, _clock, TestFactories.Log<RegisterHttpRouteHandler>());

        var payload = new RegisterHttpRouteRequest
        {
            TenantId = record.TenantId,
            WorkloadId = record.WorkloadId,
            Route = new HttpRouteSpec
            {
                RouteId = "r1", Domain = "app.test", TargetContainer = "app", TargetPort = 8080,
            },
        };

        var result = await handler.HandleAsync(Context(NodeCommands.RegisterHttpRoute, payload), CancellationToken.None);

        result.Status.Should().Be(CommandStatus.Succeeded);
        result.Result!.Value.GetProperty("publicEndpoint").GetString().Should().StartWith("203.0.113.10:3");

        _routes.Find("r1", record.TenantId).Should().NotBeNull();
    }

    [Fact]
    public async Task Removing_a_route_reports_what_actually_happened()
    {
        var handler = new RemoveRouteHandler(_routes, TestFactories.Log<RemoveRouteHandler>());

        _routes.Save(new RouteRecord
        {
            RouteId = "r1", TenantId = "tenant-1", WorkloadId = "wl-1", Kind = "http",
            Endpoint = "203.0.113.10:30001", RegisteredAt = _clock.GetUtcNow(),
        });

        var payload = new RemoveRouteRequest { TenantId = "tenant-1", RouteId = "r1" };

        var first = await handler.HandleAsync(Context(NodeCommands.RemoveRoute, payload), CancellationToken.None);
        var second = await handler.HandleAsync(Context(NodeCommands.RemoveRoute, payload), CancellationToken.None);

        first.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeFalse();
        second.Result!.Value.GetProperty("noOp").GetBoolean().Should().BeTrue(
            "a caller must never log a removal it did not perform");
    }

    [Fact]
    public void One_tenants_route_id_does_not_resolve_for_another()
    {
        _routes.Save(new RouteRecord
        {
            RouteId = "shared-id", TenantId = "tenant-a", WorkloadId = "wl-1", Kind = "http",
            Endpoint = "203.0.113.10:30001", RegisteredAt = _clock.GetUtcNow(),
        });

        _routes.Find("shared-id", "tenant-a").Should().NotBeNull();
        _routes.Find("shared-id", "tenant-b").Should().BeNull();
        _routes.Remove("shared-id", "tenant-b").Should().BeFalse();
    }

    // --- volume archiving ---

    [Fact]
    public async Task A_snapshot_runs_helpers_with_argv_arrays_and_no_shell()
    {
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());
        _runtime.Volumes.Add("app-data");

        _runtime.OneOffExitCode = 0;
        var snapshot = await archiver.SnapshotAsync("app-data", "snap-1", compress: true, null, CancellationToken.None);

        _runtime.OneOffs.Should().NotBeEmpty();
        _runtime.OneOffs.Should().OnlyContain(o => o.Command.Count > 0);
        _runtime.OneOffs.Should().NotContain(o => o.Command.Contains("sh") || o.Command.Contains("bash"));
        _runtime.OneOffs[0].Command.Should().StartWith(["tar", "-czf"]);

        snapshot.Path.Should().StartWith(VolumeArchiver.ArchiveVolume + ":");
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("snap;rm -rf /")]
    [InlineData("snap id")]
    public async Task A_snapshot_id_that_is_not_a_plain_name_is_refused(string snapshotId)
    {
        // Even without a shell, a value like "../../etc" in a path argument is a traversal the
        // helper would honour.
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());
        _runtime.Volumes.Add("app-data");

        var act = async () => await archiver.SnapshotAsync("app-data", snapshotId, true, null, CancellationToken.None);

        await act.Should().ThrowAsync<VolumeArchiver.ArchiveException>();
    }

    [Fact]
    public async Task Snapshotting_a_volume_that_does_not_exist_fails_clearly()
    {
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());

        var act = async () => await archiver.SnapshotAsync("nope", "snap-1", true, null, CancellationToken.None);

        (await act.Should().ThrowAsync<VolumeArchiver.ArchiveException>())
            .Which.Message.Should().Contain("does not exist");
    }

    [Fact]
    public async Task A_restore_verifies_the_checksum_before_it_writes_anything()
    {
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());

        // The checksum helper reports something other than what the caller expects.
        var act = async () => await archiver.RestoreAsync(
            "app-data", "snap-1", new string('f', 64), compressed: true, null, CancellationToken.None);

        await act.Should().ThrowAsync<VolumeArchiver.ArchiveException>();

        _runtime.OneOffs.Should().OnlyContain(o => o.Command[0] == "sha256sum",
            "nothing beyond the checksum should have run");
    }

    [Fact]
    public async Task Snapshot_relay_can_only_call_the_configured_panel_and_redacts_its_token()
    {
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());
        var redactor = new SecretRedactor();
        var transfer = new ArtifactRelayTransfer(_runtime, archiver, redactor, _agent.Wrapped);
        var token = new string('a', 64);
        var relayId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var result = await transfer.TransferAsync(new TransferSnapshotRequest
        {
            TenantId = "tenant-1",
            SnapshotId = "snap-1",
            Direction = SnapshotTransferDirection.UploadToPanel,
            RelayId = relayId,
            RelayToken = token,
        }, CancellationToken.None);

        var curl = _runtime.OneOffs.First(o => o.Command.FirstOrDefault() == "sh");
        curl.Env["RELAY_URL"].Should().Be("https://panel.test/api/node-artifacts/11111111-2222-3333-4444-555555555555");
        string.Join(' ', curl.Command).Should().NotContain(token);
        redactor.Redact($"Bearer {token}").Should().NotContain(token);
        result.Sha256.Should().Be(_runtime.HelperChecksum);
        _runtime.PulledImages.Should().Contain(_agent.Options.ArtifactTransferImage);
    }

    [Fact]
    public async Task A_corrupt_relay_download_is_deleted_before_restore_can_start()
    {
        var archiver = new VolumeArchiver(_agent.Wrapped, _runtime, _clock, TestFactories.Log<VolumeArchiver>());
        var transfer = new ArtifactRelayTransfer(_runtime, archiver, new SecretRedactor(), _agent.Wrapped);

        var act = async () => await transfer.TransferAsync(new TransferSnapshotRequest
        {
            TenantId = "tenant-1",
            SnapshotId = "snap-2",
            Direction = SnapshotTransferDirection.DownloadFromPanel,
            RelayId = Guid.NewGuid(),
            RelayToken = new string('b', 64),
            ArtifactSizeBytes = 4096,
            ExpectedSha256 = new string('f', 64),
        }, CancellationToken.None);

        await act.Should().ThrowAsync<VolumeArchiver.ArchiveException>();
        _runtime.OneOffs.Should().Contain(o =>
            o.Command.SequenceEqual(new[] { "rm", "-f", "/snapshots/snap-2.tar.gz" }));
    }

    public void Dispose() => _agent.Dispose();

    private sealed class NullEvents : INodeEventPublisher
    {
        public Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct) => Task.FromResult(true);

        public Task<bool> PublishEphemeralAsync(NodeEvent nodeEvent, CancellationToken ct) => Task.FromResult(true);
    }
}
