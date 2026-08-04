using System.Text.Json;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Reads the published example instances with the C# contract types. A schema and a record set can
/// both be internally consistent and still disagree about what a real message looks like; the
/// examples are the third party that catches it.
/// </summary>
public class ContractExampleTests
{
    private static string Example(string name) =>
        File.ReadAllText(Path.Combine(RepoPaths.ContractV1, "examples", name));

    [Fact]
    public void Deploy_command_example_deserializes_completely()
    {
        var envelope = NodeContract.Deserialize<CommandEnvelope>(Example("command-envelope.deploy-workload.json"));

        envelope.Should().NotBeNull();
        envelope!.Command.Should().Be(NodeCommands.DeployWorkload);
        envelope.RequiredScope.Should().Be(NodeScopes.WorkloadsWrite);
        envelope.IdempotencyKey.Should().NotBeNullOrWhiteSpace();
        envelope.Nonce.Should().NotBeNullOrWhiteSpace();
        envelope.Audit!.TenantId.Should().Be("ws-acme");

        var request = envelope.PayloadAs<DeployWorkloadRequest>();
        request.Should().NotBeNull();

        var spec = request!.Spec;
        spec.Name.Should().Be("acme-api");
        spec.Containers.Should().ContainSingle();

        var container = spec.Containers[0];
        container.Image.Digest.Should().StartWith("sha256:");
        container.Image.PullReference.Should().Be($"{container.Image.Repository}@{container.Image.Digest}");
        container.HealthCheck!.Kind.Should().Be(HealthCheckKind.Http);
        container.RestartPolicy.Mode.Should().Be(RestartMode.UnlessStopped);
        container.Resources.PidsLimit.Should().Be(512);
        container.Privileged.Should().BeFalse();
        container.Secrets.Should().ContainSingle();

        spec.Upgrade.Mode.Should().Be(UpgradeMode.BlueGreen);
        spec.Upgrade.AutoRollbackOnFailure.Should().BeTrue();
        spec.HttpRoutes.Should().ContainSingle();
    }

    [Fact]
    public void App_manifest_example_deserializes_and_resolves_per_architecture_digests()
    {
        var manifest = NodeContract.Deserialize<AppManifest>(Example("app-manifest.postgres.json"));

        manifest.Should().NotBeNull();
        manifest!.AppId.Should().Be("postgresql");
        manifest.ApplicationVersion.Should().Be("16.4");
        manifest.SupportedArchitectures.Should().BeEquivalentTo(["amd64", "arm64"]);

        var image = manifest.Images.Single();
        image.For("amd64")!.Digest.Should().StartWith("sha256:");
        image.For("arm64")!.Digest.Should().StartWith("sha256:");
        image.For("riscv64").Should().BeNull("an unlisted architecture must resolve to nothing, not to a tag");

        manifest.SecretSchema.Single().GenerateLength.Should().Be(32);
        manifest.Backup!.DatabaseEngine.Should().Be(DatabaseEngines.PostgreSql);
        manifest.Restore!.StopBeforeRestore.Should().BeTrue();
        manifest.Upgrade.MigrationNotes.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Temporary_grant_example_deserializes()
    {
        var grant = NodeContract.Deserialize<DatabaseAccessGrantSpec>(Example("database-access-grant.temporary.json"));

        grant.Should().NotBeNull();
        grant!.Mode.Should().Be(DatabaseAccessMode.Temporary);
        grant.TtlSeconds.Should().Be(3600);
        grant.IpAllowlist.Should().ContainSingle().Which.Should().Be("203.0.113.44/32");
        grant.ReadOnly.Should().BeTrue();
        grant.OperatorConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Unknown_fields_from_a_newer_control_plane_are_ignored()
    {
        // Forward compatibility is a promise in the contract README; this is the promise under test.
        const string json = """
        {
          "workloadId": "wl-1", "tenantId": "t-1",
          "somethingInventedLater": { "nested": [1, 2, 3] }
        }
        """;

        var request = NodeContract.Deserialize<WorkloadRequest>(json);

        request.Should().NotBeNull();
        request!.WorkloadId.Should().Be("wl-1");
    }

    [Fact]
    public void Secret_values_never_appear_in_a_record_s_string_form()
    {
        // Records generate a ToString that prints every property. For anything holding secret
        // material that default is a leak waiting for the first interpolated log line.
        var secret = new SecretSpec { Name = "DB_PASSWORD", Value = "hunter2-super-secret" };

        secret.ToString().Should().NotContain("hunter2");
        secret.ToString().Should().Contain("***");
    }

    [Fact]
    public void Grant_state_string_form_excludes_the_password()
    {
        var state = new DatabaseAccessGrantState
        {
            GrantId = "gr-1",
            State = DatabaseAccessState.Active,
            Engine = DatabaseEngines.PostgreSql,
            Mode = DatabaseAccessMode.Temporary,
            Username = "harbora_tmp_ab12",
            Password = "correct-horse-battery-staple",
            Endpoint = "db-gw.harbora.io:41823",
        };

        state.ToString().Should().NotContain("correct-horse");
        state.ToString().Should().Contain("harbora_tmp_ab12");
    }

    [Fact]
    public void Frames_round_trip_through_the_shared_serializer()
    {
        var frame = ControlFrame.Create(NodeFrames.Heartbeat, new NodeHeartbeat
        {
            NodeId = "node-1",
            AgentVersion = "0.2.0",
            Health = NodeHealthState.Degraded,
            RunningWorkloads = 3,
        }, sequence: 42, correlationId: "corr-1");

        var json = NodeContract.Serialize(frame);
        json.Should().Contain("\"degraded\"", "enums travel as camelCase strings, not integers");

        var round = NodeContract.Deserialize<ControlFrame>(json)!;
        round.Type.Should().Be(NodeFrames.Heartbeat);
        round.Sequence.Should().Be(42);
        round.CorrelationId.Should().Be("corr-1");
        round.V.Should().Be(NodeContract.ProtocolVersion);

        var payload = round.PayloadAs<NodeHeartbeat>()!;
        payload.Health.Should().Be(NodeHealthState.Degraded);
        payload.RunningWorkloads.Should().Be(3);
    }

    [Fact]
    public void Null_payload_reads_as_default_rather_than_throwing()
    {
        var frame = NodeContract.Deserialize<ControlFrame>(
            """{"v":1,"type":"control.ping","id":"a"}""")!;

        frame.PayloadAs<NodeHeartbeat>().Should().BeNull();
    }

    [Fact]
    public void Every_example_file_is_valid_json()
    {
        var directory = Path.Combine(RepoPaths.ContractV1, "examples");
        var files = Directory.GetFiles(directory, "*.json");

        files.Should().NotBeEmpty();

        foreach (var file in files)
        {
            var act = () => JsonDocument.Parse(File.ReadAllText(file));
            act.Should().NotThrow($"{Path.GetFileName(file)} is published as a valid instance");
        }
    }
}
