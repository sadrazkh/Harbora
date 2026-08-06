using FluentAssertions;
using Harbora.Infrastructure.Terminals;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Who may open a shell, and when one closes.
///
/// This is the widest door the panel has: a shell in a container is that application's filesystem,
/// its environment — which holds its database password — and its network. So the rule is tested for
/// the order it asks its questions in, not only for the answer, because the order is what keeps a
/// refusal from doubling as a probe.
/// </summary>
public class TerminalAccessTests
{
    [Fact]
    public void Everything_in_place_opens_a_terminal() =>
        TerminalAccess.Decide(featureEnabled: true, mayManage: true, isLocalServer: true, hasRunningContainer: true)
            .Should().Be(TerminalRefusal.None);

    [Fact]
    public void A_platform_that_has_not_enabled_terminals_has_no_terminal_at_all() =>
        TerminalAccess.Decide(featureEnabled: false, mayManage: true, isLocalServer: true, hasRunningContainer: true)
            .Should().Be(TerminalRefusal.FeatureOff);

    [Fact]
    public void The_switch_is_asked_before_anything_else() =>
        TerminalAccess.Decide(featureEnabled: false, mayManage: false, isLocalServer: false, hasRunningContainer: false)
            .Should().Be(TerminalRefusal.FeatureOff,
                "with the feature off the page does not exist, rather than existing and refusing");

    [Fact]
    public void Somebody_who_may_not_manage_the_app_is_refused() =>
        TerminalAccess.Decide(featureEnabled: true, mayManage: false, isLocalServer: true, hasRunningContainer: true)
            .Should().Be(TerminalRefusal.NotAllowed);

    [Fact]
    public void Authorisation_is_asked_before_the_state_of_the_container()
    {
        // Whether an application is up is information about somebody's application. Answering it
        // before checking who is asking turns the refusal into a probe.
        TerminalAccess.Decide(featureEnabled: true, mayManage: false, isLocalServer: true, hasRunningContainer: false)
            .Should().Be(TerminalRefusal.NotAllowed);

        TerminalAccess.Decide(featureEnabled: true, mayManage: false, isLocalServer: false, hasRunningContainer: false)
            .Should().Be(TerminalRefusal.NotAllowed);
    }

    [Fact]
    public void An_app_on_a_node_says_so_rather_than_opening_onto_nothing() =>
        TerminalAccess.Decide(featureEnabled: true, mayManage: true, isLocalServer: false, hasRunningContainer: true)
            .Should().Be(TerminalRefusal.NotLocal);

    [Fact]
    public void An_app_with_no_running_container_says_that_instead() =>
        TerminalAccess.Decide(featureEnabled: true, mayManage: true, isLocalServer: true, hasRunningContainer: false)
            .Should().Be(TerminalRefusal.NotRunning);

    [Fact]
    public void Being_on_a_node_is_reported_before_being_down() =>
        TerminalAccess.Decide(featureEnabled: true, mayManage: true, isLocalServer: false, hasRunningContainer: false)
            .Should().Be(TerminalRefusal.NotLocal,
                "\"this node has no terminal\" is the thing to fix; \"it is not running\" would " +
                "send somebody off to start an application that is already up");

    // ---- when a session ends ----

    private static readonly DateTimeOffset Start = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_session_being_used_stays_open() =>
        TerminalAccess.ShouldClose(Start, Start.AddMinutes(30), Start.AddMinutes(31))
            .Should().BeFalse();

    [Fact]
    public void A_session_nobody_has_touched_closes()
    {
        var idle = Start + TerminalAccess.IdleTimeout;

        TerminalAccess.ShouldClose(Start, Start, idle.AddSeconds(-1)).Should().BeFalse();
        TerminalAccess.ShouldClose(Start, Start, idle).Should().BeTrue();
    }

    [Fact]
    public void Output_arriving_counts_as_activity() =>
        TerminalAccess.ShouldClose(Start, Start.AddMinutes(59), Start.AddHours(1))
            .Should().BeFalse("a long-running command is producing output, and that is traffic");

    [Fact]
    public void A_session_open_all_afternoon_closes_however_busy_it_is()
    {
        var cap = Start + TerminalAccess.MaxDuration;

        TerminalAccess.ShouldClose(Start, cap.AddSeconds(-1), cap.AddSeconds(-1)).Should().BeFalse();
        TerminalAccess.ShouldClose(Start, cap, cap).Should().BeTrue(
            "a shell open for four hours is a tab nobody closed");
    }

    // ---- what is run, and how big it is ----

    [Fact]
    public void The_command_is_a_shell_and_nothing_a_caller_chose()
    {
        TerminalAccess.Command.Should().HaveCount(3);
        TerminalAccess.Command[0].Should().Be("/bin/sh");
        TerminalAccess.Command[1].Should().Be("-c");
        TerminalAccess.Command[2].Should().Contain("exec").And.Contain("/bin/sh");
    }

    [Theory]
    [InlineData(120, 40, 120u, 40u)]
    [InlineData(0, 0, 20u, 5u)]
    [InlineData(-5, -5, 20u, 5u)]
    [InlineData(100000, 100000, 500u, 200u)]
    public void A_size_a_container_will_accept(int columns, int rows, uint expectedColumns, uint expectedRows) =>
        TerminalAccess.Size(columns, rows).Should().Be((expectedColumns, expectedRows));

    [Fact]
    public void A_window_reported_as_zero_does_not_take_the_session_with_it() =>
        TerminalAccess.Size(0, 0).Should().NotBe((0u, 0u),
            "a browser resized while the page loads reports zero, and docker refuses it");
}
