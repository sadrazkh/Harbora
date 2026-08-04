using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Updates;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 5: silent and manual update, rollback on failure, version reporting, and draining a node
/// before maintenance.
/// </summary>
public sealed class UpdateAndDrainTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly FakeContainerRuntime _runtime = new();
    private readonly FakeHostFacts _host = new();
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly List<NodeEvent> _events = [];
    private readonly WorkloadRegistry _registry;
    private readonly FakeDownloader _downloader = new();
    private readonly FakeServiceController _service;

    public UpdateAndDrainTests()
    {
        _registry = new WorkloadRegistry(TestFactories.Store<WorkloadRegistryState>(_agent, "workloads.json"));
        _service = new FakeServiceController(Path.Combine(_agent.Root, "harbora-node-agent"));

        File.WriteAllText(_service.ExecutablePath, "the currently running binary");
    }

    private JsonFileStore<PendingUpdate> Pending() => TestFactories.Store<PendingUpdate>(_agent, "pending-update.json");
    private JsonFileStore<NodeState> State() => TestFactories.Store<NodeState>(_agent, "node.json");

    private WorkloadDeployer Deployer() => new(
        _agent.Wrapped, _runtime, _registry, new PortAllocator(_agent.Options.Ports),
        new HealthProbe(_runtime, TimeProvider.System, TestFactories.Log<HealthProbe>()),
        _host, new SecretRedactor(), new NodeMetrics(_clock), new Events(_events),
        TestFactories.Workspaces(_agent), _clock, TestFactories.Log<WorkloadDeployer>());

    private DrainCoordinator Drain() => new(
        State(), _registry, Deployer(), TestFactories.Audit(_agent), new NodeMetrics(_clock),
        new Events(_events), _clock, TestFactories.Log<DrainCoordinator>());

    private AgentUpdater Updater() => new(
        _agent.Wrapped, Pending(), State(), _downloader, _service, Drain(),
        TestFactories.Audit(_agent), new NodeMetrics(_clock), new Events(_events),
        _clock, TestFactories.Log<AgentUpdater>());

    private static AgentUpdateRequest Request(string version, string sha256, string? url = null) => new()
    {
        TargetVersion = version,
        DownloadUrl = url ?? "https://releases.harbora.test/harbora-node-agent-linux-x64",
        Sha256 = sha256,
        DrainFirst = false,
        VerifyTimeoutSeconds = 30,
    };

    private static string Sha256Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));

    // --- verification ---

    [Fact]
    public async Task An_artifact_whose_checksum_does_not_match_is_never_installed()
    {
        // An unverified binary downloaded and then executed as root is the worst thing an update
        // path can do.
        _downloader.Content = "a completely different binary";

        var result = await Updater().ApplyAsync(
            Request("99.0.0", Sha256Of("what we expected")), null, CancellationToken.None);

        result.Outcome.Should().Be(AgentUpdateOutcome.Failed);
        result.Error!.Code.Should().Be(NodeErrorCode.UpdateVerificationFailed);

        (await File.ReadAllTextAsync(_service.ExecutablePath)).Should().Be("the currently running binary");
        _service.Restarts.Should().Be(0);
        Pending().Load().Should().BeNull();
    }

    [Fact]
    public async Task A_plain_http_download_url_is_refused()
    {
        var result = await Updater().ApplyAsync(
            Request("99.0.0", Sha256Of("x"), "http://releases.harbora.test/agent"), null, CancellationToken.None);

        result.Outcome.Should().Be(AgentUpdateOutcome.Failed);
        result.Error!.Code.Should().Be(NodeErrorCode.UpdateVerificationFailed);
        _downloader.Requests.Should().BeEmpty("nothing should be fetched from a URL we already refused");
    }

    [Fact]
    public async Task Updating_to_the_version_already_running_does_nothing()
    {
        var result = await Updater().ApplyAsync(
            Request(AgentVersion.Current, Sha256Of("x")), null, CancellationToken.None);

        result.Outcome.Should().Be(AgentUpdateOutcome.AlreadyCurrent);
        _downloader.Requests.Should().BeEmpty();
    }

    // --- applying ---

    [Fact]
    public async Task A_verified_artifact_replaces_the_binary_and_restarts_the_service()
    {
        if (!OperatingSystem.IsLinux()) return; // The self-update path is Linux-only by design.

        _downloader.Content = "the new binary";

        var result = await Updater().ApplyAsync(
            Request("99.0.0", Sha256Of("the new binary")), null, CancellationToken.None);

        result.Outcome.Should().Be(AgentUpdateOutcome.Updated);
        (await File.ReadAllTextAsync(_service.ExecutablePath)).Should().Be("the new binary");

        var pending = Pending().Load()!;
        pending.TargetVersion.Should().Be("99.0.0");
        pending.PreviousVersion.Should().Be(AgentVersion.Current);
        File.Exists(pending.PreviousBinaryPath).Should().BeTrue("a rollback needs something to roll back to");
    }

    [Fact]
    public void The_marker_is_written_before_the_swap()
    {
        // A crash between the two leaves a marker with no swap, which the next start resolves
        // harmlessly. The reverse would leave a broken binary and nothing to restore.
        var source = File.ReadAllText(
            Path.Combine(RepoPaths.Root, "src", "Harbora.NodeAgent", "Updates", "AgentUpdater.cs"));

        var markerAt = source.IndexOf("pending.Save(new PendingUpdate", StringComparison.Ordinal);
        var swapAt = source.IndexOf("File.Move(staged, service.ExecutablePath", StringComparison.Ordinal);

        markerAt.Should().BeGreaterThan(0);
        swapAt.Should().BeGreaterThan(markerAt);
    }

    // --- completing and rolling back ---

    [Fact]
    public async Task An_update_that_produced_the_expected_version_is_completed()
    {
        Pending().Save(new PendingUpdate
        {
            TargetVersion = AgentVersion.Current,
            PreviousVersion = "0.0.1",
            PreviousBinaryPath = Path.Combine(_agent.Root, "previous"),
            ExecutablePath = _service.ExecutablePath,
            StartedAt = _clock.GetUtcNow(),
            VerifyTimeoutSeconds = 30,
        });

        File.WriteAllText(Path.Combine(_agent.Root, "previous"), "old binary");

        var outcome = await Updater().CompletePendingAsync(CancellationToken.None);

        outcome!.Outcome.Should().Be(AgentUpdateOutcome.Updated);
        Pending().Load().Should().BeNull("the marker is cleared once the outcome is known");
        State().Load()!.PreviousAgentVersion.Should().Be("0.0.1");
        _events.Should().Contain(e => e.Kind == NodeEventKinds.AgentUpdateCompleted);
    }

    [Fact]
    public async Task An_update_that_came_back_as_the_wrong_version_is_rolled_back()
    {
        var previous = Path.Combine(_agent.Root, "previous");
        File.WriteAllText(previous, "the previous binary");

        Pending().Save(new PendingUpdate
        {
            TargetVersion = "99.99.99",
            PreviousVersion = "0.0.1",
            PreviousBinaryPath = previous,
            ExecutablePath = _service.ExecutablePath,
            StartedAt = _clock.GetUtcNow(),
            VerifyTimeoutSeconds = 30,
        });

        var outcome = await Updater().CompletePendingAsync(CancellationToken.None);

        outcome!.Outcome.Should().Be(AgentUpdateOutcome.RolledBack);
        (await File.ReadAllTextAsync(_service.ExecutablePath)).Should().Be("the previous binary");
        Pending().Load().Should().BeNull();
        _events.Should().Contain(e => e.Kind == NodeEventKinds.AgentUpdateRolledBack);
    }

    [Fact]
    public async Task A_rollback_with_no_previous_binary_reports_failure_rather_than_pretending()
    {
        Pending().Save(new PendingUpdate
        {
            TargetVersion = "99.99.99",
            PreviousVersion = "0.0.1",
            PreviousBinaryPath = Path.Combine(_agent.Root, "does-not-exist"),
            ExecutablePath = _service.ExecutablePath,
            StartedAt = _clock.GetUtcNow(),
            VerifyTimeoutSeconds = 30,
        });

        var outcome = await Updater().CompletePendingAsync(CancellationToken.None);

        outcome!.Outcome.Should().Be(AgentUpdateOutcome.Failed);
        outcome.Error!.Code.Should().Be(NodeErrorCode.UpdateApplyFailed);
    }

    [Fact]
    public async Task With_no_update_in_flight_there_is_nothing_to_complete()
    {
        (await Updater().CompletePendingAsync(CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Completing_an_update_lifts_the_drain_it_started_with()
    {
        await Drain().DrainAsync(false, TimeSpan.FromSeconds(5), "agent update", CancellationToken.None);

        Pending().Save(new PendingUpdate
        {
            TargetVersion = AgentVersion.Current,
            PreviousVersion = "0.0.1",
            PreviousBinaryPath = Path.Combine(_agent.Root, "previous"),
            ExecutablePath = _service.ExecutablePath,
            StartedAt = _clock.GetUtcNow(),
            VerifyTimeoutSeconds = 30,
        });

        await Updater().CompletePendingAsync(CancellationToken.None);

        Drain().IsDraining.Should().BeFalse("a node left draining after a successful update takes no work");
    }

    // --- draining ---

    [Fact]
    public async Task Draining_sets_a_flag_that_survives_a_restart()
    {
        // A node that forgot it was draining would accept the very deploy the operator drained it
        // to avoid — and a reboot is the most likely thing to happen next.
        await Drain().DrainAsync(false, TimeSpan.FromSeconds(5), "kernel upgrade", CancellationToken.None);

        var afterRestart = TestFactories.Store<NodeState>(_agent, "node.json").Load()!;

        afterRestart.Draining.Should().BeTrue();
        afterRestart.DrainReason.Should().Be("kernel upgrade");
    }

    [Fact]
    public async Task Draining_without_stopping_leaves_workloads_running()
    {
        await SeedWorkloadAsync();

        var result = await Drain().DrainAsync(false, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        result.Draining.Should().BeTrue();
        result.WorkloadsStopped.Should().Be(0);
        result.WorkloadsRemaining.Should().Be(1);
        _runtime.Containers.Values.Should().OnlyContain(c => c.State == "running");
    }

    [Fact]
    public async Task Draining_with_stop_stops_them()
    {
        await SeedWorkloadAsync();

        var result = await Drain().DrainAsync(true, TimeSpan.FromSeconds(30), "maintenance", CancellationToken.None);

        result.WorkloadsStopped.Should().Be(1);
        result.WorkloadsRemaining.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        _runtime.Containers.Values.Should().OnlyContain(c => c.State == "exited");
    }

    [Fact]
    public async Task A_drain_that_runs_out_of_time_says_so()
    {
        await SeedWorkloadAsync();

        var result = await Drain().DrainAsync(true, TimeSpan.Zero, null, CancellationToken.None);

        result.TimedOut.Should().BeTrue("an operator about to reboot needs to know what is still up");
        result.WorkloadsRemaining.Should().Be(1);
    }

    [Fact]
    public async Task Undraining_puts_the_node_back_in_service()
    {
        await Drain().DrainAsync(false, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var result = await Drain().UndrainAsync(CancellationToken.None);

        result.Draining.Should().BeFalse();
        Drain().IsDraining.Should().BeFalse();
    }

    [Fact]
    public async Task Undraining_a_node_that_is_not_draining_is_harmless()
    {
        (await Drain().UndrainAsync(CancellationToken.None)).Draining.Should().BeFalse();
    }

    [Fact]
    public async Task Draining_is_audited()
    {
        var audit = TestFactories.Audit(_agent);

        var coordinator = new DrainCoordinator(
            State(), _registry, Deployer(), audit, new NodeMetrics(_clock),
            new Events(_events), _clock, TestFactories.Log<DrainCoordinator>());

        await coordinator.DrainAsync(false, TimeSpan.FromSeconds(5), "kernel upgrade", CancellationToken.None);

        audit.Read().Should().Contain(e => e.Action == "node.drain" && e.Reason == "kernel upgrade");
    }

    private async Task SeedWorkloadAsync()
    {
        var spec = TestFactories.Workload();

        await Deployer().DeployAsync(
            new Harbora.NodeAgent.Commands.CommandContext(
                TestFactories.Envelope(NodeCommands.DeployWorkload), new RecordingResponder(), _clock.GetUtcNow()),
            new DeployWorkloadRequest { Spec = spec }, false, CancellationToken.None);
    }

    public void Dispose() => _agent.Dispose();

    private sealed class Events(List<NodeEvent> sink) : INodeEventPublisher
    {
        public Task PublishAsync(NodeEvent nodeEvent, CancellationToken ct)
        {
            lock (sink) sink.Add(nodeEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDownloader : IUpdateDownloader
    {
        public List<string> Requests { get; } = [];
        public string Content { get; set; } = "the new binary";

        public async Task DownloadAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken ct)
        {
            Requests.Add(url);
            await File.WriteAllTextAsync(destinationPath, Content, ct);
        }
    }

    private sealed class FakeServiceController(string executablePath) : IServiceController
    {
        public string ExecutablePath { get; } = executablePath;
        public int Restarts { get; private set; }

        public Task RestartAsync(CancellationToken ct)
        {
            Restarts++;
            return Task.CompletedTask;
        }
    }
}
