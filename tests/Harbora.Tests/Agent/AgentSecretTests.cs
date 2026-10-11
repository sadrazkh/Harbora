using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Features;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// Secrets as black boxes (owner decision 2026-10-12): the lock-button vault holds a value for its person
/// and hands back a handle; the model only ever sees the handle; whatever a tool echoes of a value it
/// applied is scrubbed before it can reach the provider, the conversation or a plan.
/// </summary>
public class AgentSecretTests
{
    private const string Value = "pw-Th1s-Is-The-Real-Secret!";

    /// <summary>Counts what the vault asks of the protector, so "held encrypted" is observed, not assumed.</summary>
    private sealed class CountingProtector : ISecretProtector
    {
        public List<string> Protected { get; } = [];
        public string Protect(string plaintext) { Protected.Add(plaintext); return "enc:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(plaintext)); }
        public string Unprotect(string ciphertext) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(ciphertext[4..]));
        public byte[] DeriveKey(string purpose) => new byte[32];
    }

    // ---- the vault -----------------------------------------------------------------------------

    [Fact]
    public void A_held_secret_comes_back_as_a_handle_and_only_its_person_can_resolve_it()
    {
        var clock = new MutableClock(AgentHarness.Start);
        var protector = new CountingProtector();
        var vault = new InMemoryAgentSecretVault(protector, clock);
        var (workspace, user) = (Guid.NewGuid(), Guid.NewGuid());

        var view = vault.Put(workspace, user, "DB_PASSWORD", Value);

        AgentSecretHandles.IsHandle(view.Handle).Should().BeTrue();
        view.Handle.Should().MatchRegex(@"^\{\{secret:[A-Za-z0-9_\-]{22}\}\}$");
        view.Handle.Should().NotContain(Value);
        view.Name.Should().Be("DB_PASSWORD");
        view.ExpiresAt.Should().Be(AgentHarness.Start + AgentSecretHandles.Lifetime);
        protector.Protected.Should().Equal([Value], "the value is held encrypted, never as it was typed");

        vault.Resolve(workspace, user, view.Handle).Should().Be(Value);
        vault.Resolve(workspace, user, "  " + view.Handle + "\n").Should().Be(Value, "surrounding whitespace is allowed");
        vault.Resolve(workspace, Guid.NewGuid(), view.Handle).Should().BeNull("another person");
        vault.Resolve(Guid.NewGuid(), user, view.Handle).Should().BeNull("another workspace");
        vault.Resolve(workspace, user, "{{secret:AAAAAAAAAAAAAAAAAAAAAA}}").Should().BeNull("unknown");
        vault.Resolve(workspace, user, view.Handle + "x").Should().BeNull("not exactly a handle");
        vault.Resolve(workspace, user, Value).Should().BeNull();
    }

