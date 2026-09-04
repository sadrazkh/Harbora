using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Ticks <see cref="DatabaseMaintenanceSchedule"/>s (2.3, round-2 market-gaps plan) — the same
/// cron-tick shape <c>CronRunnerTests</c> already drives for App cron jobs, reused here for the
/// scheduler that fires maintenance rather than mocking its own calls.
/// </summary>
public class DatabaseMaintenanceSchedulerTests : IDisposable
{
    private readonly ServiceProvider _sp;
    private readonly FakeDockerEngine _docker = new();
    private readonly FixedClock _clock = new(new DateTimeOffset(2026, 9, 4, 2, 59, 0, TimeSpan.Zero));
    private ManagedService _instance = null!;
    private ManagedServiceDatabase _database = null!;

    public DatabaseMaintenanceSchedulerTests()
    {
        var database = "dbmaint-scheduler-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(database));
        services.AddSingleton<ISystemClock>(_clock);
        services.AddSingleton<ISecretProtector>(new PassthroughProtector());
        services.AddSingleton<IServerEngineFactory>(new SingleEngine(_docker));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new HarboraRuntimeOptions()));
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
            new Harbora.Infrastructure.Billing.BillingOptions()));
        services.AddScoped<Harbora.Application.Abstractions.IBillingGate,
            Harbora.Infrastructure.Billing.BillingGate>();
        services.AddSingleton<IJobQueue>(new NoopJobQueue());
        services.AddScoped<DatabaseGrantExecutor>();
        services.AddScoped<ManagedServiceEngine>();
        services.AddScoped<DatabaseMaintenanceService>();
        services.AddLogging();
        _sp = services.BuildServiceProvider();

        Seed();
    }

    public void Dispose() => _sp.Dispose();

    private sealed class SingleEngine(FakeDockerEngine engine) : IServerEngineFactory
    {
        public IDockerEngine Local => engine;
        public Task<IDockerEngine> ResolveAsync(Guid serverId, CancellationToken ct) =>
            Task.FromResult<IDockerEngine>(engine);
    }

    private void Seed()
    {
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<ISecretProtector>();

        var workspace = new Harbora.Domain.Identity.Workspace
        { Id = Guid.CreateVersion7(), Name = "Acme", Slug = "acme" };
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        };
        db.AddRange(workspace, project, environment);

        _instance = new ManagedService
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, EnvironmentId = environment.Id,
            ServerId = Guid.Empty, Name = "Shop DB", ContainerName = "harbora-svc-shop",
            DatabaseName = "shop", Username = "harbora", EncryptedPassword = protector.Protect("admin_secret"),
            InternalPort = 5432, Status = ServiceStatus.Running, Type = ManagedServiceType.PostgreSql
        };
        db.Add(_instance);

        _database = new ManagedServiceDatabase
        {
            Id = Guid.CreateVersion7(), WorkspaceId = workspace.Id, ManagedServiceId = _instance.Id,
            Name = "orders", Username = "harbora_orders", EncryptedPassword = _instance.EncryptedPassword
        };
        db.Add(_database);
        db.SaveChanges();
    }

    private DatabaseMaintenanceScheduler Scheduler() =>
        new(_sp.GetRequiredService<IServiceScopeFactory>(), NullLogger<DatabaseMaintenanceScheduler>.Instance);

    [Fact]
    public async Task A_due_schedule_queues_a_run_and_advances_its_own_next_occurrence()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.DatabaseMaintenanceSchedules.Add(new DatabaseMaintenanceSchedule
            {
                WorkspaceId = _instance.WorkspaceId, ManagedServiceDatabaseId = _database.Id,
                Operation = DatabaseMaintenanceOperation.Vacuum, Enabled = true,
                Schedule = "0 3 * * *", Timezone = "UTC",
                NextRunAt = new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero)
            });
            db.SaveChanges();
        }

        _clock.UtcNow = new DateTimeOffset(2026, 9, 4, 3, 0, 30, TimeSpan.Zero);
        await Scheduler().TickAsync(default);

        using var check = _sp.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await checkDb.DatabaseMaintenanceRuns.CountAsync()).Should().Be(1);

        var schedule = await checkDb.DatabaseMaintenanceSchedules.SingleAsync();
        schedule.LastRunAt.Should().Be(_clock.UtcNow);
        schedule.NextRunAt.Should().NotBeNull();
        schedule.NextRunAt!.Value.Should().BeAfter(_clock.UtcNow);
    }

    [Fact]
    public async Task A_schedule_not_yet_due_is_left_alone()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.DatabaseMaintenanceSchedules.Add(new DatabaseMaintenanceSchedule
            {
                WorkspaceId = _instance.WorkspaceId, ManagedServiceDatabaseId = _database.Id,
                Operation = DatabaseMaintenanceOperation.Vacuum, Enabled = true,
                Schedule = "0 3 * * *", Timezone = "UTC",
                NextRunAt = new DateTimeOffset(2026, 9, 5, 3, 0, 0, TimeSpan.Zero)
            });
            db.SaveChanges();
        }

        await Scheduler().TickAsync(default);

        using var check = _sp.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await checkDb.DatabaseMaintenanceRuns.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_disabled_schedule_never_fires()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.DatabaseMaintenanceSchedules.Add(new DatabaseMaintenanceSchedule
            {
                WorkspaceId = _instance.WorkspaceId, ManagedServiceDatabaseId = _database.Id,
                Operation = DatabaseMaintenanceOperation.Vacuum, Enabled = false,
                Schedule = "0 3 * * *", Timezone = "UTC",
                NextRunAt = new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero)
            });
            db.SaveChanges();
        }

        _clock.UtcNow = new DateTimeOffset(2026, 9, 4, 3, 0, 30, TimeSpan.Zero);
        await Scheduler().TickAsync(default);

        using var check = _sp.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await checkDb.DatabaseMaintenanceRuns.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// The collision rule's scheduler-side half: a schedule due while a backup of the same database is
    /// running skips this tick rather than erroring or being disabled — the same "an ordinary reason
    /// to skip a tick" <c>BackupPolicyScheduler</c>'s own remarks give the mirror-image case. The next
    /// cron occurrence, computed the same as any other tick, is what tries again.
    /// </summary>
    [Fact]
    public async Task A_schedule_colliding_with_a_running_backup_skips_this_tick_but_still_advances()
    {
        using (var scope = _sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.DatabaseMaintenanceSchedules.Add(new DatabaseMaintenanceSchedule
            {
                WorkspaceId = _instance.WorkspaceId, ManagedServiceDatabaseId = _database.Id,
                Operation = DatabaseMaintenanceOperation.Vacuum, Enabled = true,
                Schedule = "0 3 * * *", Timezone = "UTC",
                NextRunAt = new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero)
            });
            db.Backups.Add(new Backup
            {
                WorkspaceId = _instance.WorkspaceId, DestinationId = Guid.CreateVersion7(),
                Type = BackupType.Database, TargetRef = _instance.Id.ToString(),
                ManagedServiceDatabaseId = _database.Id, Status = BackupStatus.Running
            });
            db.SaveChanges();
        }

        _clock.UtcNow = new DateTimeOffset(2026, 9, 4, 3, 0, 30, TimeSpan.Zero);
        await Scheduler().TickAsync(default);

        using var check = _sp.CreateScope();
        var checkDb = check.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await checkDb.DatabaseMaintenanceRuns.CountAsync()).Should().Be(0, "the backup collision refused the queue");
        _docker.Calls.Should().BeEmpty();

        var schedule = await checkDb.DatabaseMaintenanceSchedules.SingleAsync();
        schedule.NextRunAt.Should().NotBeNull("the schedule still advances so it is retried at its next occurrence, not spun on every tick");
        schedule.NextRunAt!.Value.Should().BeAfter(_clock.UtcNow);
    }

    [Fact]
    public void NextRun_reads_the_cron_expression_in_the_schedules_own_timezone()
    {
        var schedule = new DatabaseMaintenanceSchedule
        {
            Enabled = true, Schedule = "0 3 * * *", Timezone = "UTC"
        };

        var next = DatabaseMaintenanceScheduler.NextRun(
            schedule, new DateTimeOffset(2026, 9, 4, 1, 0, 0, TimeSpan.Zero));

        next.Should().Be(new DateTimeOffset(2026, 9, 4, 3, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void A_disabled_schedule_has_no_next_run()
    {
        var schedule = new DatabaseMaintenanceSchedule { Enabled = false, Schedule = "0 3 * * *" };
        DatabaseMaintenanceScheduler.NextRun(schedule, DateTimeOffset.UtcNow).Should().BeNull();
    }
}
