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

    /// <summary>A conversation with a plan claimed and executing, and an audit entry for each step that "ran".</summary>
    private async Task<(Guid Conversation, Guid Plan)> ExecutingAsync(int steps, params (int Index, bool Ok, string Action)[] ran)
    {
        var conversation = new AgentConversation { WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, Title = "deploy the shop" };
        var plan = new AgentPlan
        {
            WorkspaceId = _h.WorkspaceId, UserId = _h.UserId, ConversationId = conversation.Id, Status = AgentPlanStatus.Executing,
            StepsJson = AgentJson.Serialize(Enumerable.Range(0, steps).Select(i => Step(i, $"tool_{i}")).ToList()),
            ExpiresAt = AgentHarness.Start + AgentPlan.Lifetime, DecidedAt = AgentHarness.Start
        };
        _h.Db.AgentConversations.Add(conversation);
        _h.Db.AgentPlans.Add(plan);
        foreach (var (index, ok, action) in ran)
            _h.Db.AuditLogs.Add(new AuditLog
            {
                Action = action, ActorEmail = "person@example.com", TargetType = "agent_plan", TargetId = plan.Id.ToString(),
                WorkspaceId = _h.WorkspaceId,
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
        result.Steps[2].ErrorEn.Should().Be(AgentPlanService.PanelRestartedEn);

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
    public async Task A_step_that_failed_before_the_restart_and_one_never_audited_both_stop_the_plan_there()
    {
        var (_, failedFirst) = await ExecutingAsync(2, (0, false, AgentPlanService.StepExecutedAction));
        var (_, nothingRan) = await ExecutingAsync(1);

        (await Reconciler().ReconcileAsync(default)).Should().Be(2);

        AgentPlanService.ResultOf(Stored(failedFirst))!.Steps.Select(s => s.State).Should().Equal("failed", "skipped");
        var untouched = AgentPlanService.ResultOf(Stored(nothingRan))!;
        untouched.Steps.Single().State.Should().Be("failed");
        Stored(nothingRan).Status.Should().Be(AgentPlanStatus.Failed);
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
