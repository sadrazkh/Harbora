using System.Data.Common;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Features;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Assistant;
using Harbora.Infrastructure.Features;
using Harbora.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// Deleting a conversation against real Postgres (round seven): the delete locks every plan row of the
/// conversation before its conditional <c>DELETE</c>, so it and an approval's claim on one of those rows are
/// serialised by the database — and a message's claim and a delete are serialised on the conversation's own
/// row. EF InMemory has neither row locks nor <c>ExecuteDeleteAsync</c>, so each ordering is forced here by
/// holding one side's transaction open and waiting until Postgres reports the other side blocked on it.
///
/// <para>
/// The first fact is the one a plain <c>DELETE … WHERE NOT EXISTS (an executing plan)</c> fails: the approval's
/// claim commits after the delete's snapshot, and the cascade then deletes the plan it has just claimed.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class AgentConversationDeleteRaceTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class NoSecrets : IAgentSecretValues
    {
        public IReadOnlyList<string> LiveValues(Guid workspaceId, Guid userId) => [];
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

    private sealed class Caller(Guid userId, Guid workspaceId) : ICurrentUser
    {
        public Guid? UserId => userId;
        public string? Email => "person@example.com";
        public bool IsAuthenticated => true;
        public Guid? WorkspaceId => workspaceId;
    }

    private sealed class Answers(string text) : IAgentChatClient
    {
        public Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) => Task.FromResult<AssistantUnavailable?>(null);
        public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult<string?>("test/model");

        public Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct) =>
            Task.FromResult(new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, text), "test/model",
                new AgentChatUsage(1, 1), "stop", null, null));
    }

    /// <summary>A safe write that counts how often it really ran.</summary>
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

        public Task<AgentToolResult> ExecuteAsync(JsonElement args, AgentToolContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _executions);
            return Task.FromResult(new AgentToolResult(true, """{"counted":true}""", "Counted", "شمرده شد"));
        }
    }

    /// <summary>Holds the delete's transaction open after its <c>DELETE</c> ran — locks taken, nothing committed.</summary>
    private sealed class HoldAfterDelete : DbCommandInterceptor
    {
        public TaskCompletionSource Deleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<int> NonQueryExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
            {
                Deleted.TrySetResult();
                await Release.Task;
            }
            return result;
        }
    }

    private static HarboraDbContext OpenWith(string connectionString, IInterceptor interceptor) =>
        new(new DbContextOptionsBuilder<HarboraDbContext>().UseNpgsql(connectionString).AddInterceptors(interceptor).Options,
            SystemWorkspaceScope.Instance);

    private static AgentToolRegistry Registry(IAgentTool tool) => new([tool], new Everyone());

    private static AgentPlanService Plans(HarboraDbContext db, IAgentTool tool, ILogger<AgentPlanService>? logger = null) =>
        new(db, Registry(tool), new NoAudit(), new Clock(), logger ?? NullLogger<AgentPlanService>.Instance, new NoSecrets());

    private static AgentConversationService Conversations(HarboraDbContext db, AgentActor actor, IAgentTool tool, IAgentChatClient? chat = null)
    {
        var access = new ProjectAccessService(db, new Caller(actor.UserId, actor.WorkspaceId));
        return new AgentConversationService(db, chat ?? new Answers("Done."), Registry(tool), Plans(db, tool),
            new AgentUsageMeter(db, new Clock()), new AgentWorkspaceSnapshot(db, new AgentResourceResolver(db, access), access),
            new FeatureGate(db), new Everyone(), new Clock(), NullLogger<AgentConversationService>.Instance, new NoSecrets(),
            audit: new NoAudit(), turns: new AgentTurnRegistry());
    }

    private static async Task<(AgentActor Actor, Guid ConversationId, Guid PlanId)> SeedAsync(
        string connectionString, IAgentTool tool, string title = "race")
    {
        await using var db = PostgresLane.Open(connectionString);
        var actor = new AgentActor(Guid.CreateVersion7(), Guid.CreateVersion7(), new ClaimsPrincipal(new ClaimsIdentity("test")));
        db.Workspaces.Add(new Workspace { Id = actor.WorkspaceId, Name = title, Slug = title + "-" + actor.WorkspaceId.ToString("N")[..8] });
        db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = FeatureScope.Workspace, TargetId = actor.WorkspaceId, FeatureKey = PlatformFeatures.Assistant, State = FeatureState.Enabled
        });
        var conversation = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = title };
        db.AgentConversations.Add(conversation);
        db.AgentMessages.Add(new AgentMessage
        {
            WorkspaceId = actor.WorkspaceId, ConversationId = conversation.Id, Sequence = 1, Role = AgentMessageRole.User, Text = "count once"
        });
        await db.SaveChangesAsync();

        var plan = await Plans(db, tool).CreatePendingAsync(actor, conversation.Id,
            [new AgentPlanStep(0, tool.Name, tool.Kind, "{}", "call_1", "Count", "شمارش", null, null, [], [], null, false)], default);
        return (actor, conversation.Id, plan.Id);
    }

    /// <summary>Waits until some session in this database is blocked on a row lock — the other side has reached it.</summary>
    private static async Task WaitForABlockedSessionAsync(string connectionString)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var waiting = await PostgresLane.ScalarAsync<long>(connectionString,
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
            if (waiting > 0) return;
            await Task.Delay(25);
        }
        throw new TimeoutException("Nothing ever blocked on the lock the test was holding.");
    }

    private static async Task<(int Conversations, int Messages, int Plans)> LeftAsync(string connectionString, Guid conversationId)
    {
        await using var db = PostgresLane.Open(connectionString);
        return (
            await db.AgentConversations.CountAsync(c => c.Id == conversationId),
            await db.AgentMessages.CountAsync(m => m.ConversationId == conversationId),
            await db.AgentPlans.CountAsync(p => p.ConversationId == conversationId));
    }

    [PostgresFact]
    public async Task A_delete_that_meets_an_approval_claiming_its_plan_waits_for_it_and_then_refuses()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_delete_vs_claim");
        var tool = new CountingTool();
        var (actor, conversationId, planId) = await SeedAsync(connectionString, tool);

        // The approval's claim — the same conditional UPDATE TryMoveAsync makes — in flight: run, not committed.
        await using var claim = await PostgresLane.ConnectAsync(connectionString);
        await using var transaction = await claim.BeginTransactionAsync();
        await using (var update = new NpgsqlCommand(
                         """UPDATE "AgentPlans" SET "Status" = 1 WHERE "Id" = @id AND "Status" = 0""", claim, transaction))
        {
            update.Parameters.AddWithValue("id", planId);
            (await update.ExecuteNonQueryAsync()).Should().Be(1);
        }

        await using var db = PostgresLane.Open(connectionString);
        var delete = Conversations(db, actor, tool).DeleteAsync(actor, conversationId, default);
        await WaitForABlockedSessionAsync(connectionString);
        delete.IsCompleted.Should().BeFalse("the delete waits for the claim rather than reading past it");

        await transaction.CommitAsync();
        var outcome = await delete;

        outcome.Status.Should().Be(AgentDeleteStatus.Busy, "the plan it waited for is executing now");
        (await LeftAsync(connectionString, conversationId)).Should().Be((1, 1, 1),
            "a plan claimed for execution never loses its conversation underneath it");
    }

    [PostgresFact]
    public async Task An_approval_that_meets_a_delete_waits_for_it_and_then_claims_nothing()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_claim_vs_delete");
        var tool = new CountingTool();
        var (actor, conversationId, planId) = await SeedAsync(connectionString, tool);

        var hold = new HoldAfterDelete();
        await using var deleting = OpenWith(connectionString, hold);
        var delete = Conversations(deleting, actor, tool).DeleteAsync(actor, conversationId, default);
        await hold.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await using var approving = PostgresLane.Open(connectionString);
        var approve = Plans(approving, tool).ApproveAsync(actor, planId, new AgentApproveRequest(null, null), default);
        await WaitForABlockedSessionAsync(connectionString);
        approve.IsCompleted.Should().BeFalse("the claim waits on the plan row the delete has locked");

        hold.Release.SetResult();
        (await delete).Status.Should().Be(AgentDeleteStatus.Deleted);
        var approval = await approve;

        approval.Refusal!.Code.Should().Be(AgentPlanRefusal.NotFoundCode, "there was nothing left to claim");
        tool.Executions.Should().Be(0);
        (await LeftAsync(connectionString, conversationId)).Should().Be((0, 0, 0), "the cascade took its messages and plans");
    }

    [PostgresFact]
    public async Task A_rejection_that_meets_a_delete_waits_for_it_and_is_then_not_found_not_a_server_error()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_reject_vs_delete");
        var tool = new CountingTool();
        var (actor, conversationId, planId) = await SeedAsync(connectionString, tool);

        var hold = new HoldAfterDelete();
        await using var deleting = OpenWith(connectionString, hold);
        var delete = Conversations(deleting, actor, tool).DeleteAsync(actor, conversationId, default);
        await hold.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await using var rejecting = PostgresLane.Open(connectionString);
        var reject = Plans(rejecting, tool).RejectAsync(actor, planId, default);
        await WaitForABlockedSessionAsync(connectionString);

        hold.Release.SetResult();
        (await delete).Status.Should().Be(AgentDeleteStatus.Deleted);

        (await reject)!.Code.Should().Be(AgentPlanRefusal.NotFoundCode,
            "the plan it had loaded was gone by the time it could move it — a 404, not an exception");
    }

    [PostgresFact]
    public async Task A_message_that_meets_a_delete_waits_for_it_and_is_then_not_found_never_busy()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_send_vs_delete");
        var tool = new CountingTool();
        var (actor, conversationId, _) = await SeedAsync(connectionString, tool);

        var hold = new HoldAfterDelete();
        await using var deleting = OpenWith(connectionString, hold);
        var delete = Conversations(deleting, actor, tool).DeleteAsync(actor, conversationId, default);
        await hold.Deleted.Task.WaitAsync(TimeSpan.FromSeconds(15));

        await using var sending = PostgresLane.Open(connectionString);
        var emitted = 0;
        var send = Conversations(sending, actor, tool).SendAsync(actor, conversationId,
            new AgentSendMessageRequest("are you there?", null), false, (_, _) => { emitted++; return Task.CompletedTask; }, default);
        await WaitForABlockedSessionAsync(connectionString);

        hold.Release.SetResult();
        (await delete).Status.Should().Be(AgentDeleteStatus.Deleted);
        var outcome = await send;

        outcome.Status.Should().Be(AgentSendStatus.NotFound);
        emitted.Should().Be(0);
        (await LeftAsync(connectionString, conversationId)).Should().Be((0, 0, 0), "nothing was written into it afterwards");
    }

    [PostgresFact]
    public async Task Delete_all_cascades_to_messages_and_plans_and_keeps_the_busy_ones_and_everybody_elses()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_delete_all");
        var tool = new CountingTool();
        var (actor, idle, _) = await SeedAsync(connectionString, tool, "idle");
        Guid answering, executing, theirs;
        await using (var db = PostgresLane.Open(connectionString))
        {
            var a = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "answering", TurnStartedAt = Now };
            var e = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "executing" };
            var t = new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = Guid.CreateVersion7(), Title = "theirs" };
            db.AgentConversations.AddRange(a, e, t);
            db.AgentPlans.Add(new AgentPlan
            {
                WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, ConversationId = e.Id, Status = AgentPlanStatus.Executing,
                StepsJson = "[]", ExpiresAt = Now
            });
            await db.SaveChangesAsync();
            (answering, executing, theirs) = (a.Id, e.Id, t.Id);
        }

        await using var context = PostgresLane.Open(connectionString);
        var result = await Conversations(context, actor, tool).DeleteAllAsync(actor, default);

        result.Should().Be(new AgentDeleteAllView(Deleted: 1, Skipped: 2));
        (await LeftAsync(connectionString, idle)).Should().Be((0, 0, 0));
        (await LeftAsync(connectionString, answering)).Conversations.Should().Be(1);
        (await LeftAsync(connectionString, executing)).Should().Be((1, 0, 1));
        (await LeftAsync(connectionString, theirs)).Conversations.Should().Be(1);
    }

    [PostgresFact]
    public async Task The_list_search_ignores_case_in_postgres_and_pages()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_list_search");
        var tool = new CountingTool();
        var (actor, _, _) = await SeedAsync(connectionString, tool, "Deploy the SHOP");
        await using (var db = PostgresLane.Open(connectionString))
        {
            db.AgentConversations.AddRange(
                new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "پایگاه دادهٔ فروشگاه", UpdatedAt = Now },
                new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "100% done_ish", UpdatedAt = Now },
                new AgentConversation { WorkspaceId = actor.WorkspaceId, UserId = actor.UserId, Title = "", UpdatedAt = Now });
            await db.SaveChangesAsync();
        }

        await using var context = PostgresLane.Open(connectionString);
        var service = Conversations(context, actor, tool);
        async Task<IEnumerable<string>> Titles(string q, int skip = 0) =>
            (await service.ListAsync(actor, q, skip, default)).Value!.Select(c => c.Title);

        (await Titles("shop")).Should().Equal("Deploy the SHOP");
        (await Titles("فروشگاه")).Should().Equal("پایگاه دادهٔ فروشگاه");
        (await Titles("%")).Should().Equal(["100% done_ish"], "a wildcard character in the search is searched for, not obeyed");
        (await Titles("_")).Should().Equal(["100% done_ish"]);
        (await Titles("")).Should().HaveCount(3, "the reserved, untitled one is never listed");
        (await Titles("", skip: 2)).Should().HaveCount(1);
    }
}
