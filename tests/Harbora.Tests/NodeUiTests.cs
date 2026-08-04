using System.Text.RegularExpressions;
using FluentAssertions;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Navigation;
using Harbora.Web.ViewModels;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The node screens' own conventions, and the small amount of logic that decides what an operator
/// sees. The mapping from a node's state to a coloured pill is the sort of thing that looks like
/// presentation and is really a judgement — "offline" and "revoked" are not the same problem.
/// </summary>
public class NodeUiTests
{
    private static readonly string NodeViews = Path.Combine(TestPaths.WebRoot, "Views", "Nodes");

    private static NodeRow Row(
        NodeStatus status = NodeStatus.Online,
        string health = "healthy",
        bool connected = true,
        bool draining = false,
        bool revoked = false,
        DateTimeOffset? certificateNotAfter = null) =>
        new("nd_1", "web-01", status, health, connected, draining, revoked, "0.2.0",
            "eu-central", "production", "amd64", "Debian GNU/Linux", "27.3.1",
            4, 8L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024,
            200L * 1024 * 1024 * 1024, 150L * 1024 * 1024 * 1024,
            0.4, 3, 1, DateTimeOffset.UtcNow, certificateNotAfter, ["203.0.113.10"]);

    // --- the pages exist and are reachable ---

    [Theory]
    [InlineData("Index.cshtml")]
    [InlineData("Detail.cshtml")]
    public void The_node_views_exist(string view) =>
        File.Exists(Path.Combine(NodeViews, view)).Should().BeTrue();

    [Fact]
    public void The_sidebar_lists_nodes_behind_the_servers_capability()
    {
        // Reading a node list means reading hostnames, core counts and runtime versions, which is
        // not a tenant's to see.
        var item = NavigationMap.All
            .SelectMany(g => g.Items)
            .Should().ContainSingle(i => i.Key == "nodes").Subject;

        item.Controller.Should().Be("Nodes");
        item.Capability.Should().Be(Harbora.Domain.Authorization.Capabilities.ServersManage);
    }

    [Fact]
    public void Every_sidebar_item_has_a_label_in_both_languages()
    {
        // The sidebar falls through to `_ => key` for an unknown item, so a missing label is not a
        // build error — it is an English identifier sitting in a Persian menu. This is the only
        // thing that would notice.
        var sidebar = File.ReadAllText(
            Path.Combine(TestPaths.WebRoot, "Views", "Shared", "Design", "_Sidebar.cshtml"));

        foreach (var key in NavigationMap.All.SelectMany(g => g.Items).Select(i => i.Key))
            sidebar.Should().Contain($"(\"{key}\"", $"the sidebar needs a label for the '{key}' item");

        foreach (var key in NavigationMap.All.Select(g => g.Key))
            sidebar.Should().Contain($"(\"{key}\"", $"the sidebar needs a label for the '{key}' group");
    }

    [Fact]
    public void The_node_views_use_logical_direction_classes()
    {
        // ml-/mr-/pl-/pr- do not mirror in RTL, and Persian is the default culture. A physical class
        // here is a layout that is wrong for most of the people using it.
        var physical = new Regex(@"(?<![\w-])(ml|mr|pl|pr|border-l|border-r|rounded-l|rounded-r)-\w");

        foreach (var file in Directory.EnumerateFiles(NodeViews, "*.cshtml"))
        {
            var offending = physical.Match(File.ReadAllText(file));
            offending.Success.Should().BeFalse(
                $"{Path.GetFileName(file)} uses '{offending.Value}' — use ms/me/ps/pe/border-s/border-e");
        }
    }

    [Fact]
    public void The_node_views_are_written_in_both_languages()
    {
        var persian = new Regex(@"[؀-ۿ]");

        foreach (var file in Directory.EnumerateFiles(NodeViews, "*.cshtml"))
            persian.IsMatch(File.ReadAllText(file)).Should().BeTrue(
                $"{Path.GetFileName(file)} has no Persian text");
    }

    [Fact]
    public void The_views_never_render_a_measured_value_themselves()
    {
        // Same honesty gate the rest of the panel is held to: unknown is not zero, and only the
        // metric partials are allowed to decide how a measurement reads.
        foreach (var file in Directory.EnumerateFiles(NodeViews, "*.cshtml"))
            File.ReadAllText(file).Should().NotContain("View.Text",
                $"{Path.GetFileName(file)} should render Design/_Metric instead");
    }

    // --- status → tone ---

    [Fact]
    public void A_healthy_online_node_reads_as_ok() =>
        Row().Tone.Should().Be(Tone.Ok);

    [Fact]
    public void A_degraded_node_reads_as_a_warning_not_a_failure()
    {
        // Degraded means "serving, under pressure". Colouring it as an error would send someone to
        // fix a node that is working.
        Row(health: "degraded").Tone.Should().Be(Tone.Warn);
    }

    [Fact]
    public void A_draining_node_reads_as_a_warning() =>
        Row(status: NodeStatus.Draining).Tone.Should().Be(Tone.Warn);

    [Fact]
    public void An_offline_node_reads_as_an_error() =>
        Row(status: NodeStatus.Offline).Tone.Should().Be(Tone.Error);

