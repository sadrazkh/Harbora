using FluentAssertions;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The pure rules under the loop — the consent policy, the registry's capability filter, the daily
/// cap's arithmetic and the redaction-then-truncation of tool results.
/// </summary>
public class AgentPolicyTests
{
    // ---- consent -------------------------------------------------------------------------------

    [Theory]
    [InlineData(AgentToolKind.Read, true, false, false)]
    [InlineData(AgentToolKind.SafeWrite, false, true, false)]
    [InlineData(AgentToolKind.DangerousWrite, false, false, false)]
    [InlineData(AgentToolKind.Destructive, false, false, true)]
    public void The_consent_matrix(AgentToolKind kind, bool runsImmediately, bool rememberable, bool typed)
    {
        AgentConsentPolicy.RunsImmediately(kind).Should().Be(runsImmediately);
        AgentConsentPolicy.IsRememberable(kind).Should().Be(rememberable);
        AgentConsentPolicy.NeedsTypedConfirmation(kind).Should().Be(typed);
        AgentConsentPolicy.IsAutoApproved(kind, remembered: true).Should().Be(kind == AgentToolKind.SafeWrite);
        AgentConsentPolicy.IsAutoApproved(kind, remembered: false).Should().BeFalse();
    }

    [Theory]
    [InlineData("shop-db", "shop-db", true)]
    [InlineData("shop-db", "  shop-db\t", true)]
    [InlineData(" shop-db ", "shop-db", true)]
    [InlineData("shop-db", "Shop-db", false)]
    [InlineData("shop-db", "shop-db.", false)]
    [InlineData("shop-db", "", false)]
    [InlineData("shop-db", null, false)]
    [InlineData("", "", false)]
    [InlineData(null, null, false)]
    [InlineData("فروشگاه", "فروشگاه", true)]
    public void A_typed_confirmation_is_trimmed_and_then_compared_exactly(string? expected, string? typed, bool matches) =>
        AgentConsentPolicy.ConfirmationMatches(expected, typed).Should().Be(matches);

    [Fact]
    public void The_card_badges()
    {
        AgentConsentPolicy.KindLabel(AgentToolKind.SafeWrite).Should().Be("safe");
        AgentConsentPolicy.KindLabel(AgentToolKind.DangerousWrite).Should().Be("dangerous");
        AgentConsentPolicy.KindLabel(AgentToolKind.Destructive).Should().Be("destructive");
    }

    // ---- registry ------------------------------------------------------------------------------

    [Fact]
    public async Task The_registry_offers_each_role_exactly_the_tools_its_capabilities_allow()
    {
        var tools = new IAgentTool[]
        {
            new FakeTool("read_anything", AgentToolKind.Read),
            new FakeTool("restart_app", AgentToolKind.SafeWrite, Capabilities.AppsOperate),
            new FakeTool("create_app", AgentToolKind.SafeWrite, Capabilities.AppsCreate),
            new FakeTool("restore_backup", AgentToolKind.Destructive, Capabilities.BackupsRestore)
        };

        async Task<IEnumerable<string>> Offered(SystemRole role)
        {
            using var h = new AgentHarness(tools, role);
            return (await h.Registry.AllowedForAsync(h.Principal, default)).Select(t => t.Name).ToList();
        }

        (await Offered(SystemRole.Owner)).Should().Equal("create_app", "read_anything", "restart_app", "restore_backup");
        (await Offered(SystemRole.Member)).Should().Equal("create_app", "read_anything", "restart_app");
        (await Offered(SystemRole.Operator)).Should().Equal("read_anything", "restart_app");
        (await Offered(SystemRole.Viewer)).Should().Equal("read_anything");
    }

    [Fact]
    public void Two_tools_under_one_name_are_a_registration_error()
    {
        var act = () => new AgentToolRegistry(
            [new FakeTool("same", AgentToolKind.Read), new FakeTool("same", AgentToolKind.SafeWrite)],
            new AlwaysAllowed());

        act.Should().Throw<InvalidOperationException>().WithMessage("*'same'*");
    }

    [Fact]
    public void A_tool_becomes_the_function_spec_the_model_is_offered()
    {
        var tool = new FakeTool("peek", AgentToolKind.Read);
        AgentToolRegistry.SpecOf(tool).Should().Be(new AgentToolSpec("peek", tool.Description, tool.ParametersSchema));
        new AgentToolRegistry([tool], new AlwaysAllowed()).Find("PEEK").Should().BeNull("names are exact");
    }

