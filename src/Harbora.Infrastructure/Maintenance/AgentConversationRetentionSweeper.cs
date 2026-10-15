using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Infrastructure.Agent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Harbora.Infrastructure.Maintenance;

/// <summary>
/// Deletes the assistant's conversations nobody has used for as many days as the owner chose in Settings →
/// Assistant (<see cref="AgentSettingKeys.ConversationRetentionDays"/>), in every workspace, once a day.
///
/// <para>
/// <b>Not <see cref="DataRetentionSweeper"/>.</b> That one is driven by configuration, and production
/// overrides it to keep everything; this one is the owner's own choice in the panel, read on every run, so
/// changing the box takes effect at the next pass without a restart. Blank or 0 — the default — deletes
/// nothing.
/// </para>
///
/// <para>
/// <b>No signed-in user, so no query filter.</b> It runs outside any request, and every read here ignores
/// the workspace filter and says which rows it means in its own predicate: a tenant filter in a sweeper
/// reads an empty table and reports success, which has happened in this codebase before. A conversation is
/// judged by <c>UpdatedAt</c> — moved by every message and every plan outcome — and a busy one (a live turn,
/// a plan executing) is kept exactly as the person's own "delete all" keeps it
/// (<see cref="AgentConversationDeletion"/>). Its messages and plans go with it; the audit log does not.
/// </para>
///
/// <para>
/// <b>Once a day, the first pass ten minutes after start</b> — off the boot path, and not a full day away,
/// for the reason <see cref="DanglingImageSweeper"/> gives: a panel that redeploys itself at least daily
/// would otherwise never sweep. <b>One line per run</b>, a run that deleted nothing and a run that is off
/// included, and it never throws into the host. A run that deleted anything also writes one platform audit
/// row (<see cref="AuditAction"/>) — also when it failed or was stopped part-way, with what it had deleted.
/// </para>
/// </summary>
public sealed class AgentConversationRetentionSweeper : BackgroundService
{
    /// <summary>The fixed start of every line this sweep logs.</summary>
    internal const string LogPrefix = "Assistant conversation retention:";

    /// <summary>The audit row of a pass that deleted something: <c>{ deleted, kept, days, interrupted }</c>.</summary>
    public const string AuditAction = "assistant.conversations_expired";

    internal static readonly TimeSpan DefaultStartupDelay = TimeSpan.FromMinutes(10);
    internal static readonly TimeSpan DefaultInterval = TimeSpan.FromDays(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ISystemClock _clock;
    private readonly ILogger<AgentConversationRetentionSweeper> _logger;
    private readonly TimeSpan _startupDelay;
    private readonly TimeSpan _interval;

    public AgentConversationRetentionSweeper(
        IServiceScopeFactory scopeFactory, ISystemClock clock, ILogger<AgentConversationRetentionSweeper> logger)
        : this(scopeFactory, clock, logger, DefaultStartupDelay, DefaultInterval)
    {
    }

    /// <summary>For tests, which must not wait ten minutes and a day.</summary>
    internal AgentConversationRetentionSweeper(
        IServiceScopeFactory scopeFactory, ISystemClock clock, ILogger<AgentConversationRetentionSweeper> logger,
        TimeSpan startupDelay, TimeSpan interval)
    {
        _scopeFactory = scopeFactory;
        _clock = clock;
        _logger = logger;
        _startupDelay = startupDelay;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(_startupDelay, stoppingToken); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try { await Task.Delay(_interval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// One pass. Never throws; null when it did not finish — the host stopped it part-way, or it failed. A
    /// pass that did not finish has still deleted whatever its earlier batches deleted: that is logged and
    /// audited all the same, so a deletion is never left unrecorded.
    /// </summary>
    internal async Task<AgentRetentionSweepResult?> RunOnceAsync(CancellationToken ct)
    {
        var tally = new AgentDeletionTally();
        var days = 0;
        IServiceScope? scope = null;
        try
        {
            scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            days = (await scope.ServiceProvider.GetRequiredService<AgentSettingsService>().GetAsync(ct)).ConversationRetentionDays;

            if (days <= 0)
            {
                _logger.LogInformation(
                    LogPrefix + " off (Keep conversations is blank or 0 in Settings → Assistant); every conversation is kept.");
                return new AgentRetentionSweepResult(0, 0, 0);
            }

            var now = _clock.UtcNow;
            var cutoff = now - TimeSpan.FromDays(days);
            // The production turn-mark lifetime: a mark at or before this was left by a process that died.
            var cut = AgentBusyCut.At(now, AgentConversationService.DefaultTurnDeadline + AgentConversationService.TurnMarkGrace);

            var ids = await db.AgentConversations.IgnoreQueryFilters().AsNoTracking()
                .Where(c => c.UpdatedAt < cutoff)
                .Select(c => c.Id)
                .ToListAsync(ct);

            // The cut is applied again inside each delete, so a conversation used since it was listed stays.
            if (ids.Count > 0)
                await AgentConversationDeletion.DeleteIdleAsync(db, ids, c => c.UpdatedAt < cutoff, cut, tally, ct);
            var kept = tally.Deleted == ids.Count ? 0
                : await db.AgentConversations.IgnoreQueryFilters().CountAsync(c => ids.Contains(c.Id) && c.UpdatedAt < cutoff, ct);

            _logger.LogInformation(
                LogPrefix + " deleted {Deleted} conversation(s) not used for {Days} day(s), across every workspace; " +
                "kept {Kept} that were still answering or running a plan.", tally.Deleted, days, kept);
            await AuditAsync(scope, tally.Deleted, days, kept, interrupted: false);
            return new AgentRetentionSweepResult(days, tally.Deleted, kept);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested)
                _logger.LogInformation(
                    LogPrefix + " stopped by the panel shutting down, after deleting {Deleted} conversation(s); the rest wait for the next pass.",
                    tally.Deleted);
            else
                _logger.LogError(ex,
                    LogPrefix + " the run failed before it could finish ({Reason}), after deleting {Deleted} conversation(s); " +
                    "it will be tried again tomorrow.", ex.Message, tally.Deleted);

            if (scope is not null) await AuditAsync(scope, tally.Deleted, days, kept: null, interrupted: true);
            return null;
        }
        finally
        {
            scope?.Dispose();
        }
    }

    /// <summary>
    /// One platform audit row for a pass that deleted anything — the audit log is what the settings page
    /// promises to keep, and "the panel deleted these" belongs in it as much as a person's delete does. On no
    /// token: the deletions it records have happened. Never throws (the logger swallows its own failures).
    /// </summary>
    private static async Task AuditAsync(IServiceScope scope, int deleted, int days, int? kept, bool interrupted)
    {
        if (deleted == 0) return;
        try
        {
            await scope.ServiceProvider.GetRequiredService<IAuditLogger>().LogAsync(
                AuditAction, targetType: "agent_conversation", actorEmailOverride: "system",
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new { deleted, kept, days, interrupted }),
                workspaceId: null, ct: CancellationToken.None);
        }
        catch (Exception)
        {
            // Nothing to add: the log line above already says what was deleted.
        }
    }
}

/// <summary>What one retention pass did. <paramref name="Days"/> is 0 when retention is off.</summary>
internal sealed record AgentRetentionSweepResult(int Days, int Deleted, int Kept);
