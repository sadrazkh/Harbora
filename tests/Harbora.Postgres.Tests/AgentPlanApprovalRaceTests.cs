using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// The agent's two conditional updates, proven against real Postgres: a plan's <c>pending →
/// executing</c> move (<c>AgentPlanService.TryMoveAsync</c>) and the daily cap's reservation
/// (<c>AgentUsageMeter.TryReserveAsync</c>).
///
/// <para>
/// EF InMemory cannot run <c>ExecuteUpdateAsync</c>, so in Harbora.Tests both fall back to a plain
/// read-then-write that is deliberately not atomic. This lane is the only place their central claims —
/// two concurrent approvals, one execution; two concurrent calls at the cap, one admitted — are checked
/// against a real row lock rather than hoped for. Shaped like <c>SignInTokenRedemptionTests</c>, whose
/// claim these follow.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class AgentPlanApprovalRaceTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock(DateTimeOffset now) : ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private sealed class Everyone : IAgentCapabilityCheck
    {
        public Task<bool> AllowsAsync(ClaimsPrincipal user, string capability, CancellationToken ct) => Task.FromResult(true);
    }

    private sealed class NoAudit : IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            Guid? workspaceId = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>A safe write that counts how often it really ran, across every context and thread.</summary>
    private sealed class CountingTool : IAgentTool
    {
        private int _executions;
        public int Executions => _executions;
        public string Name => "count_once";
        public string Description => "Counts.";
        public string ParametersSchema => """{ "type": "object" }""";
        public AgentToolKind Kind => AgentToolKind.SafeWrite;
        public string? Capability => null;

        public Task<AgentValidation> ValidateAsync(JsonElement args, AgentToolContext context, AgentPlanScope scope, CancellationToken ct) =>
            Task.FromResult(new AgentValidation(true, TitleEn: "Count", TitleFa: "شمارش"));

        public async Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            await Task.Delay(50, ct); // long enough for a second, unguarded execution to overlap this one
            return new AgentToolResult(true, """{"counted":true}""", "Counted", "شمرده شد");
        }
    }

    private static AgentPlanService ServiceOver(HarboraDbContext db, IAgentTool tool, Clock clock) =>
        new(db, new AgentToolRegistry([tool], new Everyone()), new NoAudit(), clock, NullLogger<AgentPlanService>.Instance);

    private static async Task<(AgentActor Actor, Guid PlanId)> SeedPlanAsync(string connectionString, IAgentTool tool, Clock clock)
    {
        await using var db = PostgresLane.Open(connectionString);
        var actor = new AgentActor(Guid.CreateVersion7(), Guid.CreateVersion7(), new ClaimsPrincipal(new ClaimsIdentity("test")));
        var conversation = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "race" };
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync();

        var plan = await ServiceOver(db, tool, clock).CreatePendingAsync(actor, conversation.Id,
            [new AgentPlanStep(0, tool.Name, tool.Kind, "{}", "call_1", "Count", "شمارش", null, null, [], [], null, false)],
            default);
        return (actor, plan.Id);
    }

    [PostgresFact]
    public async Task Two_concurrent_approvals_of_one_plan_execute_it_exactly_once()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_plan_race");
        var tool = new CountingTool();
        var (actor, planId) = await SeedPlanAsync(connectionString, tool, new Clock(Now));

        // Two independent connections, so the two conditional updates are two genuinely concurrent
        // statements against the one row Postgres has to arbitrate between.
        await using var dbA = PostgresLane.Open(connectionString);
        await using var dbB = PostgresLane.Open(connectionString);
        var request = new AgentApproveRequest(null, null);

        var outcomes = await Task.WhenAll(
            ServiceOver(dbA, tool, new Clock(Now)).ApproveAsync(actor, planId, request, default),
            ServiceOver(dbB, tool, new Clock(Now)).ApproveAsync(actor, planId, request, default));

        tool.Executions.Should().Be(1, "one plan, approved twice at once, runs once");
        outcomes.Count(o => o.Ok).Should().Be(1);
        outcomes.Single(o => !o.Ok).Refusal!.Code.Should().Be(AgentPlanRefusal.NotPendingCode);

        await using var verify = PostgresLane.Open(connectionString);
        var stored = await verify.AgentPlans.SingleAsync(p => p.Id == planId);
        stored.Status.Should().Be(AgentPlanStatus.Executed);
        (await verify.AgentMessages.CountAsync(m => m.PlanId == planId && m.Role == AgentMessageRole.Tool))
            .Should().Be(1, "the outcome is recorded by the one execution only");
    }

    [PostgresFact]
    public async Task Approving_on_the_context_that_created_the_plan_sees_the_claim_it_made()
    {
        // The tracked-entity trap SignInTokenService.RefreshTrackedAsync documents: ExecuteUpdateAsync
        // never touches the change tracker, so without a reload this context would keep reading the plan
        // as pending after claiming it.
        var connectionString = await lane.FreshlyMigratedAsync("agent_plan_tracked");
        var tool = new CountingTool();
        await using var db = PostgresLane.Open(connectionString);
        var actor = new AgentActor(Guid.CreateVersion7(), Guid.CreateVersion7(), new ClaimsPrincipal(new ClaimsIdentity("test")));
        var conversation = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "tracked" };
        db.AgentConversations.Add(conversation);
        await db.SaveChangesAsync();
        var service = ServiceOver(db, tool, new Clock(Now));
        var plan = await service.CreatePendingAsync(actor, conversation.Id,
            [new AgentPlanStep(0, tool.Name, tool.Kind, "{}", "call_1", "Count", "شمارش", null, null, [], [], null, false)], default);

        var outcome = await service.ApproveAsync(actor, plan.Id, new AgentApproveRequest(null, null), default);

        outcome.Result!.Status.Should().Be("executed");
        (await service.ApproveAsync(actor, plan.Id, new AgentApproveRequest(null, null), default)).Refusal!.Code
            .Should().Be(AgentPlanRefusal.NotPendingCode);
        tool.Executions.Should().Be(1);
    }

    [PostgresFact]
    public async Task An_expired_plan_is_refused_and_written_down_as_expired()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_plan_expired");
        var tool = new CountingTool();
        var (actor, planId) = await SeedPlanAsync(connectionString, tool, new Clock(Now));

        await using var db = PostgresLane.Open(connectionString);
        var outcome = await ServiceOver(db, tool, new Clock(Now + AgentPlan.Lifetime))
            .ApproveAsync(actor, planId, new AgentApproveRequest(null, null), default);

        outcome.Refusal!.Code.Should().Be(AgentPlanRefusal.ExpiredCode);
        tool.Executions.Should().Be(0);
        await using var verify = PostgresLane.Open(connectionString);
        (await verify.AgentPlans.SingleAsync(p => p.Id == planId)).Status.Should().Be(AgentPlanStatus.Expired);
    }

    [PostgresFact]
    public async Task Concurrent_calls_at_the_daily_cap_admit_exactly_as_many_as_are_left()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_usage_cap");
        var workspaceId = Guid.CreateVersion7();
        await using (var seed = PostgresLane.Open(connectionString))
        {
            seed.Settings.Add(new Setting { Key = AgentSettingKeys.DailyRequestLimit, Value = "3" });
            await seed.SaveChangesAsync();
        }

        // Six callers racing, the first of the day among them: the unique index settles who opens the
        // day's row, the conditional update settles who gets a call.
        var contexts = Enumerable.Range(0, 6).Select(_ => PostgresLane.Open(connectionString)).ToList();
        try
        {
            var admitted = await Task.WhenAll(contexts.Select(db => new AgentUsageMeter(db, new Clock(Now)).TryReserveAsync(workspaceId, default)));

            admitted.Count(a => a).Should().Be(3);
            await using var verify = PostgresLane.Open(connectionString);
            var rows = await verify.AgentUsageDays.Where(u => u.WorkspaceId == workspaceId).ToListAsync();
            rows.Should().ContainSingle().Which.ModelCalls.Should().Be(3);
        }
        finally
        {
            foreach (var db in contexts) await db.DisposeAsync();
        }
    }
}