    private sealed class AlwaysAllowed : IAgentCapabilityCheck
    {
        public Task<bool> AllowsAsync(System.Security.Claims.ClaimsPrincipal user, string capability, CancellationToken ct) =>
            Task.FromResult(true);
    }

    // ---- the daily cap -------------------------------------------------------------------------

    [Theory]
    [InlineData(null, AgentSettingKeys.DefaultDailyRequestLimit)]
    [InlineData("", AgentSettingKeys.DefaultDailyRequestLimit)]
    [InlineData("  ", AgentSettingKeys.DefaultDailyRequestLimit)]
    [InlineData("25", 25)]
    [InlineData(" 7 ", 7)]
    [InlineData("0", 0)]
    [InlineData("-3", AgentSettingKeys.DefaultDailyRequestLimit)]
    [InlineData("lots", AgentSettingKeys.DefaultDailyRequestLimit)]
    public async Task The_limit_is_read_from_the_setting_and_anything_unreadable_means_the_default(string? stored, int expected)
    {
        using var h = new AgentHarness();
        if (stored is not null) h.SetDailyLimit(stored);

        (await h.Meter.LimitAsync(default)).Should().Be(expected);
    }

    [Fact]
    public async Task Reserving_counts_up_to_the_limit_and_then_refuses_until_the_next_utc_day()
    {
        using var h = new AgentHarness();
        h.SetDailyLimit("2");
        var meter = h.Meter;

        (await meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeTrue();
        (await meter.RemainingAsync(h.WorkspaceId, default)).Should().Be(1);
        (await meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeTrue();
        (await meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeFalse();
        (await meter.RemainingAsync(h.WorkspaceId, default)).Should().Be(0);

        h.Clock.UtcNow = new DateTimeOffset(2026, 10, 12, 0, 1, 0, TimeSpan.Zero);
        (await meter.RemainingAsync(h.WorkspaceId, default)).Should().Be(2);
        (await meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeTrue();
        (await h.Db.AgentUsageDays.CountAsync()).Should().Be(2, "one row per workspace per UTC day");
    }

    [Fact]
    public async Task One_workspaces_calls_do_not_count_against_another()
    {
        using var h = new AgentHarness();
        h.SetDailyLimit("1");
        using var other = h.Another(sameWorkspace: false);

        (await h.Meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeTrue();
        (await other.Meter.TryReserveAsync(other.WorkspaceId, default)).Should().BeTrue();
        (await h.Meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Unlimited_never_refuses_and_reports_no_remainder()
    {
        using var h = new AgentHarness();
        h.SetDailyLimit("0");

        for (var i = 0; i < 5; i++) (await h.Meter.TryReserveAsync(h.WorkspaceId, default)).Should().BeTrue();
        (await h.Meter.RemainingAsync(h.WorkspaceId, default)).Should().BeNull();
        (await h.Meter.UsedTodayAsync(h.WorkspaceId, default)).Should().Be(5);
    }

    // ---- what leaves the server ----------------------------------------------------------------

    [Fact]
    public void A_tool_result_is_redacted_before_it_is_truncated()
    {
        var secret = "zz-very-secret-zz";
        var text = new string('a', AgentJson.MaxToolResultLength - 5) + secret + new string('b', 100);

        var forModel = AgentJson.ForModel(text, [secret]);

        forModel.Should().NotContain("zz-very").And.NotContain("very-secret");
        forModel.Should().EndWith("more characters]");
        forModel.Length.Should().BeLessThan(AgentJson.MaxToolResultLength + 60);
    }

    [Fact]
    public void Shapes_are_redacted_even_when_nobody_named_the_secret()
    {
        AgentJson.ForModel("postgres://admin:hunter22@db:5432/x and API_KEY=abc123def456")
            .Should().Be("postgres://admin:[redacted]@db:5432/x and API_KEY=[redacted]");
    }

    [Fact]
    public void Persian_stays_readable_in_the_agents_json()
    {
        AgentJson.Serialize(new { name = "فروشگاه" }).Should().Be("{\"name\":\"فروشگاه\"}");
        AgentJson.Serialize(new { kind = AgentToolKind.DangerousWrite }).Should().Be("{\"kind\":\"dangerousWrite\"}");
    }
}
