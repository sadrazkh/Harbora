using FluentAssertions;
using Harbora.NodeAgent;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>Section 5: version reporting and the minimum-supported-agent comparison.</summary>
public class AgentVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.2.0", "1.10.0", -1)]
    [InlineData("2.0.0", "1.99.99", 1)]
    [InlineData("0.2.0", "0.2", 0)]
    [InlineData("1.0.1", "1.0.0", 1)]
    public void Versions_compare_numerically_not_lexically(string left, string right, int expected)
    {
        // "1.10.0" sorts before "1.2.0" as text, which would ground every node in a fleet the day
        // the minor version reached ten.
        Math.Sign(AgentVersion.Compare(left, right)).Should().Be(expected);
    }

    [Fact]
    public void A_pre_release_suffix_does_not_make_a_version_older()
    {
        AgentVersion.Compare("1.2.0-rc1", "1.2.0").Should().Be(0);
        AgentVersion.IsAtLeast("1.2.0-rc1", "1.2.0").Should().BeTrue();
    }

    [Fact]
    public void No_minimum_means_every_version_qualifies()
    {
        AgentVersion.IsAtLeast("0.0.1", null).Should().BeTrue();
        AgentVersion.IsAtLeast("0.0.1", "").Should().BeTrue();
    }

    [Fact]
    public void An_unparseable_component_reads_as_zero_rather_than_throwing()
    {
        AgentVersion.Compare("1.x.0", "1.0.0").Should().Be(0);
    }

    [Fact]
    public void The_build_reports_a_semver_shaped_version()
    {
        AgentVersion.Current.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        AgentVersion.Current.Should().NotContain("+", "the contract's semver pattern excludes the SDK's commit suffix");
    }
}

