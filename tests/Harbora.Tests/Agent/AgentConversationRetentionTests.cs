using System.Security.Cryptography;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Agent;
using Harbora.Domain.Settings;
using Harbora.Infrastructure.Agent;
using Harbora.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Harbora.Tests.Agent;

/// <summary>
/// The owner's conversation retention (round seven): the settings box that sets it, and the once-a-day sweep
/// that applies it across every workspace. The sweep runs here over a context whose workspace filter would
/// hide every row — the shape of a request scoped to somebody — so a single filtered read anywhere in it
/// would make it delete nothing and report a clean run.
/// </summary>
public sealed class AgentConversationRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 3, 0, 0, TimeSpan.Zero);

    private readonly string _database = "agent-retention-" + Guid.NewGuid();
    private readonly MutableClock _clock = new(Now);
    private readonly ListLogger<AgentConversationRetentionSweeper> _log = new();
    private readonly RecordingAuditLogger _audit = new();
    private readonly ServiceProvider _provider;

    public AgentConversationRetentionTests()
    {
        _provider = new ServiceCollection()
            // A scope that sees one workspace nobody here uses: every filtered read comes back empty.
            .AddSingleton<IWorkspaceScope>(new FixedWorkspaceScope(Guid.CreateVersion7()))
            .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_database))
            .AddScoped<AgentSettingsService>()
            .AddSingleton<IAuditLogger>(_audit)
            .BuildServiceProvider();
    }

    public void Dispose() => _provider.Dispose();

    private AgentConversationRetentionSweeper Sweeper(TimeSpan? startupDelay = null, TimeSpan? interval = null) =>
        new(_provider.GetRequiredService<IServiceScopeFactory>(), _clock, _log,
            startupDelay ?? TimeSpan.Zero, interval ?? TimeSpan.FromDays(1));

    private void Seed(Action<HarboraDbContext> seed)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        seed(db);
        db.SaveChanges();
    }

    private T Read<T>(Func<HarboraDbContext, T> read)
    {
        using var scope = _provider.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<HarboraDbContext>());
    }

    private void SetRetention(string value) => Seed(db =>
        db.Settings.Add(new Setting { Key = AgentSettingKeys.ConversationRetentionDays, Value = value }));

    private Guid Conversation(
        Guid workspaceId, TimeSpan unusedFor, string title = "a chat", DateTimeOffset? turnStartedAt = null, AgentPlanStatus? plan = null,
        DateTimeOffset? claimedAt = null)
    {
        var conversation = new AgentConversation
        {
            WorkspaceId = workspaceId, UserId = Guid.CreateVersion7(), Title = title, TurnStartedAt = turnStartedAt,
            CreatedAt = Now - unusedFor, UpdatedAt = Now - unusedFor
        };
        Seed(db =>
        {
            db.AgentConversations.Add(conversation);
            db.AgentMessages.Add(new AgentMessage
            {
                WorkspaceId = workspaceId, ConversationId = conversation.Id, Sequence = 1, Role = AgentMessageRole.User, Text = "hello"
            });
            if (plan is { } status)
                db.AgentPlans.Add(new AgentPlan
                {
                    WorkspaceId = workspaceId, UserId = conversation.UserId, ConversationId = conversation.Id, Status = status,
                    StepsJson = "[]", ExpiresAt = Now, DecidedAt = claimedAt ?? Now - TimeSpan.FromMinutes(5)
                });
        });
        return conversation.Id;
    }

    private bool Exists(Guid conversationId) =>
        Read(db => db.AgentConversations.IgnoreQueryFilters().Any(c => c.Id == conversationId));

    // ---- the sweep -----------------------------------------------------------------------------

    [Fact]
    public async Task The_sweep_deletes_only_old_idle_conversations_in_every_workspace_and_keeps_the_busy_ones()
    {
        SetRetention("30");
        var shop = Guid.CreateVersion7();
        var blog = Guid.CreateVersion7();
        var old = Conversation(shop, TimeSpan.FromDays(31), plan: AgentPlanStatus.Executed);
        var oldElsewhere = Conversation(blog, TimeSpan.FromDays(400));
        var oldReserved = Conversation(blog, TimeSpan.FromDays(45), title: "");
        var recent = Conversation(shop, TimeSpan.FromDays(29));
        var answering = Conversation(shop, TimeSpan.FromDays(60), turnStartedAt: Now - TimeSpan.FromSeconds(10));
        var executing = Conversation(blog, TimeSpan.FromDays(60), plan: AgentPlanStatus.Executing);
        var deadMark = Conversation(shop, TimeSpan.FromDays(60), turnStartedAt: Now - TimeSpan.FromHours(1));
        var brokenRun = Conversation(blog, TimeSpan.FromDays(60), plan: AgentPlanStatus.Executing, claimedAt: Now - TimeSpan.FromHours(2));

        Read(db => db.AgentConversations.Count()).Should().Be(0, "the filter this context carries hides every row");

        var result = await Sweeper().RunOnceAsync(default);

        result.Should().Be(new AgentRetentionSweepResult(Days: 30, Deleted: 5, Kept: 2));
        foreach (var gone in new[] { old, oldElsewhere, oldReserved, deadMark, brokenRun }) Exists(gone).Should().BeFalse();
        foreach (var kept in new[] { recent, answering, executing }) Exists(kept).Should().BeTrue();
        Read(db => db.AgentMessages.IgnoreQueryFilters().Count()).Should().Be(3, "the messages went with their conversations");
        Read(db => db.AgentPlans.IgnoreQueryFilters().Select(p => p.ConversationId).ToList()).Should().Equal(executing);

        var line = _log.Entries.Should().ContainSingle().Subject;
        line.Level.Should().Be(LogLevel.Information);
        line.Text.Should().StartWith(AgentConversationRetentionSweeper.LogPrefix)
            .And.Contain("deleted 5 conversation(s) not used for 30 day(s)")
            .And.Contain("kept 2");
        _audit.Entries.Should().ContainSingle().Which.Should().Match<RecordingAuditLogger.Entry>(e =>
            e.Action == AgentConversationRetentionSweeper.AuditAction && e.WorkspaceId == null
            && e.MetadataJson == "{\"deleted\":5,\"kept\":2,\"days\":30,\"interrupted\":false}");
    }

    [Fact]
    public async Task A_sweep_stopped_part_way_still_logs_and_audits_what_it_had_deleted()
    {
        SetRetention("30");
        // One more than a batch: the shutdown lands after the first batch is saved.
        Seed(db =>
        {
            for (var i = 0; i <= AgentConversationDeletion.BatchSize; i++)
                db.AgentConversations.Add(new AgentConversation
                {
                    WorkspaceId = Guid.CreateVersion7(), UserId = Guid.CreateVersion7(), Title = $"c{i}", UpdatedAt = Now - TimeSpan.FromDays(90)
                });
        });
        using var stopping = new CancellationTokenSource();
        using var provider = new ServiceCollection()
            .AddSingleton<IWorkspaceScope>(new FixedWorkspaceScope(Guid.CreateVersion7()))
            .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(_database).AddInterceptors(new CancelAfterFirstDelete(stopping)))
            .AddScoped<AgentSettingsService>()
            .AddSingleton<IAuditLogger>(_audit)
            .BuildServiceProvider();
        var sweeper = new AgentConversationRetentionSweeper(provider.GetRequiredService<IServiceScopeFactory>(), _clock, _log,
            TimeSpan.Zero, TimeSpan.FromDays(1));

        (await sweeper.RunOnceAsync(stopping.Token)).Should().BeNull("the pass did not finish");

        Read(db => db.AgentConversations.IgnoreQueryFilters().Count()).Should().Be(1, "the first batch is deleted and stays deleted");
        _log.Entries.Should().ContainSingle().Which.Text.Should().StartWith(AgentConversationRetentionSweeper.LogPrefix)
            .And.Contain($"after deleting {AgentConversationDeletion.BatchSize} conversation(s)");
        _audit.Entries.Should().ContainSingle().Which.MetadataJson.Should()
            .Be($"{{\"deleted\":{AgentConversationDeletion.BatchSize},\"kept\":null,\"days\":30,\"interrupted\":true}}");
    }

    /// <summary>Lets the first save that deletes conversations through, then starts the host's shutdown.</summary>
    private sealed class CancelAfterFirstDelete(CancellationTokenSource stopping) : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (!stopping.IsCancellationRequested) stopping.Cancel();
            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("not a number")]
    public async Task With_retention_blank_zero_or_unreadable_the_sweep_deletes_nothing_and_says_it_is_off(string? stored)
    {
        if (stored is not null) SetRetention(stored);
        var ancient = Conversation(Guid.CreateVersion7(), TimeSpan.FromDays(3000));

        var result = await Sweeper().RunOnceAsync(default);

        result.Should().Be(new AgentRetentionSweepResult(0, 0, 0));
        Exists(ancient).Should().BeTrue();
        _log.Entries.Should().ContainSingle().Which.Text.Should().StartWith(AgentConversationRetentionSweeper.LogPrefix).And.Contain("off");
    }

    [Fact]
    public async Task A_sweep_with_nothing_old_enough_still_logs_its_one_line()
    {
        SetRetention("7");
        Conversation(Guid.CreateVersion7(), TimeSpan.FromDays(1));

        (await Sweeper().RunOnceAsync(default)).Should().Be(new AgentRetentionSweepResult(7, 0, 0));
        _log.Entries.Should().ContainSingle().Which.Text.Should().Contain("deleted 0 conversation(s)");
        _audit.Entries.Should().BeEmpty("a pass that deleted nothing has nothing to put on the record");
    }

    [Fact]
    public async Task A_sweep_that_fails_is_logged_and_never_thrown()
    {
        var broken = new ServiceCollection().BuildServiceProvider(); // no database registered at all
        var sweeper = new AgentConversationRetentionSweeper(broken.GetRequiredService<IServiceScopeFactory>(), _clock, _log,
            TimeSpan.Zero, TimeSpan.FromDays(1));

        (await sweeper.RunOnceAsync(default)).Should().BeNull();
        _log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Error);
    }

    [Fact]
    public async Task The_timer_runs_a_first_pass_shortly_after_start()
    {
        SetRetention("30");
        var old = Conversation(Guid.CreateVersion7(), TimeSpan.FromDays(90));
        using var sweeper = Sweeper(startupDelay: TimeSpan.FromMilliseconds(20), interval: TimeSpan.FromHours(1));

        await sweeper.StartAsync(default);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (Exists(old) && DateTime.UtcNow < deadline) await Task.Delay(20);
        await sweeper.StopAsync(default);

        Exists(old).Should().BeFalse();
    }

    // ---- the container -------------------------------------------------------------------------

    private static ServiceCollection Registered()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Harbora:MasterKey"] = Convert.ToBase64String(SHA256.HashData("tests"u8.ToArray()))
        }).Build();
        var services = new ServiceCollection();
        Harbora.Infrastructure.DependencyInjection.AddHarboraInfrastructure(services, config);
        return services;
    }

    [Fact]
    public void The_sweep_is_a_hosted_service_the_container_can_build_and_the_turn_registry_is_one_for_the_process()
    {
        var services = Registered();
        services.Should().ContainSingle(d => d.ServiceType == typeof(IHostedService)
                                             && d.ImplementationType == typeof(AgentConversationRetentionSweeper));
        services.Should().ContainSingle(d => d.ServiceType == typeof(AgentTurnRegistry)).Which.Lifetime
            .Should().Be(ServiceLifetime.Singleton, "Stop and the turn it stops are two requests that must meet in one registry");

        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        ActivatorUtilities.CreateInstance<AgentConversationRetentionSweeper>(provider).Should().NotBeNull();
    }

    // ---- the setting ---------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData(" 90 ", "90")]
    [InlineData("۹۰", "90")]
    [InlineData("3650", "3650")]
    public void A_retention_of_blank_zero_or_1_to_3650_days_is_accepted(string? typed, string stored)
    {
        var result = AgentSettingsService.Validate(null, null, null, typed);

        result.Ok.Should().BeTrue();
        result.Input!.ConversationRetentionDays.Should().Be(stored);
    }

    [Theory]
    [InlineData("3651")]
    [InlineData("-1")]
    [InlineData("+5")]
    [InlineData("1.5")]
    [InlineData("1,000")]
    [InlineData("a month")]
    [InlineData("99999999999")]
    public void Any_other_retention_is_refused_in_both_languages(string typed)
    {
        var result = AgentSettingsService.Validate(null, null, null, typed);

        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("Keep conversations").And.Contain("3650");
        result.ErrorFa.Should().Contain("نگه‌داری گفت‌وگوها");
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0", 0)]
    [InlineData("30", 30)]
    [InlineData("3650", 3650)]
    [InlineData("3651", 0)]
    [InlineData("-3", 0)]
    [InlineData("soon", 0)]
    public void A_stored_retention_the_page_would_not_have_accepted_keeps_everything(string stored, int days) =>
        AgentSettingsService.EffectiveRetentionDays(stored).Should().Be(days);

    [Fact]
    public async Task The_retention_is_saved_and_read_back_beside_the_other_agent_settings()
    {
        await using var scope = _provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<AgentSettingsService>();

        await service.SaveAsync(AgentSettingsService.Validate("vendor/model:free", null, "120", "۳۰").Input!, default);
        var read = await service.GetAsync(default);

        read.ConversationRetentionDaysText.Should().Be("30");
        read.ConversationRetentionDays.Should().Be(30);
        read.DailyRequestLimit.Should().Be(120);

        await service.SaveAsync(AgentSettingsService.Validate("vendor/model:free", null, "120", "").Input!, default);
        (await service.GetAsync(default)).ConversationRetentionDays.Should().Be(0, "blanking the box keeps conversations for ever again");
    }
}
