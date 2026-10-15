using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Auditing;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// A plan a restart left executing (round seven, review fix): settled at startup, from what each step recorded
/// as it ran, so it neither claims for ever to be running nor keeps its conversation undeletable. Runs here over
/// a context whose workspace filter would hide every row, as a sessionless startup step must not depend on one.
/// </summary>
public sealed class AgentPlanStartupReconcilerTests : IDisposable
{
    private readonly AgentHarness _h = new();
    private readonly ServiceProvider _provider;
    private readonly ListLogger<AgentPlanStartupReconciler> _log = new();

    public AgentPlanStartupReconcilerTests()
    {
        _provider = new ServiceCollection()
            .AddSingleton<IWorkspaceScope>(new FixedWorkspaceScope(Guid.CreateVersion7()))
            .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_h.DatabaseName))
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _h.Dispose();
    }

    private AgentPlanStartupReconciler Reconciler() =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _h.Clock, _log);

    private static AgentPlanStep Step(int index, string tool) =>
        new(index, tool, AgentToolKind.SafeWrite, "{}", $"call_{index}", $"Run {tool}", $"اجرای {tool}", null, null, [], [], null, false);

    /// <summary>When the conversations here were last used: long before the restart, as a plan cut off by one is.</summary>
    private static readonly DateTimeOffset LastUsed = AgentHarness.Start - TimeSpan.FromDays(20);

    /// <summary>A conversation with a plan claimed and executing, and an audit entry for each step that "ran".</summary>
    private Task<(Guid Conversation, Guid Plan)> ExecutingAsync(int steps, params (int Index, bool Ok, string Action)[] ran) =>
        ExecutingAsync(steps, ran.Select(r => (r.Index, r.Ok, r.Action, At: AgentHarness.Start + TimeSpan.FromSeconds(1 + r.Index))).ToArray());

    /// <summary>The same, with when each audit entry was written: the plan was claimed at <see cref="AgentHarness.Start"/>.</summary>
    private async Task<(Guid Conversation, Guid Plan)> ExecutingAsync(int steps, (int Index, bool Ok, string Action, DateTimeOffset At)[] ran)
    {
        var conversation = new AgentConversation
        {
            WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, Title = "deploy the shop", CreatedAt = LastUsed, UpdatedAt = LastUsed
        };
        var plan = new AgentPlan
        {
            WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, ConversationId = conversation.Id, Status = AgentPlanStatus.Executing,
            StepsJson = AgentJson.Serialize(Enumerable.Range(0, steps).Select(i => Step(i, $"tool_{i}")).ToList()),
            ExpiresAt = AgentHarness.Start + AgentPlan.Lifetime, DecidedAt = AgentHarness.Start
        };
        _h.Db.AgentConversations.Add(conversation);
        _h.Db.AgentPlans.Add(plan);
        foreach (var (index, ok, action, at) in ran)
            _h.Db.AuditLogs.Add(new AuditLog
            {
                Action = action, ActorEmail = "person@example.com", TargetType = "agent_plan", TargetId = plan.Id.ToString(),
                WorkspaceId = _h.WorkspaceId, CreatedAt = at,
                MetadataJson = JsonSerializer.Serialize(new { conversationId = conversation.Id, planId = plan.Id, step = index + 1, stepIndex = index, tool = $"tool_{index}", ok })
            });
        await _h.Db.SaveChangesAsync();
        return (conversation.Id, plan.Id);
    }

    private AgentPlan Stored(Guid planId)
    {
        using var db = _h.NewContext();
        return db.AgentPlans.IgnoreQueryFilters().AsNoTracking().Single(p => p.Id == planId);
    }

    [Fact]
    public async Task A_plan_a_restart_left_executing_fails_from_its_running_step_keeping_what_ran()
    {
        var (conversationId, planId) = await ExecutingAsync(3, (0, true, AgentPlanService.StepExecutedAction));
        (await _h.Service.DeleteAsync(_h.Actor, conversationId, default)).Status
            .Should().Be(AgentDeleteStatus.Busy, "before the restart is settled, the plan still counts as running");

        var settled = await Reconciler().ReconcileAsync(default);

        settled.Should().Be(1);
        var plan = Stored(planId);
        plan.Status.Should().Be(AgentPlanStatus.Failed);
        plan.ExecutedAt.Should().NotBeNull("the result is shown: what the running step did is what the person must check");
        var result = AgentPlanService.ResultOf(plan)!;
        result.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped");
        result.Steps[1].ErrorEn.Should().Be("The panel restarted while this plan was running — check the resource before trying again.");
        result.Steps[1].ErrorFa.Should().Be("پنل هنگام اجرای این برنامه دوباره راه‌اندازی شد — پیش از تلاش دوباره، منبع را بررسی کنید.");
        // The step after it did not run, so — like every skipped step — the reason is its summary, not an error.
        result.Steps[2].SummaryEn.Should().Be(AgentPlanService.PanelRestartedEn);
        result.Steps[2].SummaryFa.Should().Be(AgentPlanService.PanelRestartedFa);
        result.Steps[2].ErrorEn.Should().BeNull();
        result.Steps[2].ErrorFa.Should().BeNull();

        // The conversation is told, so the next turn knows the plan did not finish.
        using (var db = _h.NewContext())
        {
            var told = await db.AgentMessages.IgnoreQueryFilters()
                .Where(m => m.ConversationId == conversationId && m.PlanId == planId && m.Role == AgentMessageRole.Tool)
                .OrderBy(m => m.Sequence).Select(m => m.Text!).ToListAsync();
            told.Should().HaveCount(3);
            told[0].Should().Contain("\"state\":\"done\"");
            told[1].Should().Contain("\"state\":\"failed\"").And.Contain("panel restarted");
            told[2].Should().Contain("\"state\":\"skipped\"");
        }

        (await _h.ServiceOver(_h.NewContext()).DeleteAsync(_h.Actor, conversationId, default)).Status
            .Should().Be(AgentDeleteStatus.Deleted, "a settled plan no longer keeps its conversation");
        _log.Entries.Should().Contain(e => e.Text.Contains("Settled 1 assistant plan(s)"));
    }

    [Fact]
    public async Task A_plan_whose_every_step_had_finished_is_executed_whatever_session_wrote_its_entries()
    {
        var (_, planId) = await ExecutingAsync(2,
            (0, true, AgentPlanService.StepExecutedAction),
            (1, true, "support." + AgentPlanService.StepExecutedAction));

        await Reconciler().ReconcileAsync(default);

        var plan = Stored(planId);
        plan.Status.Should().Be(AgentPlanStatus.Executed, "only the record was cut off; every step had run and worked");
        AgentPlanService.ResultOf(plan)!.Steps.Select(s => s.State).Should().Equal("done", "done");
    }

    [Fact]
    public async Task A_step_that_failed_before_the_restart_keeps_its_own_failure_and_only_a_running_step_gets_the_restart_words()
    {
        var (failedConversation, failedFirst) = await ExecutingAsync(3,
            (0, true, AgentPlanService.StepExecutedAction), (1, false, AgentPlanService.StepExecutedAction));
        var (_, nothingRan) = await ExecutingAsync(1);

        (await Reconciler().ReconcileAsync(default)).Should().Be(2);

        // Step 2 ran and failed before the restart; step 3 was skipped by the run itself, as after any failure.
        // Neither was cut off by the restart, so neither says it was.
        var failed = AgentPlanService.ResultOf(Stored(failedFirst))!;
        failed.Steps.Select(s => s.State).Should().Equal("done", "failed", "skipped");
        failed.Steps[1].ErrorEn.Should().Be("The step failed.");
        failed.Steps[1].ErrorFa.Should().Be("این مرحله ناموفق بود.");
        failed.Steps[2].SummaryEn.Should().Be("Skipped: an earlier step failed.");
        failed.Steps.Should().NotContain(s => s.ErrorEn == AgentPlanService.PanelRestartedEn || s.SummaryEn == AgentPlanService.PanelRestartedEn);
        using (var db = _h.NewContext())
            (await db.AgentMessages.IgnoreQueryFilters()
                    .Where(m => m.ConversationId == failedConversation && m.PlanId == failedFirst && m.Role == AgentMessageRole.Tool)
                    .Select(m => m.Text!).ToListAsync())
                .Should().NotContain(t => t.Contains(AgentPlanService.PanelRestartedEn), "no line tells the model the restart cut a step off");
        Stored(failedFirst).Status.Should().Be(AgentPlanStatus.Failed);

        // A step with no entry at all was the one running: that one, and only that kind, gets the restart words.
        var cut = AgentPlanService.ResultOf(Stored(nothingRan))!;
        cut.Steps.Single().State.Should().Be("failed");
        cut.Steps.Single().ErrorEn.Should().Be(AgentPlanService.PanelRestartedEn);
        Stored(nothingRan).Status.Should().Be(AgentPlanStatus.Failed);
    }

    [Fact]
    public async Task Settling_a_plan_does_not_move_its_conversation_up_the_list_or_restart_its_retention_clock()
    {
        var (conversationId, planId) = await ExecutingAsync(2, (0, true, AgentPlanService.StepExecutedAction));
        _h.Clock.UtcNow = AgentHarness.Start + TimeSpan.FromDays(3); // the restart, days later

        (await Reconciler().ReconcileAsync(default)).Should().Be(1);

        using var db = _h.NewContext();
        var conversation = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking().SingleAsync(c => c.Id == conversationId);
        conversation.UpdatedAt.Should().Be(LastUsed, "nobody used the conversation: the panel only wrote down what the restart cut off");
        (await db.AgentMessages.IgnoreQueryFilters().CountAsync(m => m.PlanId == planId)).Should().BeGreaterThan(0,
            "the conversation is still told, so the next turn knows");
    }

    [Fact]
    public void The_audit_read_is_bounded_below_by_the_claim_so_the_CreatedAt_index_carries_it()
    {
        using var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused").Options);
        var plan = new AgentPlan { ConversationId = Guid.NewGuid(), StepsJson = "[]", DecidedAt = AgentHarness.Start };

        var sql = AgentPlanService.StepEntriesOf(db, plan).ToQueryString();

        sql.Should().Contain("\"CreatedAt\" >=", "only entries written since the run was claimed are read");
        sql.Should().Contain("\"TargetId\" =");
    }

    [Fact]
    public async Task An_entry_from_before_the_claim_is_not_the_runs_own()
    {
        // The bound is the claim: an entry dated before it cannot have been written by this run's steps.
        var (_, planId) = await ExecutingAsync(1, [(0, true, AgentPlanService.StepExecutedAction, AgentHarness.Start - TimeSpan.FromMinutes(1))]);

        await Reconciler().ReconcileAsync(default);

        var result = AgentPlanService.ResultOf(Stored(planId))!;
        result.Steps.Single().State.Should().Be("failed", "nothing this run wrote says the step finished");
        result.Steps.Single().ErrorEn.Should().Be(AgentPlanService.PanelRestartedEn);
    }

    [Fact]
    public async Task Plans_that_are_not_executing_are_left_alone_and_a_start_with_nothing_to_settle_is_quiet()
    {
        _h.Db.AgentConversations.Add(new AgentConversation { WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, Title = "t" });
        await _h.Db.SaveChangesAsync();
        var conversation = await _h.Db.AgentConversations.SingleAsync();
        foreach (var status in new[] { AgentPlanStatus.Pending, AgentPlanStatus.Executed, AgentPlanStatus.Failed, AgentPlanStatus.Rejected, AgentPlanStatus.Expired })
            _h.Db.AgentPlans.Add(new AgentPlan
            {
                WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, ConversationId = conversation.Id, Status = status, StepsJson = "[]",
                ExpiresAt = AgentHarness.Start
            });
        await _h.Db.SaveChangesAsync();

        (await Reconciler().ReconcileAsync(default)).Should().Be(0);
        using var db = _h.NewContext();
        (await db.AgentPlans.IgnoreQueryFilters().CountAsync(p => p.ResultsJson != null)).Should().Be(0);
        _log.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task A_startup_that_cannot_reach_the_database_is_logged_and_does_not_stop_the_panel()
    {
        var broken = new ServiceCollection().BuildServiceProvider();
        var reconciler = new AgentPlanStartupReconciler(broken.GetRequiredService<IServiceScopeFactory>(), _h.Clock, _log);

        await reconciler.Invoking(r => r.StartAsync(default)).Should().NotThrowAsync();
        _log.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error);
    }

    [Fact]
    public void It_is_a_hosted_service_the_container_can_build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Harbora:MasterKey"] = Convert.ToBase64String(SHA256.HashData("tests"u8.ToArray()))
        }).Build();
        var services = new ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);

        services.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(AgentPlanStartupReconciler));
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<AgentPlanStartupReconciler>(provider).Should().NotBeNull();
    }
}