    [Fact]
    public void A_node_awaiting_its_first_connection_is_informational_rather_than_broken()
    {
        // It was enrolled seconds ago and has not dialled in yet. Red here would send an operator
        // looking for a fault that is a normal part of installing.
        Row(status: NodeStatus.Pending).Tone.Should().Be(Tone.Info);
    }

    [Fact]
    public void A_revoked_node_outranks_whatever_else_it_looks_like()
    {
        // "Went quiet" and "somebody withdrew its credential" are different problems with different
        // fixes, so a revoked node must not read as merely online.
        Row(status: NodeStatus.Online, revoked: true).Tone.Should().Be(Tone.Error);
    }

    // --- certificate warning ---

    [Fact]
    public void A_certificate_a_fortnight_out_is_flagged()
    {
        // The agent renews at two thirds of its certificate's life unprompted, so this being lit
        // means renewal has been failing — which is worth seeing before it becomes an outage.
        var now = DateTimeOffset.UtcNow;

        Row(certificateNotAfter: now.AddDays(10)).CertificateExpiringSoon(now).Should().BeTrue();
        Row(certificateNotAfter: now.AddDays(60)).CertificateExpiringSoon(now).Should().BeFalse();
    }

    [Fact]
    public void A_node_with_no_certificate_date_is_not_flagged() =>
        Row(certificateNotAfter: null).CertificateExpiringSoon(DateTimeOffset.UtcNow).Should().BeFalse();

    // --- tokens ---

    [Fact]
    public void An_outstanding_token_is_usable_and_a_spent_one_is_not()
    {
        var now = DateTimeOffset.UtcNow;

        new EnrollmentTokenRow("hbr_node_abc", now.AddMinutes(10), null, null, null, "web-01")
            .IsUsable(now).Should().BeTrue();

        new EnrollmentTokenRow("hbr_node_abc", now.AddMinutes(10), now, "nd_1", null, null)
            .IsUsable(now).Should().BeFalse();

        new EnrollmentTokenRow("hbr_node_abc", now.AddMinutes(10), null, null, now, null)
            .IsUsable(now).Should().BeFalse();

        new EnrollmentTokenRow("hbr_node_abc", now.AddMinutes(-1), null, null, null, null)
            .IsUsable(now).Should().BeFalse();
    }

    // --- events ---

    [Theory]
    [InlineData("deployment.failed", Tone.Error)]
    [InlineData("deployment.rolled-back", Tone.Error)]
    [InlineData("pressure.disk", Tone.Warn)]
    [InlineData("database-grant.expired", Tone.Warn)]
    [InlineData("certificate.expiring", Tone.Warn)]
    [InlineData("deployment.completed", Tone.Ok)]
    [InlineData("container.state-changed", Tone.Info)]
    public void An_event_kind_reads_as_the_right_severity(string kind, string expected) =>
        new NodeEventRow(kind, "…", null, DateTimeOffset.UtcNow).Tone.Should().Be(expected);

    [Fact]
    public void An_event_kind_this_panel_has_never_heard_of_renders_neutrally()
    {
        // The contract lets a newer agent invent kinds. An unknown one must render, not throw.
        new NodeEventRow("something.invented.later", "…", null, DateTimeOffset.UtcNow)
            .Tone.Should().Be(Tone.Info);
    }

    // --- commands ---

    [Theory]
    [InlineData(NodeCommandStatus.Succeeded, Tone.Ok)]
    [InlineData(NodeCommandStatus.Failed, Tone.Error)]
    [InlineData(NodeCommandStatus.Rejected, Tone.Error)]
    [InlineData(NodeCommandStatus.TimedOut, Tone.Warn)]
    [InlineData(NodeCommandStatus.Cancelled, Tone.Idle)]
    [InlineData(NodeCommandStatus.Sent, Tone.Info)]
    public void A_command_status_reads_as_the_right_severity(NodeCommandStatus status, string expected) =>
        new NodeCommandRow("c1", "DeployWorkload", status, DateTimeOffset.UtcNow, null, null, null, false, null)
            .Tone.Should().Be(expected);

    [Fact]
    public void A_command_that_has_not_finished_has_no_duration()
    {
        var issued = DateTimeOffset.UtcNow;

        new NodeCommandRow("c1", "DeployWorkload", NodeCommandStatus.Sent, issued, null, null, null, false, null)
            .Duration.Should().BeNull();

        new NodeCommandRow("c1", "DeployWorkload", NodeCommandStatus.Succeeded, issued, issued.AddSeconds(12), null, null, false, null)
            .Duration.Should().Be(TimeSpan.FromSeconds(12));
    }

    // --- the list page's own totals ---

    [Fact]
    public void The_fleet_totals_count_what_they_say()
    {
        var model = new NodeListViewModel(
            [
                Row(),
                Row(status: NodeStatus.Offline, connected: false),
                Row(status: NodeStatus.Online, draining: true),
            ],
            [], DateTimeOffset.UtcNow, "https://panel.test");

        model.Online.Should().Be(2, "a draining node is still online");
        model.Offline.Should().Be(1);
        model.Draining.Should().Be(1);
        model.Workloads.Should().Be(9);
    }

    [Fact]
    public void A_token_is_only_carried_to_the_page_when_one_was_just_minted()
    {
        // It exists in exactly one response and nowhere else; the panel stores only a hash.
        new NodeListViewModel([], [], DateTimeOffset.UtcNow, "https://panel.test")
            .NewToken.Should().BeNull();
    }
}