/// <summary>Section 12: pressure signals and the health word the scheduler reads.</summary>
public class NodeHealthEvaluatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 4, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The host is sampled once, here, and handed to the evaluator as an input. It used to read
    /// <see cref="IHostFacts"/> itself, which re-reads /proc on every access — so one verdict could
    /// be built from three different moments.
    /// </summary>
    private static (NodeHealthEvaluator Evaluator, HealthInputs Healthy) Build(Action<FakeHostFacts>? configure = null)
    {
        var host = new FakeHostFacts();
        configure?.Invoke(host);

        return (new NodeHealthEvaluator(), new HealthInputs
        {
            RuntimeAvailable = true,
            Draining = false,
            ChannelConnected = true,
            CertificateExpiresAt = Now.AddDays(60),
            Host = HostSample.Take(host, "/tmp"),
        });
    }

    [Fact]
    public void A_node_with_room_is_healthy()
    {
        var (evaluator, healthy) = Build();

        var verdict = evaluator.Evaluate(healthy, Now);

        verdict.State.Should().Be(NodeHealthState.Healthy);
        verdict.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void Low_disk_degrades_rather_than_condemns()
    {
        // The containers already running are fine; what the node must not do is accept a deploy
        // that will pull two gigabytes.
        var (evaluator, healthy) = Build(h => h.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000));

        var verdict = evaluator.Evaluate(healthy, Now);

        verdict.State.Should().Be(NodeHealthState.Degraded);
        verdict.DiskPressure.Should().BeTrue();
        verdict.Reasons.Should().ContainMatch("disk free*");
    }

    [Fact]
    public void A_large_disk_with_a_small_free_ratio_still_counts_as_pressure()
    {
        var (evaluator, healthy) = Build(h => h.DiskSpace = new DiskSpace(4_000_000_000_000, 200_000_000_000));

        evaluator.Evaluate(healthy, Now).DiskPressure.Should().BeTrue("5% free is pressure regardless of the absolute number");
    }

    [Fact]
    public void Memory_and_cpu_pressure_are_reported_separately()
    {
        var (evaluator, healthy) = Build(h =>
        {
            h.FreeMemoryBytes = 100 * 1024 * 1024;
            h.Load = new LoadAverage(20, 18, 15);
        });

        var verdict = evaluator.Evaluate(healthy, Now);

        verdict.MemoryPressure.Should().BeTrue();
        verdict.CpuPressure.Should().BeTrue();
        verdict.State.Should().Be(NodeHealthState.Degraded);
    }

    [Fact]
    public void An_unavailable_runtime_is_unhealthy()
    {
        var (evaluator, healthy) = Build();

        var verdict = evaluator.Evaluate(healthy with { RuntimeAvailable = false }, Now);

        verdict.State.Should().Be(NodeHealthState.Unhealthy);
        verdict.Reasons.Should().Contain("container runtime unavailable");
    }

    [Fact]
    public void A_revoked_credential_outranks_everything_else()
    {
        var (evaluator, healthy) = Build(h => h.DiskSpace = new DiskSpace(100, 1));

        var verdict = evaluator.Evaluate(healthy with { CredentialRevoked = true }, Now);

        verdict.State.Should().Be(NodeHealthState.Unhealthy);
        verdict.Reasons.Should().ContainMatch("*re-enroll*");
    }

    [Fact]
    public void Draining_outranks_pressure()
    {
        // Reporting a drained node as merely degraded would invite the scheduler to try it.
        var (evaluator, healthy) = Build(h => h.DiskSpace = new DiskSpace(100_000_000_000, 1_000_000_000));

        evaluator.Evaluate(healthy with { Draining = true }, Now).State.Should().Be(NodeHealthState.Draining);
    }

    [Fact]
    public void A_certificate_close_to_expiry_is_flagged_long_before_it_bites()
    {
        var (evaluator, healthy) = Build();

        var verdict = evaluator.Evaluate(healthy with { CertificateExpiresAt = Now.AddDays(3) }, Now);

        verdict.CertificateExpiringSoon.Should().BeTrue();
        verdict.Reasons.Should().ContainMatch("credential expires*");
    }

    [Fact]
    public void A_disconnected_channel_is_recorded_as_a_reason_without_condemning_the_node()
    {
        var (evaluator, healthy) = Build();

        var verdict = evaluator.Evaluate(healthy with { ChannelConnected = false }, Now);

        verdict.Reasons.Should().Contain("control channel disconnected");
        verdict.State.Should().Be(NodeHealthState.Healthy, "the containers keep running while the panel is unreachable");
    }
}

/// <summary>Section 6: what the node reports about itself.</summary>
public sealed class InventoryTests : IDisposable
{
    private readonly TempAgent _agent = new(o =>
    {
        o.Region = "eu-central";
        o.Environment = "production";
        o.Labels["tier"] = "premium";
    });

    private readonly FakeHostFacts _host = new();
    private readonly FakeContainerRuntime _runtime = new();

    private InventoryCollector Collector() => TestFactories.Inventory(_agent, _host, _runtime);

    [Fact]
    public async Task Inventory_carries_everything_section_six_asks_for()
    {
        var inventory = await Collector().CollectAsync(CancellationToken.None);

        inventory.OsName.Should().Be("Debian GNU/Linux");
        inventory.KernelVersion.Should().Be("6.1.0-test");
        inventory.Architecture.Should().Be("amd64");
        inventory.ContainerRuntime.Should().Be("docker");
        inventory.ContainerRuntimeVersion.Should().Be("27.3.1");
        inventory.CpuCores.Should().Be(4);
        inventory.TotalMemoryBytes.Should().BeGreaterThan(0);
        inventory.TotalDiskBytes.Should().BeGreaterThan(0);
        inventory.IpAddresses.Should().Contain("203.0.113.10");
        inventory.Region.Should().Be("eu-central");
        inventory.Environment.Should().Be("production");
        inventory.Labels.Should().ContainKey("tier");
        inventory.AvailablePortRange.Start.Should().Be(30_000);
        inventory.UsedPorts.Should().Contain(443);
        inventory.Storage.DataRoot.Should().Be(_agent.Root);
    }

