using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Projects;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Scheduled VACUUM/VACUUM FULL/ANALYZE/REINDEX/OPTIMIZE TABLE against a logical database (2.3,
/// round-2 market-gaps plan), proved against the fake Docker engine rather than by mocking
/// <see cref="DatabaseMaintenanceService"/>'s own calls — the same reasoning
/// <see cref="LogicalDatabaseServiceTests"/> gives: the thing worth proving is what actually reaches
/// the engine and what does not.
/// </summary>
public class DatabaseMaintenanceServiceTests
{
    private sealed class Clock(DateTimeOffset now) : Harbora.Application.Abstractions.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = now;
    }

    private static readonly DateTimeOffset Start = new(2026, 9, 4, 9, 0, 0, TimeSpan.Zero);

    private sealed record Stack(
        HarboraDbContext Db, DatabaseMaintenanceService Service, FakeDockerEngine Docker,
        ManagedService Instance, ManagedServiceDatabase Database, NoopJobQueue Jobs, Clock Clock);

    /// <summary>A single-server install — the panel talks to the same Docker daemon the database runs
    /// on, mirroring <c>LogicalDatabaseServiceTests.BuildLocal</c>.</summary>
    private static Stack BuildLocal(
        ManagedServiceType type = ManagedServiceType.PostgreSql, bool secondDatabase = true)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbmaint-" + Guid.NewGuid()).Options);

        var workspace = new Workspace { Id = Guid.CreateVersion7(), Name = "Acme", Slug = "acme" };
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.Add(workspace);
        db.Add(project);
        db.Add(environment);

        var protector = new PassthroughProtector();
        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(),
            WorkspaceId = workspace.Id,
            EnvironmentId = environment.Id,
            ServerId = Guid.Empty,
            Name = "Shop DB",
            ContainerName = "harbora-svc-shop",
            DatabaseName = "shop",
            Username = "harbora",
            EncryptedPassword = protector.Protect("admin_secret"),
            InternalPort = 5432,
            Status = ServiceStatus.Running,
            Type = type
        };
        db.Add(instance);

        var defaultDatabase = new ManagedServiceDatabase
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ManagedServiceId = instance.Id,
            Name = "shop", Username = "harbora", EncryptedPassword = instance.EncryptedPassword, IsDefault = true
        };
        db.Add(defaultDatabase);

        var orders = defaultDatabase;
        if (secondDatabase)
        {
            orders = new ManagedServiceDatabase
            {
                Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ManagedServiceId = instance.Id,
                Name = "orders", Username = "harbora_orders", EncryptedPassword = instance.EncryptedPassword
            };
            db.Add(orders);
        }
        db.SaveChanges();

        var docker = new FakeDockerEngine();
        var engines = new FakeServerEngineFactory(docker);
        var clock = new Clock(Start);
        var jobs = new NoopJobQueue();

        var grants = new DatabaseGrantExecutor(engines, protector, NullLogger<DatabaseGrantExecutor>.Instance);
        var engine = new ManagedServiceEngine(
            db, engines, protector, jobs,
            new Harbora.Infrastructure.Billing.BillingGate(
                db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions())),
            Options.Create(new HarboraRuntimeOptions()), clock, NullLogger<ManagedServiceEngine>.Instance);

        var service = new DatabaseMaintenanceService(
            db, clock, jobs, NullLogger<DatabaseMaintenanceService>.Instance, grants, engine);

        return new Stack(db, service, docker, instance, orders, jobs, clock);
    }

    /// <summary>An installation with no local reach at all.</summary>
    private static (HarboraDbContext Db, DatabaseMaintenanceService Service, ManagedService Instance, ManagedServiceDatabase Database)
        BuildUnreachable()
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("dbmaint-unreachable-" + Guid.NewGuid()).Options);

        var workspaceId = Guid.CreateVersion7();
        var instance = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ServerId = Guid.CreateVersion7(),
            Name = "Shop DB", ContainerName = "harbora-svc-shop", DatabaseName = "shop",
            Username = "harbora", InternalPort = 5432, Type = ManagedServiceType.PostgreSql
        };
        db.Add(instance);
        var database = new ManagedServiceDatabase
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspaceId, ManagedServiceId = instance.Id,
            Name = "shop", Username = "harbora", IsDefault = true
        };
        db.Add(database);
        db.SaveChanges();

        var service = new DatabaseMaintenanceService(
            db, new Clock(Start), new NoopJobQueue(), NullLogger<DatabaseMaintenanceService>.Instance);

        return (db, service, instance, database);
    }

    // -------------------------------------------------------------------------------------------
    // The right statement reaches the right logical database.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queueing_and_running_reaches_the_engine_with_the_right_statement_on_the_right_database()
    {
        var s = BuildLocal();

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.VacuumFull, DatabaseMaintenanceTrigger.Manual, null, default);
        error.Should().BeNull();
        runId.Should().NotBeNull();

        await s.Service.RunAsync(runId!.Value, default);

        s.Docker.OneOffCommands.Should().ContainSingle(c => c.Contains("VACUUM FULL;") && c.Contains("orders"));

        var run = await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(DatabaseMaintenanceRunStatus.Succeeded);
        run.StartedAt.Should().NotBeNull();
        run.FinishedAt.Should().NotBeNull();
        run.Error.Should().BeNull();
    }

    [Fact]
    public async Task The_job_queue_is_asked_to_exclude_on_the_databases_own_id()
    {
        var s = BuildLocal();

        var (runId, _) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        s.Jobs.Enqueued.Should().ContainSingle(e =>
            e.Kind == JobKind.DatabaseMaintenance && e.TargetId == runId && e.ExcludesOn == s.Database.Id);
    }

    // -------------------------------------------------------------------------------------------
    // A failure names the database and quotes the engine's own words.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_statement_names_the_database_and_repeats_the_engines_own_error()
    {
        var s = BuildLocal();
        s.Docker.OneOffExitCode = 1;
        s.Docker.OneOffOutput.Add("ERROR:  could not extend file base/16401/16544: No space left on device");

        var (runId, _) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.VacuumFull, DatabaseMaintenanceTrigger.Manual, null, default);
        await s.Service.RunAsync(runId!.Value, default);

        var run = await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(DatabaseMaintenanceRunStatus.Failed);
        run.Error.Should().Contain("VACUUM FULL", "the statement that failed must be named");
        run.Error.Should().Contain("orders", "which database it failed on must be named");
        run.Error.Should().Contain("No space left on device", "the engine's own words must be repeated, not summarised away");
    }

    // -------------------------------------------------------------------------------------------
    // An unsupported engine is refused by name and nothing is sent.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_engine_with_no_maintenance_story_is_refused_by_name_and_nothing_is_sent()
    {
        var s = BuildLocal(ManagedServiceType.Redis, secondDatabase: false);

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        runId.Should().BeNull();
        error.Should().Contain("Redis");
        s.Docker.Calls.Should().BeEmpty("nothing was ever attempted against an engine with no maintenance story");
        (await s.Db.DatabaseMaintenanceRuns.CountAsync()).Should().Be(0, "no row for a run that never happened");
    }

    [Fact]
    public async Task An_operation_this_engine_does_not_offer_is_refused_by_name_and_nothing_is_sent()
    {
        var s = BuildLocal(ManagedServiceType.MySql, secondDatabase: false);

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.VacuumFull, DatabaseMaintenanceTrigger.Manual, null, default);

        runId.Should().BeNull();
        error.Should().Contain("VACUUM FULL");
        s.Docker.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreachable_installation_is_refused_by_name_and_nothing_is_sent()
    {
        var (db, service, _, database) = BuildUnreachable();

        var (runId, error) = await service.QueueAsync(
            database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        runId.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
        (await db.DatabaseMaintenanceRuns.CountAsync()).Should().Be(0);
    }

    // -------------------------------------------------------------------------------------------
    // A run colliding with a backup is prevented.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Queueing_is_refused_while_a_backup_of_the_same_database_is_running()
    {
        var s = BuildLocal();
        s.Db.Backups.Add(new Backup
        {
            WorkspaceId = s.Instance.WorkspaceId, DestinationId = Guid.CreateVersion7(),
            Type = BackupType.Database, TargetRef = s.Instance.Id.ToString(),
            ManagedServiceDatabaseId = s.Database.Id, Status = BackupStatus.Running
        });
        s.Db.SaveChanges();

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        runId.Should().BeNull();
        error.Should().Be(DatabaseMaintenanceService.BackupRunning);
        s.Docker.Calls.Should().BeEmpty("nothing should be sent while a backup of this database is running");
    }

    [Fact]
    public async Task Queueing_is_refused_while_a_physical_base_backup_of_the_whole_instance_is_running()
    {
        var s = BuildLocal();
        s.Db.Backups.Add(new Backup
        {
            WorkspaceId = s.Instance.WorkspaceId, DestinationId = Guid.CreateVersion7(),
            Type = BackupType.PostgresBaseBackup, TargetRef = s.Instance.Id.ToString(),
            Status = BackupStatus.Pending
        });
        s.Db.SaveChanges();

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        runId.Should().BeNull();
        error.Should().Be(DatabaseMaintenanceService.BackupRunning);
    }

    [Fact]
    public async Task A_backup_of_a_DIFFERENT_logical_database_on_the_same_instance_does_not_block()
    {
        var s = BuildLocal();
        var otherDatabase = new ManagedServiceDatabase
        {
            Id = Guid.CreateVersion7(), WorkspaceId = s.Instance.WorkspaceId, ManagedServiceId = s.Instance.Id,
            Name = "billing", Username = "harbora_billing", EncryptedPassword = s.Instance.EncryptedPassword
        };
        s.Db.Add(otherDatabase);
        s.Db.Backups.Add(new Backup
        {
            WorkspaceId = s.Instance.WorkspaceId, DestinationId = Guid.CreateVersion7(),
            Type = BackupType.Database, TargetRef = s.Instance.Id.ToString(),
            ManagedServiceDatabaseId = otherDatabase.Id, Status = BackupStatus.Running
        });
        s.Db.SaveChanges();

        var (runId, error) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);

        error.Should().BeNull("a dump of a different logical database takes nothing this statement needs");
        runId.Should().NotBeNull();
    }

    [Fact]
    public async Task A_backup_that_starts_between_queueing_and_the_worker_claiming_the_job_is_caught_at_run_time()
    {
        var s = BuildLocal();

        var (runId, _) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);
        runId.Should().NotBeNull();

        // The backup started AFTER this run was queued — the race the run-time re-check exists for.
        s.Db.Backups.Add(new Backup
        {
            WorkspaceId = s.Instance.WorkspaceId, DestinationId = Guid.CreateVersion7(),
            Type = BackupType.Database, TargetRef = s.Instance.Id.ToString(),
            ManagedServiceDatabaseId = s.Database.Id, Status = BackupStatus.Running
        });
        s.Db.SaveChanges();

        await s.Service.RunAsync(runId!.Value, default);

        s.Docker.Calls.Should().BeEmpty("the run-time re-check must stop this before anything is sent");
        var run = await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == runId);
        run.Status.Should().Be(DatabaseMaintenanceRunStatus.Failed);
        run.Error.Should().Be(DatabaseMaintenanceService.BackupRunning);
    }

    // -------------------------------------------------------------------------------------------
    // Tenancy, both directions.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_right_tenants_run_reaches_the_engine()
    {
        var s = BuildLocal();

        var run = new DatabaseMaintenanceRun
        {
            WorkspaceId = s.Instance.WorkspaceId, // the database's OWN workspace
            ManagedServiceDatabaseId = s.Database.Id,
            Operation = DatabaseMaintenanceOperation.Analyze,
            Status = DatabaseMaintenanceRunStatus.Pending
        };
        s.Db.DatabaseMaintenanceRuns.Add(run);
        s.Db.SaveChanges();

        await s.Service.RunAsync(run.Id, default);

        s.Docker.OneOffCommands.Should().ContainSingle(c => c.Contains("ANALYZE;"));
        (await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == run.Id)).Status
            .Should().Be(DatabaseMaintenanceRunStatus.Succeeded);
    }

    /// <summary>
    /// A run row whose stamped <see cref="DatabaseMaintenanceRun.WorkspaceId"/> does NOT match the
    /// logical database's own workspace — the background path's explicit <c>WorkspaceId ==</c>
    /// comparison is what stops this from ever reaching the engine, the way the wrong tenant must
    /// never see (or touch) another tenant's database.
    /// </summary>
    [Fact]
    public async Task A_run_stamped_with_the_wrong_workspace_touches_nothing()
    {
        var s = BuildLocal();

        var run = new DatabaseMaintenanceRun
        {
            WorkspaceId = Guid.CreateVersion7(), // NOT s.Instance.WorkspaceId
            ManagedServiceDatabaseId = s.Database.Id,
            Operation = DatabaseMaintenanceOperation.Analyze,
            Status = DatabaseMaintenanceRunStatus.Pending
        };
        s.Db.DatabaseMaintenanceRuns.Add(run);
        s.Db.SaveChanges();

        await s.Service.RunAsync(run.Id, default);

        s.Docker.Calls.Should().BeEmpty("a run whose workspace does not match the database's own must touch nothing");
        var settled = await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == run.Id);
        settled.Status.Should().Be(DatabaseMaintenanceRunStatus.Failed);
        settled.Error.Should().Contain("no longer exists");
    }

    // -------------------------------------------------------------------------------------------
    // One at a time, and reconciliation.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_second_run_on_the_same_database_is_refused_while_one_is_already_pending()
    {
        var s = BuildLocal();

        var (first, error1) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, DatabaseMaintenanceTrigger.Manual, null, default);
        first.Should().NotBeNull();
        error1.Should().BeNull();

        var (second, error2) = await s.Service.QueueAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Analyze, DatabaseMaintenanceTrigger.Manual, null, default);

        second.Should().BeNull();
        error2.Should().Be(DatabaseMaintenanceService.AlreadyRunning);
    }

    [Fact]
    public async Task A_run_stranded_running_by_a_restart_is_settled_rather_than_left_open_forever()
    {
        var s = BuildLocal();
        var run = new DatabaseMaintenanceRun
        {
            WorkspaceId = s.Instance.WorkspaceId, ManagedServiceDatabaseId = s.Database.Id,
            Operation = DatabaseMaintenanceOperation.Vacuum, Status = DatabaseMaintenanceRunStatus.Running,
            StartedAt = Start
        };
        s.Db.DatabaseMaintenanceRuns.Add(run);
        s.Db.SaveChanges();

        await s.Service.ReconcileAsync(default);

        var settled = await s.Db.DatabaseMaintenanceRuns.SingleAsync(r => r.Id == run.Id);
        settled.Status.Should().Be(DatabaseMaintenanceRunStatus.Failed);
        settled.Error.Should().Contain("restart");
        settled.FinishedAt.Should().NotBeNull();
    }

    // -------------------------------------------------------------------------------------------
    // Schedules.
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Setting_a_schedule_computes_its_next_run_from_the_cron_expression()
    {
        var s = BuildLocal();

        var error = await s.Service.SetScheduleAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, enabled: true, "0 3 * * 0", "UTC", default);

        error.Should().BeNull();
        var row = await s.Db.DatabaseMaintenanceSchedules.SingleAsync();
        row.NextRunAt.Should().NotBeNull();
        row.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task An_unparseable_cron_expression_is_refused_rather_than_silently_never_firing()
    {
        var s = BuildLocal();

        var error = await s.Service.SetScheduleAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, enabled: true, "not a cron expression", "UTC", default);

        error.Should().NotBeNullOrWhiteSpace();
        (await s.Db.DatabaseMaintenanceSchedules.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Setting_a_schedule_twice_for_the_same_operation_updates_the_one_row()
    {
        var s = BuildLocal();

        await s.Service.SetScheduleAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, enabled: true, "0 3 * * 0", "UTC", default);
        await s.Service.SetScheduleAsync(
            s.Database.Id, DatabaseMaintenanceOperation.Vacuum, enabled: false, "0 4 * * 1", "UTC", default);

        (await s.Db.DatabaseMaintenanceSchedules.CountAsync()).Should().Be(1);
        var row = await s.Db.DatabaseMaintenanceSchedules.SingleAsync();
        row.Enabled.Should().BeFalse();
        row.Schedule.Should().Be("0 4 * * 1");
    }

    [Fact]
    public async Task Deleting_an_already_missing_schedule_is_a_success_not_an_error()
    {
        var s = BuildLocal();

        // Nothing thrown is the assertion — the same idempotent-delete idiom LogicalDatabaseService uses.
        await s.Service.DeleteScheduleAsync(Guid.CreateVersion7(), default);
    }
}