    [Fact]
    public void Every_handle_is_different()
    {
        var vault = new InMemoryAgentSecretVault(new CountingProtector(), new MutableClock(AgentHarness.Start));
        var (workspace, user) = (Guid.NewGuid(), Guid.NewGuid());

        var handles = Enumerable.Range(0, 40).Select(_ => vault.Put(workspace, user, "X", "same value").Handle).ToList();

        handles.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void A_handle_expires_after_its_lifetime_and_is_forgotten()
    {
        var clock = new MutableClock(AgentHarness.Start);
        var vault = new InMemoryAgentSecretVault(new CountingProtector(), clock);
        var (workspace, user) = (Guid.NewGuid(), Guid.NewGuid());
        var view = vault.Put(workspace, user, "TOKEN", Value);

        clock.UtcNow = AgentHarness.Start + AgentSecretHandles.Lifetime - TimeSpan.FromSeconds(1);
        vault.Resolve(workspace, user, view.Handle).Should().Be(Value);

        clock.UtcNow = AgentHarness.Start + AgentSecretHandles.Lifetime;
        vault.Resolve(workspace, user, view.Handle).Should().BeNull();
        vault.LiveValues(workspace, user).Should().BeEmpty();
    }

    [Fact]
    public void One_person_holds_at_most_fifty_and_the_oldest_makes_room()
    {
        var clock = new MutableClock(AgentHarness.Start);
        var vault = new InMemoryAgentSecretVault(new CountingProtector(), clock);
        var (workspace, user) = (Guid.NewGuid(), Guid.NewGuid());
        var other = Guid.NewGuid();
        var someoneElse = vault.Put(workspace, other, "THEIRS", "theirs");

        var handles = new List<string>();
        for (var i = 0; i <= InMemoryAgentSecretVault.MaxPerPerson; i++)
        {
            clock.UtcNow = AgentHarness.Start + TimeSpan.FromSeconds(i);
            handles.Add(vault.Put(workspace, user, $"S{i}", $"value-{i}").Handle);
        }

        vault.Resolve(workspace, user, handles[0]).Should().BeNull("the oldest was forgotten to make room");
        vault.Resolve(workspace, user, handles[1]).Should().Be("value-1");
        vault.Resolve(workspace, user, handles[^1]).Should().Be($"value-{InMemoryAgentSecretVault.MaxPerPerson}");
        vault.LiveValues(workspace, user).Should().HaveCount(InMemoryAgentSecretVault.MaxPerPerson);
        vault.Resolve(workspace, other, someoneElse.Handle).Should().Be("theirs", "one person's cap never touches another's");

    }

    // ---- the service behind POST /assistant/secrets ----------------------------------------------

    [Theory]
    [InlineData("", "x", "name")]
    [InlineData("   ", "x", "name")]
    [InlineData(null, "x", "name")]
    [InlineData("TOKEN", "", "value")]
    [InlineData("TOKEN", null, "value")]
    public async Task A_name_or_value_out_of_bounds_is_refused_and_nothing_is_held(string? name, string? value, string which)
    {
        using var h = new AgentHarness();
        var service = new AgentSecretService(h.Vault, new FeatureGate(h.Db), h.Audit);

        var outcome = await service.HoldAsync(h.Actor, new AgentSecretRequest(name!, value!), default);

        outcome.Ok.Should().BeFalse(which);
        outcome.Code.Should().Be("invalid");
        outcome.MessageFa.Should().NotBeNullOrEmpty();
        h.Vault.LiveValues(h.WorkspaceId, h.UserId).Should().BeEmpty();
        h.Audit.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task The_bounds_are_a_hundred_characters_of_name_and_eight_thousand_of_value()
    {
        using var h = new AgentHarness();
        var service = new AgentSecretService(h.Vault, new FeatureGate(h.Db), h.Audit);

        (await service.HoldAsync(h.Actor, new(new string('n', 100), new string('v', 8000)), default)).Ok.Should().BeTrue();
        (await service.HoldAsync(h.Actor, new(new string('n', 101), "v"), default)).Ok.Should().BeFalse();
        (await service.HoldAsync(h.Actor, new("N", new string('v', 8001)), default)).Ok.Should().BeFalse();
    }

    [Fact]
    public async Task Holding_a_secret_is_audited_by_name_only_and_refused_without_the_entitlement()
    {
        using var h = new AgentHarness();
        var service = new AgentSecretService(h.Vault, new FeatureGate(h.Db), h.Audit);

        var outcome = await service.HoldAsync(h.Actor, new AgentSecretRequest(" STRIPE_KEY ", Value), default);

        outcome.View!.Name.Should().Be("STRIPE_KEY");
        var entry = h.Audit.Entries.Single();
        entry.Action.Should().Be(AgentSecretService.SecretHeldAction);
        entry.WorkspaceId.Should().Be(h.WorkspaceId);
        entry.MetadataJson.Should().Contain("STRIPE_KEY").And.NotContain(Value);
        JsonSerializer.Serialize(outcome).Should().NotContain(Value);

        using var locked = new AgentHarness(entitled: false);
        var refused = await new AgentSecretService(locked.Vault, new FeatureGate(locked.Db), locked.Audit)
            .HoldAsync(locked.Actor, new AgentSecretRequest("X", Value), default);
        refused.Code.Should().Be("feature_not_enabled");
        locked.Vault.LiveValues(locked.WorkspaceId, locked.UserId).Should().BeEmpty();
    }

    // ---- the value never reaches the model -----------------------------------------------------

    [Fact]
    public async Task The_model_sees_the_handle_and_never_the_value_even_when_a_tool_echoes_it()
    {
        // A careless tool that puts the value it resolved into its result and its summary.
        AgentHarness? harness = null;
        var leaky = new FakeTool("leaky_read", AgentToolKind.Read)
        {
            OnExecute = (args, context) =>
            {
                var value = harness!.Vault.Resolve(context.WorkspaceId, context.UserId, AgentArgsOf(args));
                return new AgentToolResult(true, $$"""{"applied":"{{value}}"}""", $"Applied {value}", $"اعمال شد {value}");
            }
        };
        using var h = new AgentHarness([leaky]);
        harness = h;
        var handle = h.Vault.Put(h.WorkspaceId, h.UserId, "API_TOKEN", Value).Handle;
        h.Chat.ThenCall("leaky_read", JsonSerializer.Serialize(new { value = handle })).ThenText("Done.");

        var (_, events) = await h.SendAsync($"set API_TOKEN to {handle}");

        var sent = h.Chat.EverythingSent();
        sent.Should().Contain(handle, "the handle is what the model works with");
        sent.Should().NotContain(Value);
        events.Where(e => e.Name == "tool").Select(e => JsonSerializer.Serialize(e.Data)).Should().OnlyContain(s => !s.Contains(Value));
        (await h.Db.AgentMessages.AnyAsync(m => (m.Text != null && m.Text.Contains(Value))
                                               || (m.SummaryEn != null && m.SummaryEn.Contains(Value))
                                               || (m.ToolCallsJson != null && m.ToolCallsJson.Contains(Value)))).Should().BeFalse();
    }

    [Fact]
    public async Task A_plan_step_that_echoes_the_value_it_applied_leaves_it_in_no_result_message_or_model_request()
    {
        AgentHarness? harness = null;
        var setEnv = new FakeTool("set_env", AgentToolKind.SafeWrite)
        {
            OnExecute = (args, context) =>
            {
                var value = harness!.Vault.Resolve(context.WorkspaceId, context.UserId, AgentArgsOf(args));
                return AgentToolResult.Failed($"The node rejected {value}.", $"نود {value} را نپذیرفت.");
            }
        };
        using var h = new AgentHarness([setEnv]);
        harness = h;
        var handle = h.Vault.Put(h.WorkspaceId, h.UserId, "DB_PASSWORD", Value).Handle;
        h.Chat.ThenCall("set_env", JsonSerializer.Serialize(new { value = handle })).ThenText("Approve it?");
        var (_, events) = await h.SendAsync($"set DB_PASSWORD to {handle}");
        var planId = events.Single<AgentPlanView>("plan").Id;

        var outcome = await h.Plans.ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);

        JsonSerializer.Serialize(outcome).Should().NotContain(Value);
        var stored = await h.Db.AgentPlans.AsNoTracking().SingleAsync();
        stored.StepsJson.Should().Contain("secret:").And.NotContain(Value, "the plan stores the handle, never the value");
        stored.ResultsJson.Should().NotContain(Value);
        (await h.Db.AgentMessages.AnyAsync(m => (m.Text != null && m.Text.Contains(Value))
                                               || (m.SummaryEn != null && m.SummaryEn.Contains(Value)))).Should().BeFalse();
    }

    // ---- hardening pass ------------------------------------------------------------------------

    [Fact]
    public async Task A_value_whose_handle_expires_while_its_step_runs_is_still_scrubbed_from_what_the_step_said()
    {
        AgentHarness? harness = null;
        var setEnv = new FakeTool("set_env", AgentToolKind.SafeWrite)
        {
            OnExecute = (args, context) =>
            {
                var value = harness!.Vault.Resolve(context.WorkspaceId, context.UserId, AgentArgsOf(args));
                // The step takes long enough for the handle to lapse before the plan is finished.
                harness.Clock.UtcNow += AgentSecretHandles.Lifetime + TimeSpan.FromMinutes(1);
                return AgentToolResult.Failed($"The node rejected {value}.", $"نود {value} را نپذیرفت.");
            }
        };
        using var h = new AgentHarness([setEnv]);
        harness = h;
        var handle = h.Vault.Put(h.WorkspaceId, h.UserId, "DB_PASSWORD", Value).Handle;
        h.Chat.ThenCall("set_env", JsonSerializer.Serialize(new { value = handle })).ThenText("Approve it?");
        var (_, events) = await h.SendAsync($"set DB_PASSWORD to {handle}");

        var outcome = await h.Plans.ApproveAsync(h.Actor, events.Single<AgentPlanView>("plan").Id, new AgentApproveRequest(null, null), default);

        h.Vault.LiveValues(h.WorkspaceId, h.UserId).Should().BeEmpty("the handle lapsed during the step");
        JsonSerializer.Serialize(outcome).Should().NotContain(Value);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).ResultsJson.Should().NotContain(Value);
        (await h.Db.AgentMessages.AnyAsync(m => m.Text != null && m.Text.Contains(Value))).Should().BeFalse();
    }

