using FluentAssertions;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Tests.Fakes;
using Xunit;

namespace Harbora.NodeAgent.Tests;

/// <summary>
/// Section 7: the allowlist, replay rejection, idempotency, scope checks, timeouts and
/// cancellation — everything that must be true before a payload reaches a handler.
/// </summary>
public sealed class CommandAdmissionTests : IDisposable
{
    private readonly TempAgent _agent = new();

    // Seeded from the wall clock because envelopes are stamped with it too — the freshness window
    // is a real comparison, and a fixed epoch here would make every envelope look years stale.
    // Time still only moves when a test says so.
    private readonly ManualClock _clock = new(DateTimeOffset.UtcNow);
    private readonly JsonFileStore<NodeState> _state;
    private readonly CommandLedger _ledger;
    private readonly RecordingResponder _responder = new();

    public CommandAdmissionTests()
    {
        _state = TestFactories.Store<NodeState>(_agent, "node.json");
        _state.Save(TestFactories.EnrolledState());
        _ledger = TestFactories.Ledger(_agent, _clock);
    }

    private CommandDispatcher Dispatcher(params INodeCommandHandler[] handlers) => new(
        handlers, _ledger, TestFactories.Audit(_agent), _state, _agent.Wrapped, _clock,
        TestFactories.Log<CommandDispatcher>());

