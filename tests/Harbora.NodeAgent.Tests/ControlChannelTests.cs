using System.Text.Json;
using FluentAssertions;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Harbora.NodeAgent.Transport;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 6: protocol negotiation, heartbeat plumbing, reconnect and resume, and the durable
/// outbox that makes "the panel eventually learns what happened" true across a restart.
/// </summary>
public sealed class ControlChannelTests : IDisposable
{
    private readonly TempAgent _agent = new();
    private readonly InMemoryTransportPair _pair = new();
    private readonly JsonFileStore<NodeState> _state;
    private readonly ChannelOutbox _outbox;
    private readonly ManualClock _clock = new(new DateTimeOffset(2026, 8, 4, 12, 0, 0, TimeSpan.Zero));
    private readonly TestCertificateAuthority _ca = new();

    public ControlChannelTests()
    {
        _state = TestFactories.Store<NodeState>(_agent, "node.json");
        _state.Save(TestFactories.EnrolledState());

        _outbox = new ChannelOutbox(
            TestFactories.Store<OutboxState>(_agent, "outbox.json"), TestFactories.Log<ChannelOutbox>());
    }

    private ControlChannel Channel() => new(
        _agent.Wrapped,
        new InMemoryTransportFactory(_pair),
        _outbox,
        _state,
        TestFactories.Inventory(_agent, new FakeHostFacts(), new FakeContainerRuntime()),
        _clock,
        TestFactories.Log<ControlChannel>());

    private Harbora.NodeAgent.Identity.NodeIdentity Identity()
    {
        var store = new Harbora.NodeAgent.Identity.NodeIdentityStore(_agent.Options.IdentityDirectory);
        var csr = store.CreateSigningRequest("test-node", newKey: true);
        store.StoreCertificate(_ca.Sign(csr, _clock.GetUtcNow(), _clock.GetUtcNow().AddDays(90)), _ca.CertificatePem);
        return store.Load()!;
    }

    /// <summary>Answer the node's hello the way a healthy control plane would.</summary>
    private void AcceptHandshake(
        string resumeToken = "resume-1",
        long lastReceivedSequence = 0,
        bool resumeRejected = false,
        int protocolVersion = NodeContract.ProtocolVersion,
        IReadOnlyList<string>? scopes = null)
    {
        _pair.PushToNode(ControlFrame.Create(ControlFrames.HelloAck, new ControlHelloAck
        {
            ProtocolVersion = protocolVersion,
            ResumeToken = resumeToken,
            ServerTime = _clock.GetUtcNow(),
            LastReceivedSequence = lastReceivedSequence,
            HeartbeatIntervalSeconds = 45,
            GrantedScopes = scopes ?? NodeScopes.Default,
            ResumeRejected = resumeRejected,
        }));
    }

    [Fact]
    public async Task Handshake_negotiates_and_stores_the_session_terms()
    {
        var channel = Channel();
        AcceptHandshake();

        var ack = await channel.OpenAsync(Identity(), CancellationToken.None);

        ack.ProtocolVersion.Should().Be(NodeContract.ProtocolVersion);
        channel.IsConnected.Should().BeTrue();

        var state = _state.Load()!;
        state.ResumeToken.Should().Be("resume-1");
        state.HeartbeatIntervalSeconds.Should().Be(45);
        state.LastConnectedAt.Should().Be(_clock.GetUtcNow());
    }

    [Fact]
    public async Task The_first_frame_is_a_hello_carrying_inventory_and_capabilities()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        var hello = await _pair.NextFromNodeAsync();

        hello!.Type.Should().Be(NodeFrames.Hello);