    [Fact]
    public async Task A_value_typed_plainly_beside_its_handle_never_reaches_the_model_and_the_handle_survives_intact()
    {
        using var h = new AgentHarness();
        var handle = h.Vault.Put(h.WorkspaceId, h.UserId, "API_TOKEN", Value).Handle;
        h.Chat.ThenText("ok");

        await h.SendAsync($"use {handle} — it is {Value} by the way");

        var userMessage = h.Chat.Requests[0].Messages.Last(m => m.Role == AgentRole.User).Content!;
        userMessage.Should().Contain(handle, "a handle is not a value: it reaches the model exactly as written");
        userMessage.Should().NotContain(Value).And.NotContain("{{secret:[redacted]");
        h.Chat.EverythingSent().Should().NotContain(Value);
    }

    [Fact]
    public async Task Lines_a_person_reads_are_scrubbed_by_value_only_so_a_handle_in_them_stays_a_handle()
    {
        var held = "";
        var reader = new FakeTool("peek", AgentToolKind.Read)
        {
            OnExecute = (_, _) => new AgentToolResult(true, "{}", $"Uses {held}, which is {Value}", $"از {held} استفاده می‌کند")
        };
        using var h = new AgentHarness([reader]);
        held = h.Vault.Put(h.WorkspaceId, h.UserId, "API_TOKEN", Value).Handle;
        h.Chat.ThenCall("peek").ThenText("ok");

        var (_, events) = await h.SendAsync("peek");

        var summary = events.Where(e => e.Name == "tool").Select(e => ((AgentToolEventData)e.Data).SummaryEn).Last()!;
        summary.Should().Be($"Uses {held}, which is [redacted]", "the value is masked and the handle is left exactly as it was");
        (await h.Db.AgentMessages.SingleAsync(m => m.Role == AgentMessageRole.Tool)).SummaryEn.Should().Be(summary);
    }

