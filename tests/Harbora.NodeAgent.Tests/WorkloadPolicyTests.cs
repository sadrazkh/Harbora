using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Sections 7, 8 and 11: what a control plane is and is not allowed to ask a node to run.
/// This is the file that decides what a compromised or coerced panel can do to a customer's
/// server, so it is tested as a pure function with no daemon anywhere near it.
/// </summary>
public class WorkloadPolicyTests
{
    private static WorkloadPolicy Policy(Action<SecurityOptions>? configure = null)
    {
        var security = new SecurityOptions();
        configure?.Invoke(security);
        return new WorkloadPolicy(security, new PortAllocationOptions());
    }

    private static IReadOnlyList<PolicyViolation> Check(
        WorkloadSpec spec, WorkloadPolicy? policy = null, string architecture = "amd64", bool admin = false) =>
        (policy ?? Policy()).Validate(spec, architecture, "9.9.9", admin);

    [Fact]
    public void A_sound_specification_passes()
    {
        Check(TestFactories.Workload()).Should().BeEmpty();
    }

    // --- image pinning ---

    [Fact]
    public void An_unpinned_image_is_refused()
    {
        var spec = TestFactories.Workload(digest: "latest");

        Check(spec).Should().ContainSingle()
            .Which.Code.Should().Be(NodeErrorCode.ImageNotPinned);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sha256:short")]
    [InlineData("sha1:0000000000000000000000000000000000000000")]
    [InlineData("sha256:ZZZZ4d2e8b1c0a9f8e7d6c5b4a3928170615243342516071829304a5b6c7d8e9f")]
    public void Only_a_real_sha256_digest_counts_as_pinned(string digest)
    {
        Check(TestFactories.Workload(digest: digest))
            .Should().Contain(v => v.Code == NodeErrorCode.ImageNotPinned);
    }

    // --- the host bind mount that would arrive dressed as a volume name ---

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("../../var/run")]
    [InlineData("data:/host")]
    public void A_volume_name_that_is_really_a_host_path_is_refused(string name)
    {
        // Docker's bind syntax is "source:target", so a volume named /var/run/docker.sock becomes a
        // bind mount of the Docker socket — the whole machine, handed over through a label-shaped
        // field. This is the single most important check in the file.
        var spec = TestFactories.Workload(container: c =>
        {
            c.Mounts.Clear();
            c.Mounts.Add(new MountSpec { VolumeName = name, MountPath = "/data" });
        }) with
        {
            Volumes = [new VolumeSpec { Name = name }],
        };

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void Mounting_a_volume_the_spec_does_not_declare_is_refused()
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Mounts.Clear();
            c.Mounts.Add(new MountSpec { VolumeName = "someone-elses-volume", MountPath = "/data" });
        });

        Check(spec).Should().Contain(v => v.Message.Contains("does not declare"));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/etc")]
    [InlineData("/proc/self")]
    [InlineData("/sys")]
    [InlineData("/dev")]
    [InlineData("/var/../etc")]
    public void Mounting_over_a_protected_container_path_is_refused(string mountPath)
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Mounts.Clear();
            c.Mounts.Add(new MountSpec { VolumeName = "test-app-data", MountPath = mountPath });
        });

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void A_relative_mount_path_is_refused()
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Mounts.Clear();
            c.Mounts.Add(new MountSpec { VolumeName = "test-app-data", MountPath = "data" });
        });

        Check(spec).Should().Contain(v => v.Message.Contains("must be absolute"));
    }

    // --- privilege ---

    [Fact]
    public void Privileged_mode_is_refused_when_the_host_flag_is_off()
    {
        var spec = TestFactories.Workload(container: c => c.Privileged = true);

        Check(spec, admin: true).Should().Contain(v =>
            v.Code == NodeErrorCode.PolicyDenied && v.Message.Contains("AllowPrivilegedWorkloads"));
    }

    [Fact]
    public void Privileged_mode_is_refused_without_node_admin_even_when_the_host_flag_is_on()
    {
        // Two locks, one key each. Either alone would let a tenant workload reach it.
        var policy = Policy(s => s.AllowPrivilegedWorkloads = true);
        var spec = TestFactories.Workload(container: c => c.Privileged = true);

        Check(spec, policy, admin: false).Should().Contain(v => v.Code == NodeErrorCode.Unauthorized);
    }

    [Fact]
    public void Privileged_mode_is_allowed_only_with_both_the_host_flag_and_node_admin()
    {
        var policy = Policy(s => s.AllowPrivilegedWorkloads = true);
        var spec = TestFactories.Workload(container: c => c.Privileged = true);

        Check(spec, policy, admin: true).Should().BeEmpty();
    }

    [Fact]
    public void Host_networking_and_the_host_pid_namespace_are_treated_as_privilege()
    {
        Check(TestFactories.Workload(container: c => c.HostNetwork = true), admin: true)
            .Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);

        Check(TestFactories.Workload(container: c => c.HostPidNamespace = true), admin: true)
            .Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Theory]
    [InlineData("SYS_ADMIN")]
    [InlineData("sys_admin")]
    [InlineData("SYS_PTRACE")]
    [InlineData("NET_ADMIN")]
    public void Dangerous_capabilities_are_refused(string capability)
    {
        var spec = TestFactories.Workload(container: c => c.CapabilitiesAdd.Add(capability));

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void A_harmless_capability_is_allowed()
    {
        var spec = TestFactories.Workload(container: c => c.CapabilitiesAdd.Add("NET_BIND_SERVICE"));

        Check(spec).Should().BeEmpty();
    }

    // --- secrets ---

    [Fact]
    public void A_file_mounted_secret_needs_an_absolute_path()
    {
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "tls-key", Value = "material", MountAs = SecretMount.File, TargetPath = "certs/key.pem",
        }));

        Check(spec).Should().Contain(v => v.Message.Contains("absolute targetPath"));
    }

    [Fact]
    public void A_secret_cannot_be_written_over_a_protected_path()
    {
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "shadow", Value = "material", MountAs = SecretMount.File, TargetPath = "/etc/passwd",
        }));

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied);
    }

    [Fact]
    public void An_environment_secret_needs_no_path()
    {
        var spec = TestFactories.Workload(container: c => c.Secrets.Add(new SecretSpec
        {
            Name = "DB_PASSWORD", Value = "correct-horse-battery-staple",
        }));

        Check(spec).Should().BeEmpty();
    }

    // --- resources ---

    [Fact]
    public void A_tenant_container_must_declare_a_memory_limit()
    {
        // One tenant's memory leak taking down every other tenant on the box is not an outage
        // anyone should have to explain twice.
        var spec = TestFactories.Workload();
        var unlimited = spec with
        {
            Containers = [spec.Containers[0] with { Resources = new ResourceLimits { CpuCores = 1 } }],
        };

        Check(unlimited).Should().Contain(v => v.Message.Contains("memory limit"));
    }

    [Fact]
    public void An_admin_workload_may_run_without_a_memory_limit()
    {
        var spec = TestFactories.Workload();
        var unlimited = spec with
        {
            Containers = [spec.Containers[0] with { Resources = new ResourceLimits { CpuCores = 1 } }],
        };

        Check(unlimited, admin: true).Should().BeEmpty();
    }

    // --- placement ---

    [Fact]
    public void A_workload_for_another_architecture_is_refused()
    {
        var spec = TestFactories.Workload() with { SupportedArchitectures = ["arm64"] };

        Check(spec, architecture: "amd64").Should().Contain(v => v.Code == NodeErrorCode.UnsupportedArchitecture);
    }

    [Fact]
    public void A_workload_needing_a_newer_agent_is_refused()
    {
        var spec = TestFactories.Workload() with { MinimumAgentVersion = "99.0.0" };

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.AgentTooOld);
    }

    // --- ports ---

    [Fact]
    public void A_host_port_outside_the_allocation_range_is_refused()
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 22, HostPort = 22, PublishToHost = true });
        });

        Check(spec).Should().Contain(v => v.Code == NodeErrorCode.PolicyDenied && v.Message.Contains("allocation range"));
    }

    [Fact]
    public void A_host_port_inside_the_range_is_allowed()
    {
        var spec = TestFactories.Workload(container: c =>
        {
            c.Ports.Clear();
            c.Ports.Add(new PortMapping { ContainerPort = 8080, HostPort = 30_100, PublishToHost = true });
        });

        Check(spec).Should().BeEmpty();
    }

    // --- names ---

    [Theory]
    [InlineData("Test-App")]
    [InlineData("app_name")]
    [InlineData("-leading")]
    [InlineData("")]
    public void A_workload_name_must_be_a_dns_label(string name)
    {
        Check(TestFactories.Workload() with { Name = name })
            .Should().Contain(v => v.Message.Contains("DNS label"));
    }

    [Fact]
    public void Duplicate_container_names_are_refused()
    {
        var spec = TestFactories.Workload();
        var duplicated = spec with { Containers = [spec.Containers[0], spec.Containers[0]] };

        Check(duplicated).Should().Contain(v => v.Message.Contains("unique"));
    }

    [Fact]
    public void A_route_targeting_a_container_the_workload_lacks_is_refused()
    {
        var spec = TestFactories.Workload() with
        {
            HttpRoutes =
            [
                new HttpRouteSpec { RouteId = "r1", Domain = "app.test", TargetContainer = "ghost", TargetPort = 80 },
            ],
        };

        Check(spec).Should().Contain(v => v.Message.Contains("does not define"));
    }

    [Fact]
    public void Every_violation_is_reported_not_just_the_first()
    {
        // An operator fixing a template should see the whole list, not play whack-a-mole one
        // deploy attempt at a time.
        var spec = TestFactories.Workload(digest: "latest", container: c =>
        {
            c.Privileged = true;
            c.CapabilitiesAdd.Add("SYS_ADMIN");
        }) with { Name = "BAD NAME" };

        Check(spec).Should().HaveCountGreaterThan(3);
    }

    // --- path normalisation, which every deny-list above depends on ---

    [Theory]
    [InlineData("/var/run/../run/docker.sock", "/var/run/docker.sock")]
    [InlineData("/etc/./passwd", "/etc/passwd")]
    [InlineData("//var//run//", "/var/run")]
    [InlineData("/a/b/../../c", "/c")]
    [InlineData("/../..", "/")]
    [InlineData("", "/")]
    public void Paths_are_normalised_before_they_are_compared(string input, string expected)
    {
        // Comparing raw text would let any deny-list be walked around with three extra characters.
        WorkloadPolicy.NormalisePath(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("/var/run/docker.sock")]
    [InlineData("/var/run/../run/docker.sock")]
    [InlineData("/etc/shadow")]
    [InlineData("/proc/1/environ")]
    public void The_host_path_deny_list_survives_traversal(string path)
    {
        Policy().IsDeniedHostPath(path).Should().BeTrue();
    }
}
