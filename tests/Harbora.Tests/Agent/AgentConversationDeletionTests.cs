using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// Deleting conversations — one, or all of a person's — and the searchable, paged list (round seven). Over
/// EF InMemory, where the delete is a read-then-write: the concurrent races are proven against Postgres in
/// <c>Harbora.Postgres.Tests/AgentConversationDeleteRaceTests</c>, and what is checked here is every outcome
/// each side of those races can see, plus that the queries are ones Npgsql can translate.
/// </summary>
public class AgentConversationDeletionTests
{
    private static readonly DateTimeOffset Start = AgentHarness.Start;

    /// <summary>A conversation with two messages and, optionally, a plan in the given state.</summary>
    private static async Task<AgentConversation> SeedAsync(
        AgentHarness h, string title = "deploy the shop", Guid? workspaceId = null, Guid? userId = null,
        DateTimeOffset? turnStartedAt = null, AgentPlanStatus? plan = null, DateTimeOffset? updatedAt = null,
        DateTimeOffset? decidedAt = null)
    {
        var conversation = new AgentConversation
        {
            WorkspaceId = workspaceId ?? h.WorkspaceId, UserId = userId ?? h.UserId, Title = title,
            TurnStartedAt = turnStartedAt, CreatedAt = Start - TimeSpan.FromDays(1), UpdatedAt = updatedAt ?? Start
        };
        h.Db.AgentConversations.Add(conversation);
        h.Db.AgentMessages.AddRange(
            new AgentMessage
            {
                WorkspaceId = conversation.WorkspaceId, ConversationId = conversation.Id, Sequence = 1,
                Role = AgentMessageRole.User, Text = "what I asked about " + title
            },
            new AgentMessage
            {
                WorkspaceId = conversation.WorkspaceId, ConversationId = conversation.Id, Sequence = 2,
                Role = AgentMessageRole.Assistant, Text = "what it answered"
            });
        if (plan is { } status)
            h.Db.AgentPlans.Add(new AgentPlan
            {
                WorkspaceId = conversation.WorkspaceId, UserId = conversation.UserId, ConversationId = conversation.Id,
                Status = status, StepsJson = "[]", ExpiresAt = Start + AgentPlan.Lifetime, DecidedAt = decidedAt ?? Start
            });
        await h.Db.SaveChangesAsync();
        return conversation;
    }

    private static async Task<(int Conversations, int Messages, int Plans)> LeftOfAsync(AgentHarness h, Guid conversationId)
    {
        await using var db = h.NewContext();
        return (
            await db.AgentConversations.IgnoreQueryFilters().CountAsync(c => c.Id == conversationId),
            await db.AgentMessages.IgnoreQueryFilters().CountAsync(m => m.ConversationId == conversationId),
            await db.AgentPlans.IgnoreQueryFilters().CountAsync(p => p.ConversationId == conversationId));
    }

    // ---- one -----------------------------------------------------------------------------------

