using FluentAssertions;
using Harbora.Domain.Agent;
using Harbora.Domain.Common;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The app tools driven through the real agent loop and plan engine (<see cref="AgentConversationService"/>,
/// <see cref="AgentPlanService"/>) by a scripted model — the four scenarios in the wave-B brief: a plan
/// whose second step names what its first creates, a remembered safe tool running without a card and
/// keeping a secret out of everything it reports, a typed confirmation for a delete, and a dangerous
/// tool that is carded whatever has been remembered.
/// </summary>
public class AppToolsScenarioTests
{
    private const string Secret = "sk_live_SCENARIO_9f2c";

    [Fact]
    public async Task Create_an_app_and_give_it_a_domain_is_one_plan_of_two_safe_steps_that_runs_in_order_on_approval()
    {
        using var h = new AppToolsHarness();
        var chat = h.Agent.Chat;
        chat.ThenCalls(
                chat.Call("create_app", """{"name":"web","image":"nginx:alpine","port":80}"""),
                chat.Call("add_domain", """{"app":"web","host":"web.example.com"}"""))
            .ThenText("Plan ready: create web from nginx:alpine and add web.example.com. It is waiting for your approval.");

        var (_, events) = await h.Agent.SendAsync("create an app from image nginx:alpine called web, port 80, and add domain web.example.com");

        events.Names().Should().Equal("started", "message", "plan", "done");
        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => (s.Index, s.Tool, s.Kind)).Should().Equal((0, "create_app", "safe"), (1, "add_domain", "safe"));
        plan.Steps[0].TitleEn.Should().Be("Create app web");
        plan.Steps[1].TitleEn.Should().Be("Add domain web.example.com to web");
        plan.Steps[1].DetailEn.Should().Contain("created by an earlier step of this plan", "the domain step validated against the planned app");
        chat.Requests[1].Messages.Where(m => m.Role == AgentRole.Tool).Select(m => m.Content)
            .Should().AllSatisfy(c => c.Should().StartWith("Added as step"));
        (await h.Db.Apps.CountAsync()).Should().Be(0, "nothing runs before the person approves");

        var approved = await h.Agent.Plans.ApproveAsync(h.Agent.Actor, plan.Id, new AgentApproveRequest(null, null), default);

        approved.Ok.Should().BeTrue(approved.Refusal?.MessageEn);
        approved.Result!.Status.Should().Be("executed");
        approved.Result.Steps.Select(s => s.State).Should().Equal("done", "done");
        var app = await h.Db.Apps.AsNoTracking().Include(a => a.Domains).SingleAsync(a => a.Slug == "web");
        app.Domains.Select(d => d.Host).Should().Contain("web.example.com");
        h.Deployments.Queued.Should().ContainSingle().Which.Trigger.Should().Be(DeploymentTrigger.Assistant);
        approved.Result.Steps[0].Links.Should().Contain(l => l.Url == $"/apps/details/{app.Id}")
            .And.Contain(l => l.Kind == "deployment");
        approved.Result.Steps[1].Links.Should().ContainSingle().Which.Url.Should().Be($"/apps/details/{app.Id}");
        h.Agent.Audit.Entries.Count(e => e.Action == AgentPlanService.StepExecutedAction).Should().Be(2);
        (await h.Db.AgentPlans.AsNoTracking().SingleAsync()).Status.Should().Be(AgentPlanStatus.Executed);
    }

    [Fact]
    public async Task A_remembered_set_env_vars_runs_without_a_card_and_its_secret_value_is_reported_nowhere()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        var chat = h.Agent.Chat;

        // Turn 1: carded; approved with "don't ask again".
        chat.ThenCall("set_env_vars", """{"app":"web","variables":[{"key":"LOG_LEVEL","value":"debug"}]}""")
            .ThenText("Setting LOG_LEVEL needs your approval.");
        var (_, first) = await h.Agent.SendAsync("set LOG_LEVEL=debug on web");
        var card = first.Single<AgentPlanView>("plan");
        card.Steps.Single().Rememberable.Should().BeTrue();
        (await h.Agent.Plans.ApproveAsync(h.Agent.Actor, card.Id, new AgentApproveRequest(null, ["set_env_vars"]), default))
            .Ok.Should().BeTrue();
        (await h.Agent.Plans.RememberedToolsAsync(h.Agent.Actor, default)).Should().Contain("set_env_vars");

        // Turn 2: the same tool, now remembered — it runs at once, no card. The person never typed the
        // value into the chat; the model passes it in the call.
        var conversationId = first.Single<AgentStartedData>("started").ConversationId;
        chat.ThenCall("set_env_vars", $$"""{"app":"web","variables":[{"key":"STRIPE_KEY","value":"{{Secret}}","secret":true}]}""")
            .ThenText("STRIPE_KEY will be stored as a secret.");
        var (_, second) = await h.Agent.SendAsync("store the stripe key from the vault as a secret", conversationId);

        second.Names().Should().Equal("started", "message", "executed", "done");
        var executed = second.Single<AgentPlanResultView>("executed");
        executed.Status.Should().Be("executed");
        executed.Steps.Single().State.Should().Be("done");
        (await h.Db.EnvironmentVariables.AsNoTracking().SingleAsync(v => v.Key == "STRIPE_KEY")).Value.Should().Be("enc:" + Secret);

        // Turn 3: the conversation goes on, with the executed step in its history.
        chat.ThenText("Anything else?");
        await h.Agent.SendAsync("thanks", conversationId);

        // The value is in no tool result, no system prompt, no user or assistant text the model was sent.
        // The only place it travels is the model's own set_env_vars arguments, which the agent core replays
        // as part of the conversation (and keeps in the plan to run the step) — reported as a concern.
        foreach (var message in chat.Requests.SelectMany(r => r.Messages))
        {
            (message.Content ?? "").Should().NotContain(Secret, $"a {message.Role} message's text must not carry it");
            foreach (var call in message.ToolCalls ?? [])
                if (call.ArgumentsJson.Contains(Secret)) call.Name.Should().Be("set_env_vars");
        }

        // Nor anything the person is shown or the agent stores as words.
        var shown = string.Join("\n", second.Select(e => System.Text.Json.JsonSerializer.Serialize(e.Data)));
        shown.Should().NotContain(Secret);
        var stored = await h.Db.AgentMessages.AsNoTracking().ToListAsync();
        stored.Should().AllSatisfy(m => string.Join("|", m.Text, m.SummaryEn, m.SummaryFa).Should().NotContain(Secret));
        (await h.Db.AgentPlans.AsNoTracking().Select(p => p.ResultsJson).ToListAsync())
            .Should().AllSatisfy(json => (json ?? "").Should().NotContain(Secret));
        h.Agent.Audit.Entries.Should().AllSatisfy(e => (e.MetadataJson ?? "").Should().NotContain(Secret));
    }

    [Fact]
    public async Task Delete_app_waits_for_the_typed_slug_a_wrong_one_deletes_nothing_and_the_right_one_deletes()
    {
        using var h = new AppToolsHarness();
        var app = h.SeedApp("web");
        h.Agent.Chat.ThenCall("delete_app", """{"app":"web"}""").ThenText("Type web on the card to delete it.");

        var (_, events) = await h.Agent.SendAsync("delete the web app");

        var step = events.Single<AgentPlanView>("plan").Steps.Single();
        step.Kind.Should().Be("destructive");
        step.ConfirmationPhrase.Should().Be("web");
        step.AutoApproved.Should().BeFalse();
        step.Rememberable.Should().BeFalse();
        var planId = events.Single<AgentPlanView>("plan").Id;

        var wrong = await h.Agent.Plans.ApproveAsync(h.Agent.Actor, planId,
            new AgentApproveRequest(new Dictionary<int, string> { [0] = "Web" }, null), default);

        wrong.Ok.Should().BeFalse();
        wrong.Refusal!.Code.Should().Be(AgentPlanRefusal.ConfirmationMismatchCode);
        (await h.Db.Apps.AsNoTracking().AnyAsync(a => a.Id == app.Id)).Should().BeTrue();
        h.Operations.Calls.Should().BeEmpty();

        var right = await h.Agent.Plans.ApproveAsync(h.Agent.Actor, planId,
            new AgentApproveRequest(new Dictionary<int, string> { [0] = "web" }, null), default);

        right.Ok.Should().BeTrue(right.Refusal?.MessageEn);
        right.Result!.Steps.Single().State.Should().Be("done");
        (await h.Db.Apps.IgnoreQueryFilters().AsNoTracking().AnyAsync(a => a.Id == app.Id)).Should().BeFalse();
        h.Agent.Audit.Entries.Should().Contain(e => e.Action == "app.delete");
    }

    [Fact]
    public async Task Rollback_is_carded_even_when_every_safe_tool_has_been_remembered()
    {
        using var h = new AppToolsHarness();
        h.SeedApp("web");
        foreach (var safe in h.AllTools().Where(t => t.Kind == AgentToolKind.SafeWrite)) h.Agent.Remember(safe.Name);
        h.Agent.Chat.ThenCalls(
                h.Agent.Chat.Call("set_env_vars", """{"app":"web","variables":[{"key":"LOG_LEVEL","value":"warn"}]}"""),
                h.Agent.Chat.Call("rollback_app", """{"app":"web"}"""))
            .ThenText("Waiting for your approval.");

        var (_, events) = await h.Agent.SendAsync("quieter logs on web, and roll it back to the previous version");

        events.Names().Should().Contain("plan").And.NotContain("executed");
        var plan = events.Single<AgentPlanView>("plan");
        plan.Steps.Select(s => (s.Tool, s.Kind, s.AutoApproved)).Should().Equal(
            ("set_env_vars", "safe", true), ("rollback_app", "dangerous", false));
        plan.Steps[1].Rememberable.Should().BeFalse();
        plan.Steps[1].TitleEn.Should().Be("Roll back web to deployment #1");
        h.Deployments.Queued.Should().BeEmpty("nothing in the plan runs while one step waits for a person");
        (await h.Db.EnvironmentVariables.AnyAsync(v => v.Key == "LOG_LEVEL")).Should().BeFalse(
            "not even the remembered safe step");

        var approved = await h.Agent.Plans.ApproveAsync(h.Agent.Actor, plan.Id, new AgentApproveRequest(null, ["rollback_app"]), default);

        approved.Ok.Should().BeTrue(approved.Refusal?.MessageEn);
        (await h.Agent.Plans.RememberedToolsAsync(h.Agent.Actor, default)).Should().NotContain("rollback_app",
            "a dangerous tool can never be remembered");
        h.Deployments.Queued.Should().Contain(r => r.RollbackToDeploymentId != null);
    }
}