        var payload = hello.PayloadAs<NodeHello>()!;
        payload.NodeId.Should().Be("node-test-1");
        payload.SupportedProtocolVersions.Should().Contain(NodeContract.ProtocolVersion);
        payload.Inventory.Architecture.Should().Be("amd64");
        payload.Capabilities.SupportedCommands.Should().NotBeEmpty();
    }

    [Fact]
    public async Task A_reconnect_presents_the_resume_token()
    {
        _state.Save(_state.Load()! with { ResumeToken = "earlier-session", LastReceivedSequence = 17 });

        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        var frame = await _pair.NextFromNodeAsync();

        frame!.Type.Should().Be(NodeFrames.Resume);
        frame.PayloadAs<NodeHello>()!.ResumeToken.Should().Be("earlier-session");
        frame.PayloadAs<NodeHello>()!.LastReceivedSequence.Should().Be(17);
    }

    [Fact]
    public async Task A_protocol_version_the_agent_cannot_speak_is_refused()
    {
        var channel = Channel();
        AcceptHandshake(protocolVersion: 99);

        var act = async () => await channel.OpenAsync(Identity(), CancellationToken.None);

        await act.Should().ThrowAsync<ProtocolNegotiationException>()
            .WithMessage("*v99*");
    }

    [Fact]
    public async Task Unacknowledged_frames_are_replayed_after_a_reconnect()
    {
        // Sent while disconnected — the outbox is where a result waits out an outage.
        await Channel().SendAsync(NodeFrames.CommandResult, new { commandId = "cmd-1" }, "corr-1", CancellationToken.None);
        _outbox.Pending().Should().HaveCount(1);

        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        await _pair.NextFromNodeAsync(); // the resume/hello
        var replayed = await _pair.NextFromNodeAsync();

        replayed!.Type.Should().Be(NodeFrames.CommandResult);
        replayed.Sequence.Should().Be(1);
    }

    [Fact]
    public async Task Frames_the_control_plane_already_holds_are_not_replayed()
    {
        await Channel().SendAsync(NodeFrames.CommandResult, new { n = 1 }, null, CancellationToken.None);
        await Channel().SendAsync(NodeFrames.CommandResult, new { n = 2 }, null, CancellationToken.None);

        var channel = Channel();
        AcceptHandshake(lastReceivedSequence: 1);
        await channel.OpenAsync(Identity(), CancellationToken.None);

        _outbox.Pending().Should().ContainSingle().Which.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task A_rejected_resume_discards_the_outbox_and_resends_inventory()
    {
        await Channel().SendAsync(NodeFrames.CommandResult, new { n = 1 }, null, CancellationToken.None);

        var channel = Channel();
        AcceptHandshake(resumeRejected: true);
        await channel.OpenAsync(Identity(), CancellationToken.None);

        // Replaying into a session with no memory of those commands would deliver results for
        // commands it never issued.
        _outbox.Pending().Should().OnlyContain(e => e.Json.Contains(NodeFrames.Inventory));
        _state.Load()!.LastReceivedSequence.Should().Be(0);
    }

    [Fact]
    public async Task An_ack_trims_the_outbox()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        await channel.SendAsync(NodeFrames.CommandResult, new { n = 1 }, null, CancellationToken.None);
        _outbox.Pending().Should().NotBeEmpty();

        var sequence = _outbox.Pending().Max(e => e.Sequence);
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Ack, new { sequence }, sequence));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ConsumeAsync(channel, cts.Token);

        _outbox.Pending().Should().BeEmpty();
    }

    [Fact]
    public async Task A_ping_is_answered_with_a_pong_and_not_surfaced_to_the_command_loop()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);
        await _pair.NextFromNodeAsync(); // hello

        _pair.PushToNode(ControlFrame.Create(ControlFrames.Ping, new { }, 1));
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Command, TestFactories.Envelope(NodeCommands.DrainNode), 2));

        var surfaced = await FirstFrameAsync(channel);

        surfaced!.Type.Should().Be(ControlFrames.Command);
        _pair.SentByNode.Should().Contain(json => json.Contains(NodeFrames.Pong));
    }

    [Fact]
    public async Task A_frame_from_an_unnegotiated_protocol_version_is_dropped()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        // v2 might mean something different by fields that share a name with v1's.
        _pair.PushToNode("""{"v":2,"type":"control.command","id":"x","sequence":1,"payload":{}}""");
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Command, TestFactories.Envelope(NodeCommands.DrainNode), 2));

        var surfaced = await FirstFrameAsync(channel);

        surfaced!.V.Should().Be(NodeContract.ProtocolVersion);
        surfaced.Sequence.Should().Be(2);
    }

    [Fact]
    public async Task Unparseable_frames_do_not_kill_the_session()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        _pair.PushToNode("{ this is not json");
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Command, TestFactories.Envelope(NodeCommands.DrainNode), 5));

        var surfaced = await FirstFrameAsync(channel);

        surfaced!.Type.Should().Be(ControlFrames.Command);
    }

    [Fact]
    public async Task The_received_sequence_only_moves_forward()
    {
        var channel = Channel();
        AcceptHandshake();
        await channel.OpenAsync(Identity(), CancellationToken.None);

        _pair.PushToNode(ControlFrame.Create(ControlFrames.Ping, new { }, 9));
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Ping, new { }, 3));
        _pair.PushToNode(ControlFrame.Create(ControlFrames.Command, TestFactories.Envelope(NodeCommands.DrainNode), 4));

        await FirstFrameAsync(channel);

        // Rewinding would make the next reconnect ask the control plane to replay everything after 3.
        _state.Load()!.LastReceivedSequence.Should().Be(9);
    }

    [Fact]
    public async Task A_handshake_the_control_plane_never_answers_times_out_rather_than_hanging()
    {
        var channel = Channel();
        _pair.Close();

        var act = async () => await channel.OpenAsync(Identity(), CancellationToken.None);

        await act.Should().ThrowAsync<IOException>();
    }

    [Theory]
    [InlineData("https://panel.example.com", "wss://panel.example.com/api/node-agent/v1/channel")]
    [InlineData("https://panel.example.com/", "wss://panel.example.com/api/node-agent/v1/channel")]
    [InlineData("http://localhost:5000", "ws://localhost:5000/api/node-agent/v1/channel")]
    public void Channel_uri_is_derived_from_the_control_plane_url(string baseUrl, string expected) =>
        ControlChannel.ChannelUri(baseUrl).ToString().Should().Be(expected);

    private static async Task<ControlFrame?> FirstFrameAsync(ControlChannel channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await foreach (var frame in channel.ReadAsync(cts.Token)) return frame;
        return null;
    }

    /// <summary>Drain frames until the reader is idle, so internally-handled ones are processed.</summary>
    private static async Task ConsumeAsync(ControlChannel channel, CancellationToken ct)
    {
        try
        {
            await foreach (var _ in channel.ReadAsync(ct)) { }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public void Dispose()
    {
        _ca.Dispose();
        _agent.Dispose();
    }
}

/// <summary>Section 6: exponential backoff with jitter, and why the jitter is not decoration.</summary>
public class ReconnectPolicyTests
{
    private static ReconnectOptions Options() => new()
    {
        InitialDelayMs = 1_000,
        MaxDelayMs = 300_000,
        Multiplier = 2.0,
        Jitter = false,
    };

    [Fact]
    public void The_first_attempt_does_not_wait()
    {
        new ReconnectPolicy(Options()).Delay(1).Should().Be(TimeSpan.Zero);
    }

    [Theory]
    [InlineData(2, 1_000)]
    [InlineData(3, 2_000)]
    [InlineData(4, 4_000)]
    [InlineData(5, 8_000)]
    public void Delay_doubles_with_each_failed_attempt(int attempt, int expectedMs)
    {
        new ReconnectPolicy(Options()).Delay(attempt).TotalMilliseconds.Should().Be(expectedMs);
    }

    [Fact]
    public void Delay_is_capped()
    {
        var policy = new ReconnectPolicy(Options());

        policy.Delay(50).TotalMilliseconds.Should().Be(300_000);
        policy.Delay(int.MaxValue).TotalMilliseconds.Should().Be(300_000,
            "a node disconnected for a week reaches an attempt count where Math.Pow overflows");
    }

    [Fact]
    public void Full_jitter_spreads_the_delay_across_the_whole_window()
    {
        var options = Options();
        options.Jitter = true;

        var samples = new List<double>();
        var random = new Random(12345);
        var policy = new ReconnectPolicy(options, random.NextDouble);

        for (var i = 0; i < 200; i++) samples.Add(policy.Delay(6).TotalMilliseconds);

        // Full jitter draws from [0, computed]. A "computed ± a bit" scheme would cluster near the
        // top and leave every node returning at almost the same moment after a panel restart.
        samples.Min().Should().BeLessThan(4_000);
        samples.Max().Should().BeGreaterThan(12_000);
        samples.Should().OnlyContain(ms => ms >= 0 && ms <= 16_000);
    }

    [Fact]
    public void Jitter_can_be_turned_off_for_a_predictable_test_environment()
    {
        var options = Options();
        var policy = new ReconnectPolicy(options);

        policy.Delay(4).Should().Be(policy.Delay(4));
    }
}

/// <summary>The durable outbox: what makes a result survive an agent restart.</summary>
public sealed class ChannelOutboxTests : IDisposable
{
    private readonly TempAgent _agent = new();

    private ChannelOutbox Outbox(int max = 500) =>
        new(TestFactories.Store<OutboxState>(_agent, "outbox.json"), TestFactories.Log<ChannelOutbox>(), max);

    [Fact]
    public void Sequences_are_monotonic_and_survive_a_restart()
    {
        Outbox().Append(seq => $"first:{seq}").Should().Be(1);
        Outbox().Append(seq => $"second:{seq}").Should().Be(2);

        // A fresh instance over the same file is what a service restart looks like.
        Outbox().LastSequence.Should().Be(2);
        Outbox().Pending().Should().HaveCount(2);
    }

    [Fact]
    public void Acknowledgement_removes_everything_up_to_the_sequence()
    {
        var outbox = Outbox();
        outbox.Append(seq => $"a:{seq}");
        outbox.Append(seq => $"b:{seq}");
        outbox.Append(seq => $"c:{seq}");

        outbox.AcknowledgeThrough(2);

        outbox.Pending().Should().ContainSingle().Which.Sequence.Should().Be(3);
    }

    [Fact]
    public void An_overfull_outbox_drops_the_oldest_and_keeps_counting()
    {
        var outbox = Outbox(max: 3);

        for (var i = 0; i < 5; i++) outbox.Append(seq => $"frame:{seq}");

        var pending = outbox.Pending();
        pending.Should().HaveCount(3);
        pending.Select(e => e.Sequence).Should().Equal(3, 4, 5);
        outbox.LastSequence.Should().Be(5, "sequence numbers must not be reused after a drop");
    }

    [Fact]
    public void Reset_clears_pending_frames_but_keeps_the_sequence()
    {
        var outbox = Outbox();
        outbox.Append(seq => $"a:{seq}");
        outbox.Append(seq => $"b:{seq}");

        outbox.Reset();

        outbox.Pending().Should().BeEmpty();
        outbox.LastSequence.Should().Be(2);
    }

    public void Dispose() => _agent.Dispose();
}

/// <summary>Atomic, owner-only state files — the thing a node restart depends on.</summary>
public sealed class JsonFileStoreTests : IDisposable
{
    private readonly TempAgent _agent = new();

    private sealed record Sample(string Name, int Count);

    [Fact]
    public void A_missing_file_reads_as_null_rather_than_throwing()
    {
        TestFactories.Store<Sample>(_agent, "missing.json").Load().Should().BeNull();
    }

    [Fact]
    public void Round_trip_preserves_the_document()
    {
        var store = TestFactories.Store<Sample>(_agent, "sample.json");
        store.Save(new Sample("node", 3));

        store.Load().Should().Be(new Sample("node", 3));
    }

    [Fact]
    public void Update_reads_and_writes_under_one_lock()
    {
        var store = TestFactories.Store<Sample>(_agent, "sample.json");
        store.Save(new Sample("node", 1));

        var result = store.Update(current => current! with { Count = current.Count + 1 });

        result.Count.Should().Be(2);
        store.Load()!.Count.Should().Be(2);
    }

    [Fact]
    public void A_corrupt_file_is_quarantined_and_reads_as_null()
    {
        var store = TestFactories.Store<Sample>(_agent, "sample.json");
        store.Save(new Sample("node", 1));
        File.WriteAllText(store.Path, "{ truncated");

        // Restarting clean beats restarting confidently wrong — and the bad file is kept, because
        // whatever wrote it is a bug someone should be able to look at.
        store.Load().Should().BeNull();
        File.Exists(store.Path + ".corrupt").Should().BeTrue();
    }

    [Fact]
    public void No_temporary_file_is_left_behind()
    {
        var store = TestFactories.Store<Sample>(_agent, "sample.json");
        store.Save(new Sample("node", 1));

        File.Exists(store.Path + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void State_files_are_owner_only()
    {
        var store = TestFactories.Store<Sample>(_agent, "sample.json");
        store.Save(new Sample("node", 1));

        FilePermissions.IsOwnerOnly(store.Path).Should().BeTrue();
    }

    [Fact]
    public void Concurrent_updates_do_not_lose_writes()
    {
        var store = TestFactories.Store<Sample>(_agent, "counter.json");
        store.Save(new Sample("node", 0));

        Parallel.For(0, 50, _ => store.Update(current => current! with { Count = current.Count + 1 }));

        store.Load()!.Count.Should().Be(50);
    }

    [Fact]
    public void Enum_and_datetime_round_trip_through_the_shared_serializer()
    {
        var store = TestFactories.Store<NodeState>(_agent, "node.json");
        var when = new DateTimeOffset(2026, 8, 4, 9, 30, 0, TimeSpan.Zero);

        store.Save(new NodeState { NodeId = "n1", EnrolledAt = when, GrantedScopes = [NodeScopes.NodeAdmin] });

        var loaded = store.Load()!;
        loaded.EnrolledAt.Should().Be(when);
        loaded.HasScope(NodeScopes.NodeAdmin).Should().BeTrue();
        loaded.HasScope(NodeScopes.WorkloadsWrite).Should().BeFalse();
    }

    [Fact]
    public void An_empty_scope_list_grants_nothing()
    {
        // The window between enrollment and the first hello-ack must not read as "granted everything".
        new NodeState().HasScope(NodeScopes.WorkloadsWrite).Should().BeFalse();
    }

    public void Dispose() => _agent.Dispose();
}
