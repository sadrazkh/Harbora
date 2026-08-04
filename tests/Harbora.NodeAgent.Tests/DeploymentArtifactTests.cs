using FluentAssertions;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// The shipped scripts and unit file are part of the product, and nothing else compiles them. These
/// check the properties that would otherwise only be discovered on a customer's server.
/// </summary>
public class DeploymentArtifactTests
{
    private static string Read(string name) =>
        File.ReadAllText(Path.Combine(RepoPaths.DeployNodeAgent, name));

    private static bool Exists(string name) =>
        File.Exists(Path.Combine(RepoPaths.DeployNodeAgent, name));

    [Theory]
    [InlineData("install.sh")]
    [InlineData("uninstall.sh")]
    [InlineData("build-release.sh")]
    [InlineData("harbora-node-agent.service")]
    [InlineData("README.md")]
    public void Every_shipped_artifact_is_present(string name) => Exists(name).Should().BeTrue();

    [Theory]
    [InlineData("install.sh")]
    [InlineData("uninstall.sh")]
    [InlineData("build-release.sh")]
    public void Scripts_fail_fast(string name)
    {
        var script = Read(name);

        script.Should().StartWith("#!/usr/bin/env bash");
        script.Should().Contain("set -euo pipefail",
            "an installer that carries on after a failed step leaves a half-installed node");
    }

    // --- installer ---

    [Fact]
    public void The_installer_refuses_a_plain_http_control_plane()
    {
        // The enrollment token travels on that connection.
        Read("install.sh").Should().Contain("The control plane URL must be https");
    }

    [Fact]
    public void The_installer_requires_the_parameters_the_brief_names()
    {
        var script = Read("install.sh");

        foreach (var option in new[] { "--control-plane", "--token", "--name", "--labels", "--region", "--environment" })
            script.Should().Contain(option);
    }

    [Fact]
    public void The_installer_writes_the_token_to_its_own_file_with_restrictive_permissions()
    {
        var script = Read("install.sh");

        script.Should().Contain("enrollment.token");
        script.Should().Contain("chmod 0600 \"$token_file\"");

        // Keeping the token out of agent.conf means the file an operator edits later never
        // contained a credential.
        script.Should().NotContain("\"EnrollmentToken\":");
    }

    [Fact]
    public void The_installer_verifies_the_downloaded_binary_or_says_it_did_not()
    {
        var script = Read("install.sh");

        script.Should().Contain("sha256sum");
        script.Should().Contain("Checksum mismatch");
        script.Should().Contain("the binary was not verified",
            "silently skipping the check would make the check meaningless");
    }

    [Fact]
    public void The_installer_refuses_an_unsupported_architecture()
    {
        Read("install.sh").Should().Contain("Unsupported architecture");
    }

    [Fact]
    public void Privilege_switches_default_to_off_in_the_generated_configuration()
    {
        var script = Read("install.sh");

        script.Should().Contain("ALLOW_PRIVILEGED=\"false\"");
        script.Should().Contain("ALLOW_WORKSPACE=\"false\"");
        script.Should().Contain("Privileged workloads are ENABLED",
            "a node with privileged mode on should say so during the install, not only in a file");
    }

    // --- uninstaller ---

    [Fact]
    public void The_uninstaller_keeps_workloads_and_data_by_default()
    {
        var script = Read("uninstall.sh");

        script.Should().Contain("STOP_WORKLOADS=0");
        script.Should().Contain("PURGE_WORKLOADS=0");
        script.Should().Contain("PURGE_DATA=0");
        script.Should().Contain("PURGE_VOLUMES=0");
    }

    [Fact]
    public void Deleting_volumes_requires_a_typed_confirmation()
    {
        // The only irreversible step in the script.
        var script = Read("uninstall.sh");

        script.Should().Contain("--purge-volumes");
        script.Should().Contain("There is no undo");
        script.Should().Contain("Type 'yes' to continue");
    }

    [Fact]
    public void The_uninstaller_shreds_the_private_key_before_deleting_it()
    {
        Read("uninstall.sh").Should().Contain("shred -u");
    }