    private CommandDispatcher WithDrain() => Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));

    // --- allowlist ---

    [Fact]
    public async Task An_unknown_verb_is_refused()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload) with { Command = "RunShell" };

        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        _responder.Acks.Single().Rejected!.Code.Should().Be(NodeErrorCode.UnknownCommand);
        _responder.Results.Single().Status.Should().Be(CommandStatus.Rejected);
    }

    [Fact]
    public async Task A_contract_verb_with_no_handler_reports_that_honestly()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.SnapshotVolume), _responder, CancellationToken.None);

        _responder.Acks.Single().Rejected!.Code.Should().Be(NodeErrorCode.CommandNotSupported);
    }

    [Fact]
    public void The_catalog_has_no_arbitrary_execution_verb()
    {
        NodeCommandCatalog.TryGet("RunShell", out _).Should().BeFalse();
        NodeCommandCatalog.TryGet("Exec", out _).Should().BeFalse();
        NodeCommandCatalog.TryGet("RunCommand", out _).Should().BeFalse();
    }

    // --- replay ---

    [Fact]
    public async Task The_same_nonce_twice_is_a_replay()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, issuedAt: _clock.GetUtcNow());

        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        var replay = envelope with { CommandId = "different-id", IdempotencyKey = "different-key" };
        await dispatcher.ExecuteAsync(replay, _responder, CancellationToken.None);

        _responder.Acks.Last().Rejected!.Code.Should().Be(NodeErrorCode.ReplayRejected);
    }

    [Fact]
    public async Task A_stale_envelope_is_refused_even_with_a_fresh_nonce()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));
        var envelope = TestFactories.Envelope(
            NodeCommands.DeployWorkload, issuedAt: _clock.GetUtcNow() - TimeSpan.FromMinutes(30));

        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        var rejection = _responder.Acks.Single().Rejected!;
        rejection.Code.Should().Be(NodeErrorCode.ReplayRejected);
        rejection.Message.Should().Contain("freshness window");
    }

    [Fact]
    public async Task An_envelope_dated_in_the_future_names_the_clock_as_the_suspect()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));
        var envelope = TestFactories.Envelope(
            NodeCommands.DeployWorkload, issuedAt: _clock.GetUtcNow() + TimeSpan.FromHours(2));

        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        _responder.Acks.Single().Rejected!.Message.Should().Contain("clock");
    }

    [Fact]
    public void Replay_protection_survives_a_restart()
    {
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, issuedAt: _clock.GetUtcNow());

        TestFactories.Ledger(_agent, _clock).AdmitEnvelope(envelope).Should().BeNull();

        // A fresh ledger over the same file is what a service restart is. An in-memory nonce set
        // would let a captured envelope through on exactly the reboot nobody is watching.
        TestFactories.Ledger(_agent, _clock).AdmitEnvelope(envelope)!.Code
            .Should().Be(NodeErrorCode.ReplayRejected);
    }

    [Fact]
    public void Expired_nonces_are_swept()
    {
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, issuedAt: _clock.GetUtcNow());
        _ledger.AdmitEnvelope(envelope).Should().BeNull();

        _clock.Advance(TimeSpan.FromHours(1));
        _ledger.Sweep();

        var store = TestFactories.Store<CommandLedgerState>(_agent, "commands.json");
        store.Load()!.Nonces.Should().BeEmpty();
    }

    // --- idempotency ---

    [Fact]
    public async Task A_redelivered_command_replays_the_original_result_without_re_executing()
    {
        var handler = ScriptedHandler.Succeeding(NodeCommands.DeployWorkload, new AcknowledgedResult { Applied = true });
        var dispatcher = Dispatcher(handler);

        var first = TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "deploy:app-1:rel-5");
        await dispatcher.ExecuteAsync(first, _responder, CancellationToken.None);

        // The control plane retries with a fresh nonce but the same idempotency key.
        var retry = TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "deploy:app-1:rel-5");
        await dispatcher.ExecuteAsync(retry, _responder, CancellationToken.None);

        handler.Invocations.Should().Be(1, "the work must happen exactly once");
        _responder.Acks.Last().Deduplicated.Should().BeTrue();

        var replayed = _responder.Results.Last();
        replayed.IdempotentReplay.Should().BeTrue();
        replayed.CommandId.Should().Be(retry.CommandId, "the result must answer the command that was actually sent");
        replayed.Status.Should().Be(CommandStatus.Succeeded);
    }

    [Fact]
    public async Task Idempotency_survives_a_restart()
    {
        var handler = ScriptedHandler.Succeeding(NodeCommands.DeployWorkload);
        await Dispatcher(handler).ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k1"),
            _responder, CancellationToken.None);

        var afterRestart = ScriptedHandler.Succeeding(NodeCommands.DeployWorkload);
        var dispatcher = new CommandDispatcher(
            [afterRestart], TestFactories.Ledger(_agent, _clock), TestFactories.Audit(_agent),
            _state, _agent.Wrapped, _clock, TestFactories.Log<CommandDispatcher>());

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k1"),
            _responder, CancellationToken.None);

        afterRestart.Invocations.Should().Be(0);
    }

    [Fact]
    public async Task A_failed_command_is_remembered_too()
    {
        // Otherwise a retry loop re-runs a deploy that is failing for a permanent reason, forever.
        var handler = ScriptedHandler.Failing(NodeCommands.DeployWorkload, NodeErrorCode.ImagePullFailed, "no such manifest");
        var dispatcher = Dispatcher(handler);

        await dispatcher.ExecuteAsync(TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k2"), _responder, CancellationToken.None);
        await dispatcher.ExecuteAsync(TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k2"), _responder, CancellationToken.None);

        handler.Invocations.Should().Be(1);
        _responder.Results.Last().Error!.Code.Should().Be(NodeErrorCode.ImagePullFailed);
    }

    [Fact]
    public async Task A_cancelled_command_is_not_remembered()
    {
        // It left the node in an unknown state; replaying "cancelled" would refuse the retry that
        // is supposed to resolve it.
        var dispatcher = Dispatcher(ScriptedHandler.Hanging(NodeCommands.DeployWorkload));

        using var cts = new CancellationTokenSource();
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k3");
        var run = dispatcher.ExecuteAsync(envelope, _responder, cts.Token);

        await WaitUntilAsync(() => dispatcher.InFlightCount > 0);
        await cts.CancelAsync();
        await run;

        _responder.Results.Single().Status.Should().Be(CommandStatus.Cancelled);
        _ledger.FindCompleted("k3").Should().BeNull();
    }

    [Fact]
    public void Completed_records_expire()
    {
        _agent.Options.IdempotencyRetentionHours = 1;
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, idempotencyKey: "k4");

        _ledger.RecordCompleted(envelope, CommandResult.Ok(envelope.CommandId, new { }, _clock.GetUtcNow()));
        _ledger.FindCompleted("k4").Should().NotBeNull();

        _clock.Advance(TimeSpan.FromHours(2));
        _ledger.FindCompleted("k4").Should().BeNull();
    }

    // --- authorisation ---

    [Fact]
    public async Task A_command_declaring_the_wrong_scope_is_unauthorized()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeleteWorkload));

        // Claiming a read scope for a destructive verb must not soften the check.
        var envelope = TestFactories.Envelope(NodeCommands.DeleteWorkload, scope: NodeScopes.WorkloadsRead);
        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        _responder.Acks.Single().Rejected!.Code.Should().Be(NodeErrorCode.Unauthorized);
    }

    [Fact]
    public async Task A_scope_the_node_was_not_enrolled_with_is_refused()
    {
        _state.Save(TestFactories.EnrolledState() with { GrantedScopes = [NodeScopes.WorkloadsRead] });
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload), _responder, CancellationToken.None);

        var rejection = _responder.Acks.Single().Rejected!;
        rejection.Code.Should().Be(NodeErrorCode.Unauthorized);
        rejection.Message.Should().Contain("not enrolled");
    }

    [Fact]
    public async Task An_agent_below_the_minimum_version_refuses_work()
    {
        _state.Save(TestFactories.EnrolledState() with { MinimumAgentVersion = "999.0.0" });
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.DeployWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload), _responder, CancellationToken.None);

        _responder.Acks.Single().Rejected!.Code.Should().Be(NodeErrorCode.AgentTooOld);
    }

    // --- draining ---

    [Fact]
    public async Task A_draining_node_refuses_new_work()
    {
        _state.Save(TestFactories.EnrolledState(draining: true) with { DrainReason = "kernel upgrade" });

        await WithDrain().ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload), _responder, CancellationToken.None);

        var rejection = _responder.Acks.Single().Rejected!;
        rejection.Code.Should().Be(NodeErrorCode.NodeDraining);
        rejection.Message.Should().Contain("kernel upgrade");
        rejection.Retryable.Should().BeTrue();
    }

    [Fact]
    public async Task A_draining_node_still_answers_read_and_admin_commands()
    {
        // Otherwise a drained node could never be told to update, or to stop draining.
        _state.Save(TestFactories.EnrolledState(draining: true));

        var dispatcher = Dispatcher(
            ScriptedHandler.Succeeding(NodeCommands.GetWorkloadStatus),
            ScriptedHandler.Succeeding(NodeCommands.DrainNode));

        await dispatcher.ExecuteAsync(TestFactories.Envelope(NodeCommands.GetWorkloadStatus), _responder, CancellationToken.None);
        await dispatcher.ExecuteAsync(TestFactories.Envelope(NodeCommands.DrainNode), _responder, CancellationToken.None);

        _responder.Acks.Should().OnlyContain(a => a.Rejected == null);
        _responder.Results.Should().OnlyContain(r => r.Status == CommandStatus.Succeeded);
    }

    // --- execution ---

    [Fact]
    public async Task A_handler_that_faults_produces_a_failed_result_rather_than_silence()
    {
        var dispatcher = Dispatcher(new ScriptedHandler(
            NodeCommands.DeployWorkload, (_, _) => throw new InvalidOperationException("boom")));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload), _responder, CancellationToken.None);

        var result = _responder.Results.Single();
        result.Status.Should().Be(CommandStatus.Failed);
        result.Error!.Code.Should().Be(NodeErrorCode.Internal);
    }

    [Fact]
    public async Task A_command_that_overruns_its_timeout_is_reported_as_timed_out()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Hanging(NodeCommands.DeployWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload, timeoutSeconds: 1),
            _responder, CancellationToken.None);

        var result = _responder.Results.Single();
        result.Status.Should().Be(CommandStatus.TimedOut);
        result.Error!.Retryable.Should().BeTrue("a longer bound might succeed where a shorter one did not");
    }

    [Fact]
    public async Task An_explicit_cancel_stops_an_in_flight_command()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Hanging(NodeCommands.DeployWorkload));
        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload, timeoutSeconds: 300);

        var run = dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        await WaitUntilAsync(() => dispatcher.InFlightCount > 0);
        dispatcher.Cancel(envelope.CommandId, "operator changed their mind").Should().BeTrue();
        await run;

        _responder.Results.Single().Status.Should().Be(CommandStatus.Cancelled);
    }

    [Fact]
    public void Cancelling_an_unknown_command_is_a_no_op()
    {
        Dispatcher().Cancel("never-seen", null).Should().BeFalse();
    }

    [Fact]
    public async Task An_envelope_timeout_beyond_a_day_falls_back_to_the_catalog_default()
    {
        // An unbounded deploy would pin a concurrency slot forever.
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.StopWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.StopWorkload, timeoutSeconds: 999_999),
            _responder, CancellationToken.None);

        _responder.Results.Single().Status.Should().Be(CommandStatus.Succeeded);
    }

    [Fact]
    public async Task Progress_and_result_carry_the_same_command_id()
    {
        var dispatcher = Dispatcher(new ScriptedHandler(NodeCommands.DeployWorkload, async (context, ct) =>
        {
            await context.ReportAsync("pulling", 40, "layer 2 of 5", ct);
            return context.Ok(new AcknowledgedResult { Applied = true });
        }));

        var envelope = TestFactories.Envelope(NodeCommands.DeployWorkload);
        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        _responder.Progress.Single().CommandId.Should().Be(envelope.CommandId);
        _responder.Progress.Single().Phase.Should().Be("pulling");
        _responder.Results.Single().CommandId.Should().Be(envelope.CommandId);
    }

    [Fact]
    public async Task Every_admitted_command_produces_exactly_one_ack_and_one_result()
    {
        var dispatcher = Dispatcher(ScriptedHandler.Succeeding(NodeCommands.RestartWorkload));

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.RestartWorkload), _responder, CancellationToken.None);

        _responder.Acks.Should().ContainSingle();
        _responder.Results.Should().ContainSingle();
    }

    [Fact]
    public async Task Admission_and_completion_are_both_audited()
    {
        var audit = TestFactories.Audit(_agent);
        var dispatcher = new CommandDispatcher(
            [ScriptedHandler.Succeeding(NodeCommands.DeleteWorkload)], _ledger, audit, _state,
            _agent.Wrapped, _clock, TestFactories.Log<CommandDispatcher>());

        var envelope = TestFactories.Envelope(NodeCommands.DeleteWorkload);
        await dispatcher.ExecuteAsync(envelope, _responder, CancellationToken.None);

        var entries = audit.Read();
        entries.Should().HaveCount(2);
        entries.Should().Contain(e => e.Outcome == "accepted" && e.ActorName == "tester@example.com");
        entries.Should().Contain(e => e.Outcome == "succeeded" && e.CommandId == envelope.CommandId);
        entries.Should().OnlyContain(e => e.Action == "command.DeleteWorkload");
    }

    [Fact]
    public async Task A_rejection_is_audited_with_its_reason()
    {
        var audit = TestFactories.Audit(_agent);
        var dispatcher = new CommandDispatcher(
            [ScriptedHandler.Succeeding(NodeCommands.DeployWorkload)], _ledger, audit, _state,
            _agent.Wrapped, _clock, TestFactories.Log<CommandDispatcher>());

        await dispatcher.ExecuteAsync(
            TestFactories.Envelope(NodeCommands.DeployWorkload, scope: NodeScopes.WorkloadsRead),
            _responder, CancellationToken.None);

        audit.Read().Single().ErrorCode.Should().Be(nameof(NodeErrorCode.Unauthorized));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        condition().Should().BeTrue("the condition should have become true within 5s");
    }

    public void Dispose() => _agent.Dispose();
}