    [Fact]
    public async Task A_node_whose_docker_is_down_still_reports_in()
    {
        // Failing to build an inventory would look identical to being offline — and being offline
        // is precisely what the panel would then fail to distinguish from a broken daemon.
        _runtime.Available = false;

        var inventory = await Collector().CollectAsync(CancellationToken.None);

        inventory.ContainerRuntimeVersion.Should().Be("27.3.1");
        inventory.Hostname.Should().Be("test-node");
    }

    [Fact]
    public void Capabilities_list_every_implemented_command()
    {
        Collector().Capabilities().SupportedCommands.Should().BeEquivalentTo(NodeCommandCatalog.All);
    }

    [Fact]
    public void Privileged_mode_is_reported_as_off_unless_the_host_flag_is_set()
    {
        Collector().Capabilities().PrivilegedModeEnabled.Should().BeFalse();

        _agent.Options.Security.AllowPrivilegedWorkloads = true;
        Collector().Capabilities().PrivilegedModeEnabled.Should().BeTrue();
    }

    [Theory]
    [InlineData("amd64", true)]
    [InlineData("arm64", true)]
    [InlineData("riscv64", false)]
    [InlineData("x86", false)]
    public void Only_amd64_and_arm64_can_run_workloads(string architecture, bool supported)
    {
        _host.Architecture = architecture;
        Collector().ArchitectureIsSupported().Should().Be(supported);
    }

    [Fact]
    public void Architecture_names_are_normalised_to_the_contract_spelling()
    {
        HostFacts.NormaliseArchitecture(System.Runtime.InteropServices.Architecture.X64).Should().Be("amd64");
        HostFacts.NormaliseArchitecture(System.Runtime.InteropServices.Architecture.Arm64).Should().Be("arm64");
    }

    public void Dispose() => _agent.Dispose();
}

/// <summary>The configuration checks that must fail loudly rather than half-work.</summary>
public class OptionsValidationTests
{
    private static NodeAgentOptions Valid() => new()
    {
        ControlPlaneUrl = "https://panel.example.com",
        NodeName = "node-1",
    };

    [Fact]
    public void A_sound_configuration_has_no_problems() => Valid().Validate().Should().BeEmpty();

    [Fact]
    public void A_plain_http_control_plane_is_refused_by_default()
    {
        var options = Valid();
        options.ControlPlaneUrl = "http://panel.example.com";

        options.Validate().Should().ContainMatch("*must be https*",
            "the enrollment token travels on that connection");
    }

    [Fact]
    public void Plain_http_is_allowed_only_with_the_explicit_development_switch()
    {
        var options = Valid();
        options.ControlPlaneUrl = "http://localhost:5000";
        options.Security.AllowInsecureControlPlane = true;

        options.Validate().Should().BeEmpty();
    }

    [Fact]
    public void A_non_loopback_metrics_bind_address_is_refused()
    {
        // The one socket the agent listens on must stay off the network, and a typo must not be
        // able to quietly break the "installing a node opens no inbound port" promise.
        var options = Valid();
        options.Metrics.BindAddress = "0.0.0.0";

        options.Validate().Should().ContainMatch("*not loopback*");
    }

    [Fact]
    public void Privileged_ports_are_not_the_agents_to_hand_out()
    {
        var options = Valid();
        options.Ports.Start = 80;

        options.Validate().Should().ContainMatch("*1024 or above*");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    public void An_absurd_heartbeat_interval_is_refused(int seconds)
    {
        var options = Valid();
        options.HeartbeatIntervalSeconds = seconds;

        options.Validate().Should().ContainMatch("*HeartbeatIntervalSeconds*");
    }

    [Fact]
    public void Data_directories_derive_from_the_data_root()
    {
        var options = Valid();
        options.DataDirectory = "/var/lib/harbora-node";

        options.IdentityDirectory.Should().Contain("identity");
        options.StateDirectory.Should().Contain("state");
        options.SnapshotDirectory.Should().Contain("snapshots");
        options.AuditLogPath.Should().Contain("audit");
    }
}