    [Fact]
    public void The_uninstaller_only_touches_containers_the_agent_manages()
    {
        // Without the label filter, "clean up" would be a cleanup of the customer's host.
        Read("uninstall.sh").Should().Contain("io.harbora.managed=true");
    }

    // --- systemd unit ---

    [Theory]
    [InlineData("NoNewPrivileges=true")]
    [InlineData("ProtectSystem=strict")]
    [InlineData("ProtectHome=true")]
    [InlineData("PrivateTmp=true")]
    [InlineData("ProtectKernelModules=true")]
    [InlineData("RestrictSUIDSGID=true")]
    [InlineData("LockPersonality=true")]
    public void The_unit_confines_the_agent(string directive) =>
        Read("harbora-node-agent.service").Should().Contain(directive);

    [Fact]
    public void The_unit_restarts_the_agent_but_not_forever_in_a_tight_loop()
    {
        var unit = Read("harbora-node-agent.service");

        unit.Should().Contain("Restart=always", "the agent is how the node is reachable at all");
        unit.Should().Contain("StartLimitBurst=", "a genuinely broken build must not spin the machine");
    }

    [Fact]
    public void The_unit_starts_after_docker_without_requiring_it()
    {
        var unit = Read("harbora-node-agent.service");

        unit.Should().Contain("After=network-online.target docker.service");

        // Refusing to start when Docker is down would look identical to the machine being off — and
        // reporting that Docker is down is the agent's job.
        unit.Should().NotContain("Requires=docker.service");
    }

    [Fact]
    public void The_unit_can_write_only_the_paths_the_agent_needs()
    {
        var unit = Read("harbora-node-agent.service");

        unit.Should().Contain("ReadWritePaths=/var/lib/harbora-node /etc/harbora-node /usr/local/bin");
    }

    [Fact]
    public void The_unit_gives_in_flight_commands_time_to_report_before_the_kill()
    {
        Read("harbora-node-agent.service").Should().Contain("TimeoutStopSec=");
    }

    [Fact]
    public void The_unit_does_not_claim_sd_notify_support_the_agent_does_not_have()
    {
        // A notify unit that never notifies hangs systemd until the start timeout rather than
        // failing visibly.
        Read("harbora-node-agent.service").Should().NotContain("Type=notify");
    }

    // --- release ---

    [Fact]
    public void The_release_build_produces_self_contained_binaries_for_both_architectures()
    {
        var script = Read("build-release.sh");

        script.Should().Contain("linux-x64");
        script.Should().Contain("linux-arm64");
        script.Should().Contain("--self-contained true", "a node must not need a .NET runtime installed");
        script.Should().Contain("PublishSingleFile=true");
    }

    [Fact]
    public void The_release_build_emits_a_checksum_beside_every_artifact()
    {
        Read("build-release.sh").Should().Contain("sha256sum");
    }

    [Fact]
    public void The_release_workflow_runs_the_tests_before_it_publishes()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepoPaths.Root, ".github", "workflows", "release-node-agent.yml"));

        workflow.Should().Contain("dotnet test");
        workflow.Should().Contain("harbora-node-agent-${{ matrix.rid }}.sha256");
    }

    // --- documentation ---

    [Theory]
    [InlineData("README.md")]
    [InlineData("installation.md")]
    [InlineData("security.md")]
    [InlineData("troubleshooting.md")]
    [InlineData("merge-notes.md")]
    public void The_documents_the_brief_asks_for_exist(string name)
    {
        var path = Path.Combine(RepoPaths.Root, "docs", "node-agent", name);

        File.Exists(path).Should().BeTrue();
        new FileInfo(path).Length.Should().BeGreaterThan(1000, "a placeholder is not a document");
    }

    [Fact]
    public void The_development_example_does_not_ship_a_real_secret()
    {
        var example = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "examples", "node-agent", "agent.development.json"));

        example.Should().Contain("localhost");
        example.Should().Contain("dev-enrollment-token");
        example.Should().Contain("\"AllowPrivilegedWorkloads\": false",
            "a dev run should exercise the same refusals a node does");
    }
}
