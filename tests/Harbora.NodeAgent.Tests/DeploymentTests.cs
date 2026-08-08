using FluentAssertions;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Commands.Handlers;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Hosting;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Sections 8 and 14: idempotent deployment, failed image pull, failed health check and rollback,
/// secret handling, multi-tenant isolation, node restart recovery.
/// </summary>
public sealed class DeploymentTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeHostFacts _host = new();
    private readonly SecretRedactor _redactor = new();
    private readonly RecordingEvents _events = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly WorkloadRegistry _registry;
    private readonly RecordingResponder _responder = new();

    public DeploymentTests()
    {
        _registry = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));
    }

    private WorkloadDeployer Deployer() => new(
        _agent.Wrapped,
        _runtime,
        _registry,
        new PortAllocator(_agent.Options.Ports),
        // The probe polls against a real clock on purpose: its deadline and its sleep have to come
        // from the same source, and a frozen clock would make the loop wait for a deadline that
        // never arrives. Tests that exercise failure set HealthGraceSeconds to 1.
        new HealthProbe(_runtime, TimeProvider.System, TestFactories.Log<HealthProbe>(),
            httpProbe: (_, _, _) => Task.FromResult<int?>(200),
            tcpProbe: (_, _, _, _) => Task.FromResult(true)),
        _host,
        _redactor,
        new NodeMetrics(_clock),
        _events,
        TestFactories.Workspaces(_agent),
        _clock,
        TestFactories.Log<WorkloadDeployer>());

    private CommandContext Context(string? tenantId = "tenant-1")
    {
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, tenantId: tenantId);
        return new CommandContext(envelope, _responder, _clock.GetUtcNow());
    }

    private static DeployWorkloadRequest Request(WorkloadSpec spec, AppManifest? manifest = null, bool dryRun = false) =>
        new() { Spec = spec, Manifest = manifest, DryRun = dryRun };

    private async Task<DeployWorkloadResult> DeployAsync(WorkloadSpec spec, AppManifest? manifest = null, bool admin = false) =>
        await Deployer().DeployAsync(Context(spec.TenantId), Request(spec, manifest), admin, CancellationToken.None);

    // --- the happy path ---

    [Fact]
    public async Task A_deploy_pulls_by_digest_creates_the_container_and_records_the_release()
    {
        var spec = TestFactories.Workload();

        var result = await DeployAsync(spec);

        result.Deployed.Should().BeTrue();
        result.Status.Healthy.Should().BeTrue();

        _runtime.PulledImages.Should().ContainSingle()
            .Which.Should().Contain("@sha256:", "the pull must use the digest, never the tag");

        _runtime.Created.Should().ContainSingle();
        _registry.Find(spec.WorkloadId, spec.TenantId).Should().NotBeNull();
    }

    [Fact]
    public async Task Networks_and_volumes_are_created_before_the_container()
    {
        var spec = TestFactories.Workload();

        await DeployAsync(spec);

        _runtime.Networks.Should().Contain("harbora-tenant-1");
        _runtime.Volumes.Should().Contain("test-app-data");
    }

    [Fact]
    public async Task Every_created_resource_is_labelled_with_its_tenant_and_workload()
    {
        // The labels are how a cleanup pass tells the agent's containers from the machine owner's,
        // and how a cross-tenant read is refused.
        var spec = TestFactories.Workload();

        await DeployAsync(spec);

        var created = _runtime.Created.Single();
        created.Labels[NodeLabels.Managed].Should().Be("true");
        created.Labels[NodeLabels.Tenant].Should().Be("tenant-1");
        created.Labels[NodeLabels.Workload].Should().Be(spec.WorkloadId);
        created.Labels.Should().ContainKey(NodeLabels.Release);
    }

    [Fact]
    public async Task Containers_get_a_stable_network_alias_as_well_as_a_versioned_name()
    {
        // A compose service configured for "db:5432" must keep working across releases, even though
        // the container name carries a release id that changes every time.
        var spec = TestFactories.Workload();

        await DeployAsync(spec);

        var created = _runtime.Created.Single();
        created.Name.Should().Contain("test-app").And.Contain("app");
        created.NetworkAliases.Should().Contain("app");
    }

    [Fact]
    public async Task Containers_default_to_dropping_all_capabilities_and_blocking_new_privileges()
    {
        await DeployAsync(TestFactories.Workload());

        var created = _runtime.Created.Single();
        created.CapabilitiesDrop.Should().Contain("ALL");
        created.NoNewPrivileges.Should().BeTrue();
        created.Privileged.Should().BeFalse();
    }

    // --- idempotency at the reconciliation level ---

    [Fact]
    public async Task Re_deploying_an_identical_healthy_spec_changes_nothing()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);
        var createdOnce = _runtime.Created.Count;

        var second = await DeployAsync(spec);

        second.Deployed.Should().BeFalse();
        second.Warnings.Should().ContainMatch("*already matched*");
        _runtime.Created.Should().HaveCount(createdOnce, "re-creating a healthy service would be a gratuitous restart");
    }

    [Fact]
    public async Task A_changed_spec_produces_a_new_release()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        var updated = spec with { AppVersion = "1.1.0" };
        var result = await DeployAsync(updated);

        result.Deployed.Should().BeTrue();
        _runtime.Created.Should().HaveCount(2);
        _registry.Find(spec.WorkloadId, spec.TenantId)!.Previous.Should().NotBeNull();
    }

    [Fact]
    public void The_fingerprint_is_stable_for_an_identical_spec_and_differs_for_a_changed_one()
    {
        var a = TestFactories.Workload();
        var b = TestFactories.Workload();

        WorkloadDeployer.Fingerprint(a).Should().Be(WorkloadDeployer.Fingerprint(b));
        WorkloadDeployer.Fingerprint(a with { AppVersion = "2.0" }).Should().NotBe(WorkloadDeployer.Fingerprint(a));
    }

    [Fact]
    public async Task A_dry_run_validates_and_touches_nothing()
    {
        var spec = TestFactories.Workload();

        var result = await Deployer().DeployAsync(Context(), Request(spec, dryRun: true), false, CancellationToken.None);

        result.Deployed.Should().BeFalse();
        result.Warnings.Should().ContainMatch("Dry run*");
        _runtime.Created.Should().BeEmpty();
        _runtime.PulledImages.Should().BeEmpty();
    }

    // --- failure paths ---

    [Fact]
    public async Task A_failed_image_pull_fails_the_deploy_and_starts_nothing()
    {
        _runtime.PullFailure = NodeErrorCode.ImagePullFailed;

        var act = async () => await DeployAsync(TestFactories.Workload());

        await act.Should().ThrowAsync<ContainerRuntimeException>();
        _runtime.Created.Should().BeEmpty();
    }

    [Fact]
    public async Task A_digest_that_does_not_match_the_spec_fails_the_deploy()
    {
        // Pulling by digest makes substitution hard; reading back what the daemon actually has
        // turns "hard" into "checked".
        _runtime.DigestOverride = _ => "sha256:" + new string('b', 64);

        var act = async () => await DeployAsync(TestFactories.Workload());

        (await act.Should().ThrowAsync<ContainerRuntimeException>())
            .Which.Message.Should().Contain("pinned");
    }

    [Fact]
    public async Task An_unhealthy_release_is_removed_and_the_previous_one_restored()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        _runtime.HealthOverride = false;
        var failing = spec with
        {
            AppVersion = "2.0.0",
            Upgrade = new UpgradeStrategy { HealthGraceSeconds = 1, AutoRollbackOnFailure = true },
        };

        var result = await DeployAsync(failing);

        result.Deployed.Should().BeFalse();
        result.RolledBack.Should().BeTrue();
        result.Warnings.Should().ContainMatch("*previous release was restored*");

        _events.Published.Should().Contain(e => e.Kind == NodeEventKinds.DeploymentRolledBack);
    }

    [Fact]
    public async Task A_first_release_that_fails_health_reports_that_there_was_nothing_to_restore()
    {
        _runtime.HealthOverride = false;

        var spec = TestFactories.Workload() with
        {
            Upgrade = new UpgradeStrategy { HealthGraceSeconds = 1, AutoRollbackOnFailure = true },
        };

        var result = await DeployAsync(spec);

        result.RolledBack.Should().BeFalse();
        result.Warnings.Should().ContainMatch("*no previous release*");
        _events.Published.Should().Contain(e => e.Kind == NodeEventKinds.DeploymentFailed);
    }

    [Fact]
    public async Task Rollback_can_be_turned_off_for_a_workload_that_must_not_revert()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        _runtime.HealthOverride = false;
        var failing = spec with
        {
            AppVersion = "2.0.0",
            Upgrade = new UpgradeStrategy { HealthGraceSeconds = 1, AutoRollbackOnFailure = false },
        };

        var result = await DeployAsync(failing);

        result.RolledBack.Should().BeFalse();
    }

    [Fact]
    public async Task A_container_that_refuses_to_start_is_reported_rather_than_left_behind()
    {
        _runtime.StartFails = true;

        var spec = TestFactories.Workload() with
        {
            Upgrade = new UpgradeStrategy { HealthGraceSeconds = 1 },
        };

        var result = await DeployAsync(spec);

        result.Deployed.Should().BeFalse();
        _registry.Find(spec.WorkloadId, spec.TenantId).Should().BeNull("a failed first deploy leaves no record");
    }

    // --- secrets ---

    [Fact]
    public async Task Environment_secrets_are_injected_and_never_placed_on_the_command_line()
    {
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "DB_PASSWORD", Value = "correct-horse-battery-staple",
        }));

        await DeployAsync(spec);

        var created = _runtime.Created.Single();
        created.Env["DB_PASSWORD"].Should().Be("correct-horse-battery-staple");
        (created.Command ?? []).Should().NotContain(arg => arg.Contains("correct-horse"));
    }

    [Fact]
    public async Task File_secrets_are_written_to_tmpfs_rather_than_the_writable_layer()
    {
        // The writable layer gets committed, exported and pushed to a registry. A tmpfs does not.
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "tls-key", Value = "-----BEGIN PRIVATE KEY-----material-----END PRIVATE KEY-----",
            MountAs = SecretMount.File, TargetPath = "/run/secrets/tls.key",
        }));

        await DeployAsync(spec);

        var created = _runtime.Created.Single();
        created.TmpfsFiles.Should().ContainSingle().Which.Path.Should().Be("/run/secrets/tls.key");
        created.Env.Should().NotContainKey("tls-key");
    }

    [Fact]
    public async Task Deployed_secrets_are_registered_with_the_redactor()
    {
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "DB_PASSWORD", Value = "s3cret-from-the-panel",
        }));

        await DeployAsync(spec);

        _redactor.Redact("connecting with s3cret-from-the-panel").Should().NotContain("s3cret-from-the-panel");
    }

    // --- tenant isolation ---

    [Fact]
    public async Task A_command_acting_for_one_tenant_cannot_deploy_another_tenants_spec()
    {
        var spec = TestFactories.Workload(tenantId: "tenant-b");

        var act = async () => await Deployer().DeployAsync(
            Context("tenant-a"), Request(spec), false, CancellationToken.None);

        (await act.Should().ThrowAsync<DeploymentRefusedException>())
            .Which.Code.Should().Be(NodeErrorCode.Unauthorized);
    }

    [Fact]
    public async Task One_tenants_workload_id_does_not_resolve_for_another_tenant()
    {
        await DeployAsync(TestFactories.Workload(workloadId: "wl-shared", tenantId: "tenant-a"));

        _registry.Find("wl-shared", "tenant-a").Should().NotBeNull();
        _registry.Find("wl-shared", "tenant-b").Should().BeNull(
            "the tenant is part of the key, not a filter applied afterwards");
    }

    // --- manifests ---

    [Fact]
    public async Task A_manifest_that_needs_a_newer_agent_is_refused()
    {
        var manifest = Manifest() with { MinimumNodeVersion = "99.0.0" };

        var act = async () => await DeployAsync(TestFactories.Workload(), manifest);

        (await act.Should().ThrowAsync<DeploymentRefusedException>())
            .Which.Code.Should().Be(NodeErrorCode.AgentTooOld);
    }

    [Fact]
    public async Task A_manifest_for_another_architecture_is_refused()
    {
        var manifest = Manifest() with { SupportedArchitectures = ["arm64"] };

        var act = async () => await DeployAsync(TestFactories.Workload(), manifest);

        (await act.Should().ThrowAsync<DeploymentRefusedException>())
            .Which.Code.Should().Be(NodeErrorCode.UnsupportedArchitecture);
    }

    [Fact]
    public async Task A_manifest_claiming_an_architecture_with_no_digest_for_it_is_invalid()
    {
        var manifest = Manifest() with
        {
            SupportedArchitectures = ["amd64", "arm64"],
            Images =
            [
                new ManifestImage
                {
                    Role = "app",
                    Repository = "registry.test/app",
                    DigestByArchitecture = new Dictionary<string, string> { ["amd64"] = "sha256:" + new string('a', 64) },
                },
            ],
        };

        var act = async () => await DeployAsync(TestFactories.Workload(), manifest);

        (await act.Should().ThrowAsync<DeploymentRefusedException>())
            .Which.Message.Should().Contain("no digest");
    }

    [Fact]
    public async Task A_spec_missing_a_required_manifest_secret_is_refused()
    {
        var manifest = Manifest() with
        {
            SecretSchema = [new SecretField { Key = "DB_PASSWORD", Required = true }],
        };

        var act = async () => await DeployAsync(TestFactories.Workload(), manifest);

        (await act.Should().ThrowAsync<DeploymentRefusedException>())
            .Which.Message.Should().Contain("DB_PASSWORD");
    }

    [Fact]
    public async Task A_spec_satisfying_the_manifest_deploys()
    {
        var manifest = Manifest() with
        {
            EnvironmentSchema = [new EnvironmentField { Key = "LOG_LEVEL", Required = true }],
            SecretSchema = [new SecretField { Key = "DB_PASSWORD", Required = true }],
        };

        var spec = TestFactories.Workload();
        var complete = spec with
        {
            Containers =
            [
                spec.Containers[0] with
                {
                    Env = new Dictionary<string, string> { ["LOG_LEVEL"] = "info" },
                    Secrets = [new SecretSpec { Name = "DB_PASSWORD", Value = "generated-by-the-panel" }],
                },
            ],
        };

        (await DeployAsync(complete, manifest)).Deployed.Should().BeTrue();
    }

    // --- port allocation ---

    [Fact]
    public async Task Published_ports_are_allocated_from_the_configured_range()
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 8080, PublishToHost = true });
        });

        var result = await DeployAsync(spec);

        var port = result.AllocatedPorts.Should().ContainSingle().Subject.Value;
        port.Should().BeInRange(30_000, 32_767);
    }

    [Fact]
    public async Task Allocation_avoids_ports_the_host_is_already_listening_on()
    {
        // Consulting only our own bookkeeping collides with whatever else the customer runs, which
        // surfaces later as "the deploy worked yesterday".
        _host.Ports = Enumerable.Range(30_000, 5).ToList();

        var spec = TestFactories.Workload(container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 8080, PublishToHost = true });
        });

        var result = await DeployAsync(spec);

        result.AllocatedPorts.Single().Value.Should().Be(30_005);
    }

    [Fact]
    public async Task Two_workloads_do_not_receive_the_same_host_port()
    {
        PortMapping Published() => new() { ContainerPort = 8080, PublishToHost = true };

        var first = TestFactories.Workload(workloadId: "wl-1", container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(Published());
        });

        var second = TestFactories.Workload(workloadId: "wl-2", container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(Published());
        });

        var a = await DeployAsync(first);
        var b = await DeployAsync(second);

        a.AllocatedPorts.Single().Value.Should().NotBe(b.AllocatedPorts.Single().Value);
    }

    // --- lifecycle ---

    [Fact]
    public async Task Deleting_a_workload_keeps_its_volumes_by_default()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        var record = _registry.Find(spec.WorkloadId, spec.TenantId)!;
        await Deployer().DeleteAsync(record, deleteVolumes: false, force: false, CancellationToken.None);

        _runtime.Volumes.Should().Contain("test-app-data",
            "deleting a workload and deleting its data are different decisions");
        _registry.Find(spec.WorkloadId, spec.TenantId).Should().BeNull();
    }

    [Fact]
    public async Task Deleting_with_volumes_removes_them()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        var record = _registry.Find(spec.WorkloadId, spec.TenantId)!;
        await Deployer().DeleteAsync(record, deleteVolumes: true, force: false, CancellationToken.None);

        _runtime.Volumes.Should().NotContain("test-app-data");
    }

    // --- restart recovery ---

    [Fact]
    public async Task Reconciliation_restarts_a_workload_that_came_back_stopped()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        foreach (var id in _runtime.Containers.Keys.ToList())
            await _runtime.StopAsync(id, 0, CancellationToken.None);

        var report = await Reconciler().ReconcileAsync(CancellationToken.None);

        report.Restarted.Should().Be(1);
        _runtime.Containers.Values.Should().OnlyContain(c => c.State == "running");
    }

    [Fact]
    public async Task Reconciliation_reports_a_workload_whose_containers_vanished()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);
        _runtime.Containers.Clear();

        var report = await Reconciler().ReconcileAsync(CancellationToken.None);

        report.Missing.Should().Be(1);
        report.Problems.Should().ContainMatch("*no containers present*");
    }

    [Fact]
    public async Task Reconciliation_does_nothing_while_the_runtime_is_unreachable()
    {
        // Concluding that every container is missing would try to recreate all of them the moment
        // Docker came back.
        await DeployAsync(TestFactories.Workload());
        _runtime.Available = false;

        var report = await Reconciler().ReconcileAsync(CancellationToken.None);

        report.Checked.Should().Be(0);
        report.Problems.Should().Contain("container runtime unavailable");
    }

    [Fact]
    public async Task The_registry_survives_a_restart()
    {
        var spec = TestFactories.Workload();
        await DeployAsync(spec);

        var afterRestart = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));

        afterRestart.Find(spec.WorkloadId, spec.TenantId).Should().NotBeNull();
        afterRestart.AllocatedPorts().Should().BeEmpty();
    }

    private StateReconciler Reconciler() =>
        new(_registry, Deployer(), _runtime, TestFactories.Audit(_agent), TestFactories.Log<StateReconciler>());

    private static AppManifest Manifest() => new()
    {
        AppId = "test-app",
        TemplateVersion = "1.0.0",
        ApplicationVersion = "1.0.0",
        SupportedArchitectures = ["amd64"],
        Images =
        [
            new ManifestImage
            {
                Role = "app",
                Repository = "registry.test/app",
                DigestByArchitecture = new Dictionary<string, string> { ["amd64"] = "sha256:" + new string('a', 64) },
            },
        ],
    };

    public void Dispose() => _agent.Dispose();

    /// <summary>Records the events the deployer publishes instead of sending them anywhere.</summary>
    private sealed class RecordingEvents : INodeEventPublisher
    {
        public List<NodeEvent> Published { get; } = [];

        public Task<bool> PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
        {
            lock (Published) Published.Add(nodeEvent);
            return Task.FromResult(true);
        }

        public Task<bool> PublishEphemeralAsync(NodeEvent nodeEvent, CancellationToken ct) =>
            PublishAsync(nodeEvent, ct);
    }
}
