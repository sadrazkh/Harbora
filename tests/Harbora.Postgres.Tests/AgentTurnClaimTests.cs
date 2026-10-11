using System.Security.Claims;
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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// A conversation's turn mark (<c>AgentConversation.TurnStartedAt</c>) against real Postgres: two
/// messages sent to one conversation at once start exactly one turn, and the turn clears its own mark at
/// the end — which depends on the stamp being stored to the microsecond Postgres keeps, since the clear
/// compares it for equality. EF InMemory runs neither the conditional update nor that comparison the way
/// Postgres does, so both are only proven here.
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class AgentTurnClaimTests(PostgresLane lane)
{
    private sealed class Clock : ISystemClock
    {
        // Ticks finer than a microsecond on purpose: the stamp must still round-trip.
        public DateTimeOffset UtcNow => new DateTimeOffset(2026, 10, 11, 12, 0, 0, 123, TimeSpan.Zero).AddTicks(4567);
    }

    /// <summary>Nobody here holds a lock-button secret.</summary>
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

    /// <summary>Answers once a gate opens, so a turn can be held open while a second message arrives.</summary>
    private sealed class GatedClient(Task gate) : IAgentChatClient
    {
        public Task<AssistantUnavailable?> GetAvailabilityAsync(CancellationToken ct) => Task.FromResult<AssistantUnavailable?>(null);
        public Task<string?> GetModelAsync(CancellationToken ct) => Task.FromResult<string?>("test/model");

        public async Task<AgentChatResponse> CompleteAsync(AgentChatRequest request, CancellationToken ct)
        {
            await gate.WaitAsync(ct);
            return new AgentChatResponse(true, new AgentChatMessage(AgentRole.Assistant, "Done."), "test/model",
                new AgentChatUsage(1, 1), "stop", null, null);
        }
    }

    private static AgentConversationService ServiceOver(HarboraDbContext db, IAgentChatClient chat, Guid userId, Guid workspaceId)
    {
        var clock = new Clock();
        var access = new ProjectAccessService(db, new Caller(userId, workspaceId));
        var registry = new AgentToolRegistry([], new Everyone());
        return new AgentConversationService(db, chat, registry,
            new AgentPlanService(db, registry, new NoAudit(), clock, NullLogger<AgentPlanService>.Instance, new NoSecrets()),
            new AgentUsageMeter(db, clock), new AgentWorkspaceSnapshot(db, new AgentResourceResolver(db, access), access),
            new FeatureGate(db), new Everyone(), clock, NullLogger<AgentConversationService>.Instance, new NoSecrets());
    }

    [PostgresFact]
    public async Task Two_messages_sent_at_once_start_one_turn_and_the_turn_clears_its_own_mark()
    {
        var connectionString = await lane.FreshlyMigratedAsync("agent_turn_claim");
        var userId = Guid.CreateVersion7();
        var workspaceId = Guid.CreateVersion7();
        var conversationId = Guid.CreateVersion7();
        await using (var seed = PostgresLane.Open(connectionString))
        {
            seed.Workspaces.Add(new Workspace { Id = workspaceId, Name = "turns", Slug = "turns" });
            seed.FeatureGrants.Add(new FeatureGrant
            {
                Scope = FeatureScope.Workspace, TargetId = workspaceId, FeatureKey = PlatformFeatures.Assistant, State = FeatureState.Enabled
            });
            seed.AgentConversations.Add(new AgentConversation { Id = conversationId, WorkspaceId = workspaceId, UserId = userId, Title = "race" });
            await seed.SaveChangesAsync();
        }

        var actor = new AgentActor(workspaceId, userId, new ClaimsPrincipal(new ClaimsIdentity("test")));
        var gate = new TaskCompletionSource();
        await using var dbA = PostgresLane.Open(connectionString);
        await using var dbB = PostgresLane.Open(connectionString);

        Task<AgentSendOutcome> Send(HarboraDbContext db, string text) =>
            ServiceOver(db, new GatedClient(gate.Task), userId, workspaceId).SendAsync(
                actor, conversationId, new AgentSendMessageRequest(text, null), false, (_, _) => Task.CompletedTask, default);

        var first = Send(dbA, "one");
        var second = Send(dbB, "two");

        // The loser is refused without waiting on the gate; then the winner may finish.
        var settled = await Task.WhenAny(first, second);
        (await settled).Status.Should().Be(AgentSendStatus.Busy, "the message that lost the claim is refused at once");
        gate.SetResult();
        var outcomes = await Task.WhenAll(first, second);

        outcomes.Count(o => o.Status == AgentSendStatus.Streamed).Should().Be(1);
        outcomes.Count(o => o.Status == AgentSendStatus.Busy).Should().Be(1);

        await using var verify = PostgresLane.Open(connectionString);
        (await verify.AgentConversations.SingleAsync(c => c.Id == conversationId)).TurnStartedAt
            .Should().BeNull("the winner cleared its own mark — the stamp round-tripped exactly");
        (await verify.AgentMessages.CountAsync(m => m.ConversationId == conversationId && m.Role == AgentMessageRole.User))
            .Should().Be(1, "only the message whose turn started was stored");
    }
}