    [Fact]
    public async Task A_card_and_its_summary_never_show_a_held_value_a_validation_put_in_them()
    {
        var careless = new FakeTool("set_env", AgentToolKind.SafeWrite)
        {
            OnValidate = (_, _, _) => new AgentValidation(true, TitleEn: $"Set TOKEN to {Value}", TitleFa: $"تنظیم {Value}",
                DetailEn: $"was {Value}", WarningsEn: [$"{Value} will be visible"], WarningsFa: [$"{Value} دیده می‌شود"])
        };
        using var h = new AgentHarness([careless]);
        h.Vault.Put(h.WorkspaceId, h.UserId, "TOKEN", Value);
        h.Chat.ThenCall("set_env").ThenText("Approve it?");

        var (_, events) = await h.SendAsync("set the token");

        var step = events.Single<AgentPlanView>("plan").Steps.Single();
        JsonSerializer.Serialize(step).Should().NotContain(Value);
        step.TitleEn.Should().Be("Set TOKEN to [redacted]");
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).StepsJson.Should().NotContain(Value);
        (await h.Db.AgentMessages.AnyAsync(m => (m.SummaryEn != null && m.SummaryEn.Contains(Value))
                                               || (m.Text != null && m.Text.Contains(Value)))).Should().BeFalse();
        h.Chat.EverythingSent().Should().NotContain(Value);
    }

    [Fact]
    public void The_bound_request_body_never_prints_its_value()
    {
        var body = new Harbora.Web.Controllers.AssistantSecretBody { Name = "STRIPE_KEY", Value = Value };

        body.ToString().Should().Contain("STRIPE_KEY").And.NotContain(Value).And.Contain(AgentSecretHandles.Masked);
        body.ToRequest().Should().Be(new AgentSecretRequest("STRIPE_KEY", Value));
    }

    [Fact]
    public void The_secrets_endpoint_caps_its_request_body_at_32_kilobytes()
    {
        // TestServer does not implement IHttpMaxRequestBodySizeFeature, so the limit cannot be exercised
        // over HTTP here; this pins the attribute Kestrel enforces in production.
        var method = typeof(Harbora.Web.Controllers.AssistantController).GetMethod("HoldSecret")!;
        var limit = method.GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute), false)
            .Cast<Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute>().Single();
        ((Microsoft.AspNetCore.Http.Metadata.IRequestSizeLimitMetadata)limit).MaxRequestBodySize.Should().Be(32L * 1024);
    }

    private static string AgentArgsOf(JsonElement args) => args.GetProperty("value").GetString()!;
}
