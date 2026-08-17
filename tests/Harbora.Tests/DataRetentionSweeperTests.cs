using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Auditing;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Identity;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Maintenance;
using Harbora.NodeAgent.Contracts;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

// Harbora.Domain.Common has a LogLevel of its own (deployment build output), and this file is about
// the other one.
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Harbora.Tests;

/// <summary>
/// The nightly sweeper that keeps the unbounded tables bounded (HARBORA-0012), extended by N1/M4
/// (2026-08-16 notification-system spec) to cover AlertIncident and NotificationDelivery.
///
/// <para>
/// Every clock here is fixed. A retention test that slept would be testing the scheduler, which is
/// the one part of this that has no decisions in it.
/// </para>
/// </summary>
public class DataRetentionSweeperTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 3, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Throws for exactly one entity type, the way a table with a lock or a broken index would.
    /// Everything else behaves normally, which is the point: the other eight tables must still be
    /// swept. Armed after seeding, so the fixture itself can still be written.
    /// </summary>
    private sealed class OneBadTableDbContext(DbContextOptions<HarboraDbContext> options, Type broken)
        : HarboraDbContext(options)
    {
        public bool Armed { get; set; }

        public override DbSet<TEntity> Set<TEntity>() =>
            Armed && typeof(TEntity) == broken
                ? throw new InvalidOperationException("relation is locked")
                : base.Set<TEntity>();
    }

    /// <summary>
    /// Asks to stop the moment the sweep reaches one table, and then fails the way a query does
    /// when its token is cancelled. Stands in for the panel being stopped mid-sweep.
    /// </summary>
    private sealed class StopsMidSweepDbContext(
        DbContextOptions<HarboraDbContext> options, Type stopAt, CancellationTokenSource stopping)
        : HarboraDbContext(options)
    {
        public bool Armed { get; set; }

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (!Armed || typeof(TEntity) != stopAt) return base.Set<TEntity>();

            stopping.Cancel();
            throw new OperationCanceledException(stopping.Token);
        }
    }

    private static DbContextOptions<HarboraDbContext> NewOptions() =>
        new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("retention-" + Guid.NewGuid()).Options;

    /// <summary>
    /// Captures what an operator would actually see, with the level, because the whole point of the
    /// lines below is that a table nobody swept must not pass for a table with nothing to sweep.
    /// </summary>
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static DataRetentionSweeper NewSweeper(
        HarboraDbContext db, RetentionOptions? options = null, DateTimeOffset? now = null,
        ILogger<DataRetentionSweeper>? logger = null)
    {
        // Registered as a singleton instance so the scope the sweeper opens does not dispose the
        // context the test is still holding. The sweeper resolves it exactly as it does in
        // production — through a scope from IServiceScopeFactory.
        var services = new ServiceCollection();
        services.AddSingleton(db);
        var provider = services.BuildServiceProvider();

        return new DataRetentionSweeper(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(options ?? new RetentionOptions()),
            new FixedClock(now ?? Now),
            logger ?? NullLogger<DataRetentionSweeper>.Instance);
    }

    /// <summary>
    /// One row per table on each side of its own cutoff. Seeded through a system-scoped context so
    /// the fixture itself is never the thing the workspace filter hides.
    /// </summary>
    private static async Task SeedBothSidesAsync(HarboraDbContext db, Guid workspaceId)
    {
        var oldDeployment = Guid.NewGuid();
        var newDeployment = Guid.NewGuid();

        db.DeploymentLogs.AddRange(
            new DeploymentLog { DeploymentId = oldDeployment, Message = "old", Timestamp = Now.AddDays(-91) },
            new DeploymentLog { DeploymentId = newDeployment, Message = "new", Timestamp = Now.AddDays(-89) });

        db.AuditLogs.AddRange(
            new AuditLog { Action = "user.login", CreatedAt = Now.AddDays(-366) },
            new AuditLog { Action = "user.login", CreatedAt = Now.AddDays(-364) });

        db.CronRuns.AddRange(
            new CronRun { WorkspaceId = workspaceId, AppId = Guid.NewGuid(), StartedAt = Now.AddDays(-92), FinishedAt = Now.AddDays(-91) },
            new CronRun { WorkspaceId = workspaceId, AppId = Guid.NewGuid(), StartedAt = Now.AddDays(-90), FinishedAt = Now.AddDays(-89) });

        db.FunctionInvocations.AddRange(
            new Harbora.Domain.Functions.FunctionInvocation
            {
                WorkspaceId = workspaceId, FunctionId = Guid.NewGuid(), AppId = Guid.NewGuid(),
                StartedAt = Now.AddDays(-32), CompletedAt = Now.AddDays(-32)
            },
            new Harbora.Domain.Functions.FunctionInvocation
            {
                WorkspaceId = workspaceId, FunctionId = Guid.NewGuid(), AppId = Guid.NewGuid(),
                StartedAt = Now.AddDays(-29), CompletedAt = Now.AddDays(-29)
            });

        db.NodeCommands.AddRange(
            new NodeCommandRecord
            {
                NodeId = "node-1", CommandId = "c-old", Command = NodeCommands.DeployWorkload,
                IdempotencyKey = "k-old", Status = NodeCommandStatus.Succeeded, IssuedAt = Now.AddDays(-91)
            },
            new NodeCommandRecord
            {
                NodeId = "node-1", CommandId = "c-new", Command = NodeCommands.DeployWorkload,
                IdempotencyKey = "k-new", Status = NodeCommandStatus.Succeeded, IssuedAt = Now.AddDays(-89)
            });

        db.NodeEvents.AddRange(
            new NodeEventRecord { NodeId = "node-1", Kind = "DiskPressure", At = Now.AddDays(-91) },
            new NodeEventRecord { NodeId = "node-1", Kind = "DiskPressure", At = Now.AddDays(-89) });

        db.IdempotencyRecords.AddRange(
            new IdempotencyRecord { WorkspaceId = workspaceId, Key = "old", Endpoint = "e", ExpiresAt = Now.AddMinutes(-1) },
            new IdempotencyRecord { WorkspaceId = workspaceId, Key = "new", Endpoint = "e", ExpiresAt = Now.AddMinutes(1) });

        db.PasswordResetTokens.AddRange(
            new PasswordResetToken { UserId = Guid.NewGuid(), TokenHash = "old", ExpiresAt = Now.AddDays(-8) },
            new PasswordResetToken { UserId = Guid.NewGuid(), TokenHash = "new", ExpiresAt = Now.AddMinutes(30) });

        db.UserSessions.AddRange(
            new UserSession { UserId = Guid.NewGuid(), LastSeenAt = Now.AddDays(-8), ExpiresAt = Now.AddMinutes(-1) },
            new UserSession { UserId = Guid.NewGuid(), LastSeenAt = Now, ExpiresAt = Now.AddMinutes(1) });
        db.EmailVerificationTokens.AddRange(
            new EmailVerificationToken { UserId = Guid.NewGuid(), TokenHash = "verify-old", ExpiresAt = Now.AddMinutes(-1) },
            new EmailVerificationToken { UserId = Guid.NewGuid(), TokenHash = "verify-new", ExpiresAt = Now.AddMinutes(1) });

        // N1/M4 (2026-08-16 notification-system spec).
        db.AlertIncidents.AddRange(
            new Harbora.Domain.Monitoring.AlertIncident
            {
                WorkspaceId = workspaceId, Condition = AlertEvent.DiskWarning, Severity = AlertSeverity.Warning,
                Title = "old", Body = "old", OpenedAt = Now.AddDays(-92), LastObservedAt = Now.AddDays(-92),
                ClosedAt = Now.AddDays(-91), ClosedReason = IncidentClosedReason.Resolved
            },
            new Harbora.Domain.Monitoring.AlertIncident
            {
                WorkspaceId = workspaceId, Condition = AlertEvent.DiskWarning, Severity = AlertSeverity.Warning,
                Title = "new", Body = "new", OpenedAt = Now.AddDays(-90), LastObservedAt = Now.AddDays(-90),
                ClosedAt = Now.AddDays(-89), ClosedReason = IncidentClosedReason.Resolved
            });

        db.NotificationDeliveries.AddRange(
            new Harbora.Domain.Notifications.NotificationDelivery
            {
                WorkspaceId = workspaceId, Purpose = NotificationDeliveryPurpose.AlertDispatch,
                Channel = AlertChannel.Webhook, Subject = "old", Status = NotificationDeliveryStatus.Sent,
                CreatedAt = Now.AddDays(-91)
            },
            new Harbora.Domain.Notifications.NotificationDelivery
            {
                WorkspaceId = workspaceId, Purpose = NotificationDeliveryPurpose.AlertDispatch,
                Channel = AlertChannel.Webhook, Subject = "new", Status = NotificationDeliveryStatus.Sent,
                CreatedAt = Now.AddDays(-89)
            });

        // N2 (2026-08-16 notification-system spec). AlertDedupMarkDays defaults to 7, so the cutoff
        // sits a week back from Now rather than the 90-day shape most of this fixture uses.
        db.AlertDedupMarks.AddRange(
            new Harbora.Domain.Monitoring.AlertDedupMark { Key = "dedup-old", FiredAt = Now.AddDays(-8) },
            new Harbora.Domain.Monitoring.AlertDedupMark { Key = "dedup-new", FiredAt = Now.AddDays(-6) });

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Every_table_loses_its_rows_past_the_cutoff_and_keeps_the_rest()
    {
        using var db = new HarboraDbContext(NewOptions());
        await SeedBothSidesAsync(db, Guid.NewGuid());

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);

        result.Failures.Should().BeEmpty();

        db.DeploymentLogs.Should().ContainSingle().Which.Message.Should().Be("new");
        db.AuditLogs.Should().ContainSingle().Which.CreatedAt.Should().Be(Now.AddDays(-364));
        db.CronRuns.IgnoreQueryFilters().Should().ContainSingle()
            .Which.FinishedAt.Should().Be(Now.AddDays(-89));
        db.NodeCommands.Should().ContainSingle().Which.CommandId.Should().Be("c-new");
        db.NodeEvents.Should().ContainSingle().Which.At.Should().Be(Now.AddDays(-89));
        db.IdempotencyRecords.IgnoreQueryFilters().Should().ContainSingle().Which.Key.Should().Be("new");
        db.PasswordResetTokens.Should().ContainSingle().Which.TokenHash.Should().Be("new");
        db.UserSessions.Should().ContainSingle().Which.ExpiresAt.Should().Be(Now.AddMinutes(1));
        db.EmailVerificationTokens.Should().ContainSingle().Which.TokenHash.Should().Be("verify-new");
        db.FunctionInvocations.IgnoreQueryFilters().Should().ContainSingle()
            .Which.CompletedAt.Should().Be(Now.AddDays(-29));
        db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle().Which.Title.Should().Be("new");
        db.NotificationDeliveries.Should().ContainSingle().Which.Subject.Should().Be("new");
        db.AlertDedupMarks.Should().ContainSingle().Which.Key.Should().Be("dedup-new");

        // Thirteen tables, one row each — and the sweep says so, rather than reporting a bare total
        // that could hide a table it never reached.
        result.Deleted.Should().HaveCount(13);
        result.Deleted.Values.Should().AllSatisfy(n => n.Should().Be(1));
        result.TotalDeleted.Should().Be(13);
    }

    [Fact]
    public async Task A_table_that_throws_does_not_stop_the_others()
    {
        // The failure this is really about: one locked or corrupt table silently ending the sweep,
        // so every other table grows forever and the logs mention only the one that broke.
        using var db = new OneBadTableDbContext(NewOptions(), typeof(NodeEventRecord));
        await SeedBothSidesAsync(db, Guid.NewGuid());
        db.Armed = true;

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);
        db.Armed = false;

        result.Failures.Should().ContainKey(RetentionTables.NodeEvents);
        result.Failures[RetentionTables.NodeEvents].Should().Contain("relation is locked");

        // The other twelve still ran.
        result.Deleted.Should().HaveCount(12);
        result.Deleted.Should().NotContainKey(RetentionTables.NodeEvents);
        db.DeploymentLogs.Should().ContainSingle();
        db.AuditLogs.Should().ContainSingle();
        db.CronRuns.IgnoreQueryFilters().Should().ContainSingle();
        db.NodeCommands.Should().ContainSingle();
        db.IdempotencyRecords.IgnoreQueryFilters().Should().ContainSingle();
        db.PasswordResetTokens.Should().ContainSingle();
    }

    [Fact]
    public async Task One_unusable_number_of_days_does_not_abandon_the_whole_sweep()
    {
        // The cutoff is worked out before the per-table guard is entered, and deployment logs are
        // swept first — so a value too large to be a date on that one key used to throw past every
        // guard in the class and end the pass before any table was reached, every night, saying
        // only "the data retention sweep failed".
        using var db = new HarboraDbContext(NewOptions());
        await SeedBothSidesAsync(db, Guid.NewGuid());

        var result = await NewSweeper(db, new RetentionOptions { DeploymentLogDays = int.MaxValue })
            .SweepAsync(CancellationToken.None);

        result.Failures.Should().BeEmpty();
        result.KeptForever.Should().Contain(RetentionTables.DeploymentLogs);
        db.DeploymentLogs.Should().HaveCount(2, "a span too long to be a date means keep, not delete");
        result.Deleted.Should().HaveCount(12, "every other table was still swept");
    }

    [Fact]
    public async Task A_cutoff_too_large_to_be_a_date_is_said_out_loud_with_the_key_and_the_value()
    {
        // Reading an unusable value as "keep for ever" stops it ending the sweep, but on its own it
        // makes the largest table stop being swept in silence — and the nightly line about the other
        // six then reads like a healthy pass. RetentionSweepResult keeps "kept" apart from "swept"
        // precisely so that cannot happen, and nothing in src/ ever read that field: one producer,
        // no consumers. So it has to be said in the log, where somebody will see it, and it has to
        // name the setting an operator would go and edit.
        using var db = new HarboraDbContext(NewOptions());
        await SeedBothSidesAsync(db, Guid.NewGuid());
        var logger = new RecordingLogger<DataRetentionSweeper>();

        await NewSweeper(db, new RetentionOptions { DeploymentLogDays = 900_000_000 }, logger: logger)
            .SweepAsync(CancellationToken.None);

        var warnings = logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();

        warnings.Should().ContainSingle().Which.Message.Should()
            .Contain("Retention:DeploymentLogDays", "an operator needs the key, not just the table")
            .And.Contain("900000000", "and the value they typed, so they can recognise it")
            .And.Contain(RetentionTables.DeploymentLogs);
    }

    [Fact]
    public async Task A_table_turned_off_on_purpose_is_reported_once_and_not_as_a_problem()
    {
        // The other reading of "keep for ever" is a deliberate one, and it must still appear — a
        // table nobody swept should never be indistinguishable from a table with nothing to sweep —
        // but it is a decision somebody made, not something to wake anyone up about.
        using var db = new HarboraDbContext(NewOptions());
        await SeedBothSidesAsync(db, Guid.NewGuid());
        var logger = new RecordingLogger<DataRetentionSweeper>();

        await NewSweeper(db, new RetentionOptions { AuditLogDays = 0 }, logger: logger)
            .SweepAsync(CancellationToken.None);

        logger.Entries.Should().NotContain(e => e.Level >= LogLevel.Warning);
        logger.Entries.Should()
            .ContainSingle(e => e.Level == LogLevel.Information && e.Message.Contains(RetentionTables.AuditLogs));
    }

    [Fact]
    public async Task Being_asked_to_stop_mid_sweep_is_not_a_table_failure()
    {
        // Shutdown must not be recorded as nine broken tables, and must not be swallowed either:
        // the tables after the stop are simply not swept, and the caller learns the pass ended.
        using var stopping = new CancellationTokenSource();
        using var db = new StopsMidSweepDbContext(NewOptions(), typeof(AuditLog), stopping);
        await SeedBothSidesAsync(db, Guid.NewGuid());
        db.Armed = true;

        var sweep = async () => await NewSweeper(db).SweepAsync(stopping.Token);

        await sweep.Should().ThrowAsync<OperationCanceledException>();
        db.Armed = false;

        // Without the guard this would be filed as a failure of AuditLogs and the sweep would carry
        // on through the five tables after it.
        db.NodeEvents.Should().HaveCount(2);
        db.PasswordResetTokens.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_table_set_to_keep_forever_is_left_alone_and_says_so()
    {
        using var db = new HarboraDbContext(NewOptions());
        await SeedBothSidesAsync(db, Guid.NewGuid());

        var result = await NewSweeper(db, new RetentionOptions { AuditLogDays = 0 })
            .SweepAsync(CancellationToken.None);

        db.AuditLogs.Should().HaveCount(2);
        result.Deleted.Should().NotContainKey(RetentionTables.AuditLogs);
        result.KeptForever.Should().Contain(RetentionTables.AuditLogs);
    }

    [Fact]
    public async Task The_sweep_reaches_every_tenant_even_from_a_workspace_scoped_context()
    {
        // The trap this codebase has paid for repeatedly: a filtered read from a sessionless path
        // finds nothing, deletes nothing, and reports a clean pass. CronRun and IdempotencyRecord
        // are the two swept tables that carry a workspace filter.
        var tenant = Guid.NewGuid();
        var someoneElse = new FixedWorkspaceScope(Guid.NewGuid());

        using var db = new HarboraDbContext(NewOptions(), someoneElse);
        await SeedBothSidesAsync(db, tenant);

        // The protection set is read from Deployments and Apps, and both of those carry a workspace
        // filter too. This is the read where a leak is worst: an empty candidate set merely fails to
        // delete, while an empty protection set deletes the rows that must never go — the build
        // still being written, and the account of what an app is running right now.
        var building = Guid.NewGuid();
        var live = Guid.NewGuid();
        db.Deployments.AddRange(
            new Deployment
            {
                Id = building, AppId = Guid.NewGuid(), WorkspaceId = tenant,
                Number = 1, Status = DeploymentStatus.Building
            },
            new Deployment
            {
                Id = live, AppId = Guid.NewGuid(), WorkspaceId = tenant,
                Number = 2, Status = DeploymentStatus.Succeeded
            });
        db.Apps.Add(new App { Name = "shop", Slug = "shop", WorkspaceId = tenant, ActiveDeploymentId = live });
        db.DeploymentLogs.AddRange(
            new DeploymentLog { DeploymentId = building, Message = "still building", Timestamp = Now.AddDays(-400) },
            new DeploymentLog { DeploymentId = live, Message = "what is running now", Timestamp = Now.AddDays(-400) });
        await db.SaveChangesAsync();

        var result = await NewSweeper(db).SweepAsync(CancellationToken.None);

        result.Deleted[RetentionTables.CronRuns].Should().Be(1);
        result.Deleted[RetentionTables.IdempotencyRecords].Should().Be(1);
        db.CronRuns.IgnoreQueryFilters().Should().ContainSingle();
        db.IdempotencyRecords.IgnoreQueryFilters().Should().ContainSingle();

        db.DeploymentLogs.Select(log => log.Message).Should()
            .BeEquivalentTo(["new", "still building", "what is running now"]);
    }

    [Fact]
    public async Task The_logs_of_a_running_build_survive_however_old_the_cutoff_makes_them()
    {
        using var db = new HarboraDbContext(NewOptions());

        var building = new Deployment
        {
            Id = Guid.NewGuid(), AppId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(),
            Number = 1, Status = DeploymentStatus.Building
        };
        db.Deployments.Add(building);
        db.DeploymentLogs.Add(new DeploymentLog
        {
            DeploymentId = building.Id, Message = "still building", Timestamp = Now.AddDays(-400)
        });
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        db.DeploymentLogs.Should().ContainSingle().Which.Message.Should().Be("still building");
    }

    [Fact]
    public async Task The_logs_of_the_release_an_app_is_running_survive()
    {
        using var db = new HarboraDbContext(NewOptions());

        var deploymentId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        db.Deployments.Add(new Deployment
        {
            Id = deploymentId, AppId = Guid.NewGuid(), WorkspaceId = workspaceId,
            Number = 1, Status = DeploymentStatus.Succeeded
        });
        db.Apps.Add(new App
        {
            Name = "shop", Slug = "shop", WorkspaceId = workspaceId, ActiveDeploymentId = deploymentId
        });
        db.DeploymentLogs.Add(new DeploymentLog
        {
            DeploymentId = deploymentId, Message = "what is running now", Timestamp = Now.AddDays(-400)
        });
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        db.DeploymentLogs.Should().ContainSingle().Which.Message.Should().Be("what is running now");
    }

    [Fact]
    public async Task An_open_incident_survives_however_old_it_is()
    {
        // RetentionRule.AlertIncidentsToDelete only ever considers ClosedAt != null. An open incident
        // is the current state of a condition — read by the bell badge and the timeline — and must
        // never be swept out from under them just because it has stood open a long time.
        using var db = new HarboraDbContext(NewOptions());
        db.AlertIncidents.Add(new Harbora.Domain.Monitoring.AlertIncident
        {
            WorkspaceId = Guid.NewGuid(), Condition = AlertEvent.DeployFailed, Severity = AlertSeverity.Critical,
            Title = "still open", Body = "still open", OpenedAt = Now.AddDays(-400), LastObservedAt = Now.AddDays(-400)
        });
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle().Which.Title.Should().Be("still open");
    }

    [Fact]
    public async Task A_pending_delivery_survives_however_old_it_is()
    {
        // RetentionRule.NotificationDeliveriesToDelete only ever considers a terminal status. A
        // Pending row is either not yet attempted or waiting out a retry backoff, and a queued Job
        // still holds its id — sweeping it would leave that job with nothing to claim.
        using var db = new HarboraDbContext(NewOptions());
        db.NotificationDeliveries.Add(new Harbora.Domain.Notifications.NotificationDelivery
        {
            WorkspaceId = Guid.NewGuid(), Purpose = NotificationDeliveryPurpose.AlertDispatch,
            Channel = AlertChannel.Webhook, Subject = "still pending",
            Status = NotificationDeliveryStatus.Pending, CreatedAt = Now.AddDays(-400)
        });
        await db.SaveChangesAsync();

        await NewSweeper(db).SweepAsync(CancellationToken.None);

        db.NotificationDeliveries.Should().ContainSingle().Which.Subject.Should().Be("still pending");
    }
}