    [Fact]
    public async Task Deleting_ones_own_conversation_takes_its_messages_and_plans_and_audits_its_id_alone()
    {
        using var h = new AgentHarness();
        var doomed = await SeedAsync(h, title: "my bank password is in here", plan: AgentPlanStatus.Executed);
        var kept = await SeedAsync(h, title: "another one");
        h.Db.AgentUsageDays.Add(new AgentUsageDay { WorkspaceId = h.WorkspaceId, Day = DateOnly.FromDateTime(Start.UtcDateTime), ModelCalls = 7 });
        await h.Db.SaveChangesAsync();

        var outcome = await h.ServiceOver(h.NewContext()).DeleteAsync(h.Actor, doomed.Id, default);

        outcome.Status.Should().Be(AgentDeleteStatus.Deleted);
        (await LeftOfAsync(h, doomed.Id)).Should().Be((0, 0, 0));
        (await LeftOfAsync(h, kept.Id)).Should().Be((1, 2, 0), "only the one asked for goes");
        (await h.NewContext().AgentUsageDays.IgnoreQueryFilters().SingleAsync()).ModelCalls
            .Should().Be(7, "a deleted conversation's model calls were still made today");

        var audit = h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentConversationService.ConversationDeletedAction).Subject;
        audit.TargetId.Should().Be(doomed.Id.ToString());
        audit.WorkspaceId.Should().Be(h.WorkspaceId);
        (audit.MetadataJson ?? "").Should().NotContain("bank password", "the title is what the person asked to be rid of");
    }

    [Fact]
    public async Task Somebody_elses_conversation_and_the_same_persons_in_another_workspace_are_not_found_and_kept()
    {
        using var h = new AgentHarness();
        var mine = await SeedAsync(h);
        using var colleague = h.Another();
        var elsewhere = new AgentActor(Guid.CreateVersion7(), h.UserId, h.Principal);

        (await colleague.Service.DeleteAsync(colleague.Actor, mine.Id, default)).Status.Should().Be(AgentDeleteStatus.NotFound);
        var other = await h.Service.DeleteAsync(elsewhere, mine.Id, default);
        other.Status.Should().Be(AgentDeleteStatus.NotFound);
        other.MessageEn.Should().Be("There is no such conversation.");
        other.MessageFa.Should().NotBeNullOrEmpty();
        (await h.Service.DeleteAsync(h.Actor, Guid.NewGuid(), default)).Status.Should().Be(AgentDeleteStatus.NotFound);

        (await LeftOfAsync(h, mine.Id)).Should().Be((1, 2, 0));
        h.Audit.Entries.Should().BeEmpty("nothing was deleted, so nothing is recorded as deleted");
    }

    [Fact]
    public async Task A_conversation_still_answering_a_message_is_busy_and_kept()
    {
        using var h = new AgentHarness();
        var busy = await SeedAsync(h, turnStartedAt: Start - TimeSpan.FromSeconds(20));

        var outcome = await h.Service.DeleteAsync(h.Actor, busy.Id, default);

        outcome.Status.Should().Be(AgentDeleteStatus.Busy);
        outcome.MessageEn.Should().StartWith("This conversation is still answering a message");
        outcome.MessageFa.Should().NotBeNullOrEmpty();
        (await LeftOfAsync(h, busy.Id)).Should().Be((1, 2, 0));
    }

    [Fact]
    public async Task A_conversation_with_a_plan_executing_is_busy_and_kept()
    {
        using var h = new AgentHarness();
        var busy = await SeedAsync(h, plan: AgentPlanStatus.Executing);

        var outcome = await h.Service.DeleteAsync(h.Actor, busy.Id, default);

        outcome.Status.Should().Be(AgentDeleteStatus.Busy);
        outcome.MessageEn.Should().StartWith("A plan in this conversation is still running");
        outcome.MessageFa.Should().NotBeNullOrEmpty();
        (await LeftOfAsync(h, busy.Id)).Should().Be((1, 2, 1));
    }

    [Theory]
    [InlineData(AgentPlanStatus.Pending)]
    [InlineData(AgentPlanStatus.Executed)]
    [InlineData(AgentPlanStatus.Failed)]
    [InlineData(AgentPlanStatus.Rejected)]
    [InlineData(AgentPlanStatus.Expired)]
    public async Task A_plan_that_is_not_executing_goes_with_its_conversation(AgentPlanStatus status)
    {
        using var h = new AgentHarness();
        var conversation = await SeedAsync(h, plan: status);

        (await h.Service.DeleteAsync(h.Actor, conversation.Id, default)).Status.Should().Be(AgentDeleteStatus.Deleted);
        (await LeftOfAsync(h, conversation.Id)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_plan_executing_under_a_claim_older_than_30_minutes_no_longer_keeps_its_conversation()
    {
        using var h = new AgentHarness();
        var broken = await SeedAsync(h, plan: AgentPlanStatus.Executing,
            decidedAt: Start - AgentConversationDeletion.ExecutingPlanAbandonedAfter - TimeSpan.FromSeconds(1));
        var running = await SeedAsync(h, plan: AgentPlanStatus.Executing,
            decidedAt: Start - AgentConversationDeletion.ExecutingPlanAbandonedAfter + TimeSpan.FromMinutes(1));

        (await h.Service.DeleteAsync(h.Actor, broken.Id, default)).Status
            .Should().Be(AgentDeleteStatus.Deleted, "a run that broke while the process lived on must not block a delete for ever");
        (await LeftOfAsync(h, broken.Id)).Should().Be((0, 0, 0));
        (await h.Service.DeleteAsync(h.Actor, running.Id, default)).Status.Should().Be(AgentDeleteStatus.Busy);
        (await LeftOfAsync(h, running.Id)).Should().Be((1, 2, 1));
    }

    [Fact]
    public async Task A_stale_turn_mark_left_by_a_dead_process_does_not_keep_a_conversation()
    {
        using var h = new AgentHarness();
        var stale = await SeedAsync(h,
            turnStartedAt: Start - AgentConversationService.DefaultTurnDeadline - AgentConversationService.TurnMarkGrace);

        (await h.Service.DeleteAsync(h.Actor, stale.Id, default)).Status.Should().Be(AgentDeleteStatus.Deleted);
        (await LeftOfAsync(h, stale.Id)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_message_to_a_deleted_conversation_is_not_found_and_nothing_is_stored_or_sent()
    {
        using var h = new AgentHarness();
        h.Chat.ThenText("Hello.");
        var (_, events) = await h.SendAsync("hello");
        var id = events.Single<AgentStartedData>("started").ConversationId;
        (await h.ServiceOver(h.NewContext()).DeleteAsync(h.Actor, id, default)).Status.Should().Be(AgentDeleteStatus.Deleted);
        h.Chat.Clear();
        h.Chat.ThenText("never");

        using var db = h.NewContext();
        var outcome = await h.ServiceOver(db).SendAsync(h.Actor, id, new AgentSendMessageRequest("still there?", null), false,
            (_, _) => Task.CompletedTask, default);

        outcome.Status.Should().Be(AgentSendStatus.NotFound);
        h.Chat.Requests.Should().BeEmpty();
        (await LeftOfAsync(h, id)).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task A_delete_that_lands_between_reading_the_conversation_and_claiming_it_makes_the_message_not_found_never_busy()
    {
        using var h = new AgentHarness();
        var conversation = await SeedAsync(h);
        h.Chat.ThenText("never");

        // The delete lands exactly between the send's read of the row and its claim of the turn.
        var interceptor = new DeleteBeforeClaim(() => h.ServiceOver(h.NewContext()).DeleteAsync(h.Actor, conversation.Id, default));
        using var db = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName).AddInterceptors(interceptor).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));
        var events = new List<AgentStreamEvent>();

        var outcome = await h.ServiceOver(db).SendAsync(h.Actor, conversation.Id, new AgentSendMessageRequest("hi", null), false,
            (e, _) => { events.Add(e); return Task.CompletedTask; }, default);

        interceptor.Deleted!.Status.Should().Be(AgentDeleteStatus.Deleted, "the delete ran before any turn was claimed");
        outcome.Status.Should().Be(AgentSendStatus.NotFound, "a conversation that is gone is not one still answering");
        events.Should().BeEmpty();
        h.Chat.Requests.Should().BeEmpty();
        (await LeftOfAsync(h, conversation.Id)).Should().Be((0, 0, 0), "nothing was written to it afterwards");
    }

    /// <summary>Runs a delete from another request the first time this context is about to save a turn claim.</summary>
    private sealed class DeleteBeforeClaim(Func<Task<AgentDeleteOutcome>> delete) : SaveChangesInterceptor
    {
        public AgentDeleteOutcome? Deleted { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var claiming = eventData.Context!.ChangeTracker.Entries<AgentConversation>()
                .Any(e => e.State == EntityState.Modified && e.Property(c => c.TurnStartedAt).IsModified);
            if (claiming && Deleted is null) Deleted = await delete();
            return result;
        }
    }

    [Fact]
    public async Task A_plan_whose_conversation_was_deleted_cannot_be_approved_and_nothing_runs()
    {
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""").ThenText("Approve it?");
        var (_, events) = await h.SendAsync("make a database");
        var planId = events.Single<AgentPlanView>("plan").Id;
        var conversationId = events.Single<AgentStartedData>("started").ConversationId;

        (await h.ServiceOver(h.NewContext()).DeleteAsync(h.Actor, conversationId, default)).Status
            .Should().Be(AgentDeleteStatus.Deleted, "a card waiting for a decision is not a plan running");

        using var db = h.NewContext();
        var approval = await new AgentPlanService(db, h.Registry, h.Audit, h.Clock,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentPlanService>.Instance, h.Vault)
            .ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);

        approval.Refusal!.Code.Should().Be(AgentPlanRefusal.NotFoundCode);
        create.Executions.Should().Be(0);
        h.Audit.Entries.Should().NotContain(e => e.Action == AgentPlanService.StepExecutedAction);
    }

    [Fact]
    public async Task A_plan_whose_conversation_vanishes_while_it_runs_writes_nothing_throws_nothing_and_says_so_in_the_log()
    {
        // The window the delete's lock closes on Postgres, forced open: the step itself removes the plan and
        // its conversation behind the busy check's back, as a race the lock did not cover would.
        var create = new FakeTool("create_database", AgentToolKind.SafeWrite);
        using var h = new AgentHarness([create]);
        h.Chat.ThenCall("create_database", """{"name":"shop-db"}""").ThenText("Approve it?");
        var (_, events) = await h.SendAsync("make a database");
        var planId = events.Single<AgentPlanView>("plan").Id;
        var conversationId = events.Single<AgentStartedData>("started").ConversationId;
        create.OnExecuteAsync = async (_, _) =>
        {
            await using var other = h.NewContext();
            other.AgentMessages.RemoveRange(other.AgentMessages.Where(m => m.ConversationId == conversationId));
            other.AgentPlans.RemoveRange(other.AgentPlans.Where(p => p.ConversationId == conversationId));
            other.AgentConversations.RemoveRange(other.AgentConversations.Where(c => c.Id == conversationId));
            await other.SaveChangesAsync();
            return new AgentToolResult(true, """{"created":"shop-db"}""", "Created shop-db", "shop-db ساخته شد");
        };
        var logger = new ListLogger<AgentPlanService>();

        using var db = h.NewContext();
        var approval = await new AgentPlanService(db, h.Registry, h.Audit, h.Clock, logger, h.Vault)
            .ApproveAsync(h.Actor, planId, new AgentApproveRequest(null, null), default);

        approval.Ok.Should().BeTrue("the person approved and the step ran; nothing is thrown at them");
        approval.Result!.Steps.Single().State.Should().Be("done");
        create.Executions.Should().Be(1);
        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentPlanService.StepExecutedAction, "what ran is audited");
        (await LeftOfAsync(h, conversationId)).Should().Be((0, 0, 0), "no outcome was written into a conversation that is gone");
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Warning
                                                   && e.Text.Contains("deleted while it ran"));
    }

    // ---- all -----------------------------------------------------------------------------------

    [Fact]
    public async Task Delete_all_deletes_every_listed_conversation_keeps_and_counts_the_busy_ones_and_touches_nobody_elses()
    {
        using var h = new AgentHarness();
        var idle = new[] { await SeedAsync(h, "one"), await SeedAsync(h, "two", plan: AgentPlanStatus.Executed), await SeedAsync(h, "three") };
        var answering = await SeedAsync(h, "answering", turnStartedAt: Start - TimeSpan.FromSeconds(5));
        var executing = await SeedAsync(h, "executing", plan: AgentPlanStatus.Executing);
        var reserved = await SeedAsync(h, title: "");
        using var colleague = h.Another();
        var colleagues = await SeedAsync(h, "theirs", userId: colleague.UserId);
        var elsewhere = await SeedAsync(h, "mine, elsewhere", workspaceId: Guid.CreateVersion7());

        var result = await h.ServiceOver(h.NewContext()).DeleteAllAsync(h.Actor, default);

        result.Should().Be(new AgentDeleteAllView(Deleted: 3, Skipped: 2));
        foreach (var c in idle) (await LeftOfAsync(h, c.Id)).Should().Be((0, 0, 0));
        (await LeftOfAsync(h, answering.Id)).Conversations.Should().Be(1);
        (await LeftOfAsync(h, executing.Id)).Should().Be((1, 2, 1));
        (await LeftOfAsync(h, reserved.Id)).Conversations.Should().Be(1, "the pane's reserved, empty conversation is not in the list");
        (await LeftOfAsync(h, colleagues.Id)).Should().Be((1, 2, 0));
        (await LeftOfAsync(h, elsewhere.Id)).Should().Be((1, 2, 0));

        var audit = h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentConversationService.ConversationsDeletedAction).Subject;
        audit.MetadataJson.Should().Be("""{"deleted":3,"skipped":2}""");
        audit.WorkspaceId.Should().Be(h.WorkspaceId);
    }

    [Fact]
    public async Task Delete_all_with_nothing_to_delete_says_so()
    {
        using var h = new AgentHarness();

        (await h.Service.DeleteAllAsync(h.Actor, default)).Should().Be(new AgentDeleteAllView(0, 0));
    }

    [Fact]
    public async Task Delete_all_whose_later_batch_fails_still_audits_and_logs_what_the_earlier_batches_deleted()
    {
        using var h = new AgentHarness();
        // One more than a batch, so there are two batches; no messages, to keep the seed small.
        for (var i = 0; i <= AgentConversationDeletion.BatchSize; i++)
            h.Db.AgentConversations.Add(new AgentConversation { WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = $"c{i}", UpdatedAt = Start });
        await h.Db.SaveChangesAsync();
        var logger = new ListLogger<AgentConversationService>();
        using var failing = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName)
                .AddInterceptors(new FailTheSecondDelete()).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));

        var act = () => h.ServiceOver(failing, logger: logger).DeleteAllAsync(h.Actor, default);

        await act.Should().ThrowAsync<DbUpdateException>("the failure still reaches the request");
        using var db = h.NewContext();
        (await db.AgentConversations.CountAsync()).Should().Be(1, "the first batch was deleted and stays deleted");
        h.Audit.Entries.Should().ContainSingle(e => e.Action == AgentConversationService.ConversationsDeletedAction)
            .Which.MetadataJson.Should().Be($$"""{"deleted":{{AgentConversationDeletion.BatchSize}},"interrupted":true}""");
        logger.Entries.Should().ContainSingle(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Error)
            .Which.Text.Should().Contain($"{AgentConversationDeletion.BatchSize} had been deleted");
    }

    [Fact]
    public async Task A_failing_audit_on_the_way_out_of_a_part_way_delete_all_does_not_replace_the_real_failure()
    {
        using var h = new AgentHarness();
        for (var i = 0; i <= AgentConversationDeletion.BatchSize; i++)
            h.Db.AgentConversations.Add(new AgentConversation { WorkspaceId = h.WorkspaceId, UserId = h.UserId, Title = $"c{i}", UpdatedAt = Start });
        await h.Db.SaveChangesAsync();
        using var failing = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(h.DatabaseName)
                .AddInterceptors(new FailTheSecondDelete()).Options,
            new Harbora.Application.Abstractions.FixedWorkspaceScope(h.WorkspaceId));

        var act = () => h.ServiceOver(failing, audit: new ThrowingAudit()).DeleteAllAsync(h.Actor, default);

        (await act.Should().ThrowAsync<DbUpdateException>()).WithMessage("The database refused the second batch.",
            "the failure that stopped the delete is the one the request sees, not the audit's");
    }

    /// <summary>An audit sink that breaks — the contract says a logger never throws, and this one does anyway.</summary>
    private sealed class ThrowingAudit : Harbora.Application.Abstractions.IAuditLogger
    {
        public Task LogAsync(string action, string? targetType = null, string? targetId = null, string? ipAddress = null,
            string? actorEmailOverride = null, Guid? userIdOverride = null, string? metadataJson = null,
            Guid? workspaceId = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("The audit table is gone.");
    }

    /// <summary>Lets the first save that deletes conversations through and refuses the second.</summary>
    private sealed class FailTheSecondDelete : SaveChangesInterceptor
    {
        private int _deletes;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<AgentConversation>().Any(e => e.State == EntityState.Deleted)
                && ++_deletes == 2)
                throw new DbUpdateException("The database refused the second batch.");
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    // ---- the list ------------------------------------------------------------------------------

    [Fact]
    public async Task The_list_finds_a_title_by_any_part_of_it_ignoring_case_and_never_lists_a_reserved_conversation()
    {
        using var h = new AgentHarness();
        await SeedAsync(h, "Deploy the SHOP", updatedAt: Start - TimeSpan.FromMinutes(3));
        await SeedAsync(h, "why did my shop's build fail", updatedAt: Start - TimeSpan.FromMinutes(2));
        await SeedAsync(h, "پایگاه دادهٔ فروشگاه را بساز", updatedAt: Start - TimeSpan.FromMinutes(1));
        await SeedAsync(h, "", updatedAt: Start);
        using var colleague = h.Another();
        await SeedAsync(h, "their shop", userId: colleague.UserId);

        async Task<IEnumerable<string>> Titles(string? q) =>
            (await h.Service.ListAsync(h.Actor, q, 0, default)).Value!.Select(c => c.Title);

        (await Titles("  sHoP ")).Should().Equal("why did my shop's build fail", "Deploy the SHOP");
        (await Titles("فروشگاه")).Should().Equal("پایگاه دادهٔ فروشگاه را بساز");
        (await Titles("nothing like it")).Should().BeEmpty();
        (await Titles(null)).Should().HaveCount(3, "no search lists every titled conversation of this person");
        (await Titles("   ")).Should().HaveCount(3, "a blank search is no search");
    }

    [Fact]
    public async Task The_list_pages_with_skip_most_recently_active_first_without_repeating_or_dropping_a_row()
    {
        using var h = new AgentHarness();
        // Two share each instant, so the order inside an instant has to be decided by something stable.
        var all = new List<AgentConversation>();
        for (var i = 0; i < 37; i++)
            all.Add(await SeedAsync(h, $"conversation {i}", updatedAt: Start - TimeSpan.FromMinutes(i / 2)));

        var first = (await h.Service.ListAsync(h.Actor, null, 0, default)).Value!;
        var second = (await h.Service.ListAsync(h.Actor, null, AgentConversationService.ConversationListSize, default)).Value!;
        var beyond = (await h.Service.ListAsync(h.Actor, null, 37, default)).Value!;

        first.Should().HaveCount(AgentConversationService.ConversationListSize);
        second.Should().HaveCount(7, "a page shorter than a full one is the last");
        beyond.Should().BeEmpty();
        first.Concat(second).Select(c => c.Id).Should().OnlyHaveUniqueItems().And.BeEquivalentTo(all.Select(c => c.Id));
        first.Concat(second).Select(c => c.UpdatedAt).Should().BeInDescendingOrder();
        (await h.Service.ListAsync(h.Actor, default)).Select(c => c.Id).Should().Equal(first.Select(c => c.Id),
            "the old overload is the first page, unsearched");
    }

    [Fact]
    public async Task A_search_longer_than_100_characters_or_a_negative_skip_is_refused_in_both_languages()
    {
        using var h = new AgentHarness();
        await SeedAsync(h, new string('a', 50));

        var tooLong = await h.Service.ListAsync(h.Actor, new string('a', AgentConversationService.MaxSearchLength + 1), 0, default);
        tooLong.Ok.Should().BeFalse();
        tooLong.Refusals.Single().Message.Should().Be("A search can be at most 100 characters.");
        tooLong.Refusals.Single().MessageFa.Should().NotBeNullOrEmpty();

        (await h.Service.ListAsync(h.Actor, new string('a', 100), 0, default)).Ok.Should().BeTrue();
        (await h.Service.ListAsync(h.Actor, "  " + new string('a', 100) + "  ", 0, default)).Ok
            .Should().BeTrue("the length is counted after trimming");

        var negative = await h.Service.ListAsync(h.Actor, null, -1, default);
        negative.Ok.Should().BeFalse();
        negative.Refusals.Single().MessageFa.Should().NotBeNullOrEmpty();
    }

    // ---- what Postgres is sent -----------------------------------------------------------------

    private static HarboraDbContext PostgresContext() => new(
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseNpgsql("Host=unused;Database=unused;Username=unused;Password=unused")
            .Options);

    [Fact]
    public void The_search_is_one_Npgsql_translates_into_sql_rather_than_refusing()
    {
        // InMemory evaluates any C#; Npgsql refuses what it cannot translate, and only at run time. Asking
        // for the SQL compiles the query the way the real provider will — no database needed.
        using var db = PostgresContext();

        var sql = AgentConversationService.Page(db.AgentConversations, Guid.NewGuid(), Guid.NewGuid(), "Shop", 30).ToQueryString();

        sql.Should().Contain("lower(", "the title is lowered by the database");
        sql.Should().MatchRegex("(?i)(strpos|LIKE)", "and searched inside, not compared whole");
        sql.Should().Contain("ORDER BY").And.Contain("OFFSET").And.Contain("LIMIT");
    }

    [Fact]
    public void The_delete_locks_the_plans_and_checks_for_an_executing_one_in_sql()
    {
        using var db = PostgresContext();

        var locks = AgentConversationDeletion.LockPlans(db, [Guid.NewGuid()]).ToQueryString();
        var idle = db.AgentConversations.IgnoreQueryFilters()
            .Where(AgentConversationDeletion.Idle(db, AgentBusyCut.At(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(3)))).ToQueryString();

        locks.Should().Contain("FOR UPDATE").And.Contain("ANY(").And.Contain("ORDER BY");
        idle.Should().Contain("EXISTS").And.Contain("\"AgentPlans\"").And.Contain("TurnStartedAt")
            .And.Contain("COALESCE", "a claim's age is DecidedAt, falling back to UpdatedAt, worked out by the database");
    }

    // ---- the registry --------------------------------------------------------------------------

    [Fact]
    public void A_stop_reaches_only_its_owners_turn_and_a_released_or_replaced_turn_is_not_stopped()
    {
        var registry = new AgentTurnRegistry();
        var conversation = Guid.NewGuid();
        var workspace = Guid.NewGuid();
        var owner = Guid.NewGuid();

        var first = registry.Register(conversation, workspace, owner);
        registry.Stop(conversation, workspace, Guid.NewGuid()).Should().BeFalse("somebody else's turn");
        registry.Stop(conversation, Guid.NewGuid(), owner).Should().BeFalse("the same person in another workspace");
        first.StopRequested.Should().BeFalse();
        first.Token.IsCancellationRequested.Should().BeFalse();

        // A turn that took over a stale mark replaces the old registration; the old turn's release must not
        // remove the new one.
        var second = registry.Register(conversation, workspace, owner);
        registry.Release(first).Should().BeFalse("nobody asked the old turn to stop");
        registry.Stop(conversation, workspace, owner).Should().BeTrue();
        second.StopRequested.Should().BeTrue();
        first.StopRequested.Should().BeFalse();

        registry.Release(second).Should().BeTrue("the release reports the Stop that reached the turn first, which the turn must honour");
        registry.Release(second).Should().BeTrue("and keeps reporting it: a release is idempotent");
        registry.Stop(conversation, workspace, owner).Should().BeFalse("a turn whose work is over has nothing to stop");
        registry.Stop(Guid.NewGuid(), workspace, owner).Should().BeFalse();
    }
}
