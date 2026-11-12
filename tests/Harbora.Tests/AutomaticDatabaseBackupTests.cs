using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Backups;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Servers;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Security;
using Harbora.Infrastructure.Services;
using Harbora.Infrastructure.Tenancy;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// Round nine, item one: a database a person creates gets a daily backup keeping seven, with nobody
/// setting it up — through the same <see cref="DatabaseProvisioningService.CreateAsync"/> the form,
/// Launch and the assistant all call. Each way it can be refused is here, and each one still creates
/// the database and says in both languages why there is no backup.
/// </summary>
public class AutomaticDatabaseBackupTests
{
    private sealed class Rig : IDisposable
    {
        public DatabaseServiceHarness H { get; } = new();
        public FakeServerEngineFactory Engines { get; } = new(new FakeDockerEngine());

        /// <summary>The harness's scripted quota by default; a real one when a test needs a plan to say no.</summary>
        public IQuotaService? Quota { get; set; }

        public AutomaticDatabaseBackup Backup() => new(
            H.Db, Quota ?? H.Quota, Engines, H.Audit, H.Clock, NullLogger<AutomaticDatabaseBackup>.Instance);

        public DatabaseProvisioningService Provisioning()
        {
            var caller = new DatabaseServiceHarness.Caller(H.WorkspaceId, H.UserId);
            return new DatabaseProvisioningService(
                H.Db, H.Engine, Quota ?? H.Quota, H.Scheduler, new PassthroughProtector(),
                new ProjectService(H.Db, H.Clock, audit: H.Audit), new ProjectAccessService(H.Db, caller),
                new ResourceCreationBilling(H.Db, H.Clock, Options.Create(new BillingOptions { Enabled = false })),
                H.Audit, caller, Backup());
        }

        public List<BackupSchedule> Schedules() => H.Db.BackupSchedules.IgnoreQueryFilters().AsNoTracking().ToList();

        public Plan GivePlan(int maxSchedules = 0, int maxRetention = 0)
        {
            var plan = new Plan { Name = "Small", MaxBackupSchedules = maxSchedules, MaxBackupRetentionCount = maxRetention };
            H.Db.Plans.Add(plan);
            H.Db.Workspaces.IgnoreQueryFilters().Single(w => w.Id == H.WorkspaceId).PlanId = plan.Id;
            H.Db.SaveChanges();
            Quota = new QuotaService(H.Db, Options.Create(new BillingOptions()));
            return plan;
        }

        public void Dispose() => H.Dispose();
    }

    // ---- the ordinary case ---------------------------------------------------------------------

    [Fact]
    public async Task A_new_PostgreSQL_database_is_backed_up_every_day_keeping_seven_with_nobody_setting_it_up()
    {
        using var rig = new Rig();

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request("Shop DB") with { IpAddress = "203.0.113.9" }, default);

        outcome.Ok.Should().BeTrue();
        var created = outcome.Value!;
        var schedule = rig.Schedules().Should().ContainSingle().Subject;
        schedule.WorkspaceId.Should().Be(rig.H.WorkspaceId);
        schedule.Type.Should().Be(BackupType.Database);
        schedule.TargetRef.Should().Be(created.ServiceId.ToString());
        schedule.ManagedServiceDatabaseId.Should().BeNull("null is the instance's own database, the one the page shows");
        schedule.IntervalHours.Should().Be(24);
        schedule.RetentionCount.Should().Be(7);
        schedule.IsEnabled.Should().BeTrue();

        created.Backup.Should().NotBeNull();
        created.Backup!.Scheduled.Should().BeTrue();
        created.Backup.ScheduleId.Should().Be(schedule.Id);
        created.Backup.Message.Should().Contain("every day").And.Contain("seven").And.Contain("Backups page");
        created.Backup.MessageFa.Should().Contain("هر روز").And.Contain("هفت");

        rig.H.Engine.Provisioned.Should().Equal(created.ServiceId);
        rig.H.Audit.Entries.Select(e => e.Action).Should().Equal("database.created", "backup.schedule_created");
        var entry = rig.H.Audit.Entries[1];
        entry.TargetType.Should().Be("backup_schedule");
        entry.TargetId.Should().Be(schedule.Id.ToString());
        entry.IpAddress.Should().Be("203.0.113.9");
        entry.MetadataJson.Should().Contain("\"automatic\":true").And.Contain(created.ServiceId.ToString());
    }

    [Fact]
    public async Task Its_first_run_waits_a_whole_day_so_a_database_still_being_set_up_is_not_backed_up_and_alerted_on()
    {
        using var rig = new Rig();

        await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        // BackupScheduler takes a schedule with no NextRunAt as due on its next five-minute tick.
        var schedule = rig.Schedules().Single();
        schedule.NextRunAt.Should().Be(rig.H.Clock.UtcNow.AddHours(24));
        schedule.LastRunAt.Should().BeNull();
    }

    [Fact]
    public async Task The_creation_has_committed_before_the_schedule_takes_the_lock_again()
    {
        using var rig = new Rig();
        var quota = new OrderRecordingQuota();
        rig.Quota = quota;

        await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        quota.Events.Should().Equal("lock", "commit", "lock", "commit");
    }

    // ---- where it goes -------------------------------------------------------------------------

    [Fact]
    public async Task A_workspace_with_no_destination_gets_the_local_one_the_Backups_page_would_make_and_keeps_using_it()
    {
        using var rig = new Rig();

        await rig.Provisioning().CreateAsync(rig.H.Request("first"), default);
        await rig.Provisioning().CreateAsync(rig.H.Request("second"), default);

        var destination = rig.H.Db.BackupDestinations.IgnoreQueryFilters().Should().ContainSingle(
            "the second database finds the destination the first one made").Subject;
        destination.Type.Should().Be(BackupDestinationType.Local);
        destination.Name.Should().Be("Local");
        destination.IsDefault.Should().BeTrue();
        destination.LocalPath.Should().BeNull("a local destination with no path writes to the staging directory");
        rig.Schedules().Should().HaveCount(2).And.OnlyContain(s => s.DestinationId == destination.Id);
    }

    [Fact]
    public async Task The_workspace_default_destination_is_used_when_it_has_one()
    {
        using var rig = new Rig();
        var other = new BackupDestination { WorkspaceId = rig.H.WorkspaceId, Name = "Old", Type = BackupDestinationType.Local };
        var bucket = new BackupDestination
        {
            WorkspaceId = rig.H.WorkspaceId, Name = "Bucket", Type = BackupDestinationType.S3, IsDefault = true,
            Bucket = "backups", Endpoint = "https://s3.example.com"
        };
        rig.H.Db.BackupDestinations.AddRange(other, bucket);
        await rig.H.Db.SaveChangesAsync();

        await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        rig.Schedules().Single().DestinationId.Should().Be(bucket.Id);
        rig.H.Db.BackupDestinations.IgnoreQueryFilters().Should().HaveCount(2, "none is added beside the ones it has");
    }

    // ---- only where a backup can really run ----------------------------------------------------

    [Fact]
    public async Task A_cache_is_still_created_but_gets_no_schedule_and_a_sentence_saying_why()
    {
        using var rig = new Rig();

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request("sessions", ManagedServiceType.Redis), default);

        outcome.Ok.Should().BeTrue();
        rig.H.Engine.Provisioned.Should().ContainSingle();
        rig.Schedules().Should().BeEmpty();
        var backup = outcome.Value!.Backup!;
        backup.Scheduled.Should().BeFalse();
        backup.Message.Should().StartWith("No daily backup was set up for sessions:").And.Contain("a cache");
        backup.MessageFa.Should().Contain("پشتیبان‌گیری روزانه تنظیم نشد").And.Contain("کش");
    }

    [Theory]
    [InlineData(ManagedServiceType.RabbitMq, "a message broker")]
    [InlineData(ManagedServiceType.Nats, "a message broker")]
    [InlineData(ManagedServiceType.Meilisearch, "a search index")]
    public async Task An_engine_with_no_dump_of_its_own_gets_no_schedule(ManagedServiceType type, string kind)
    {
        using var rig = new Rig();
        var service = Seed(rig, "thing", type);

        var setup = await rig.Backup().ScheduleAsync(service.Id, null, default);

        setup.Scheduled.Should().BeFalse();
        setup.Message.Should().Contain(kind);
        rig.Schedules().Should().BeEmpty();
    }

    [Theory]
    [InlineData(ManagedServiceType.PostgreSql)]
    [InlineData(ManagedServiceType.MySql)]
    [InlineData(ManagedServiceType.MariaDb)]
    [InlineData(ManagedServiceType.MongoDb)]
    public async Task Every_engine_with_a_dump_of_its_own_gets_one(ManagedServiceType type)
    {
        using var rig = new Rig();
        var service = Seed(rig, "data", type);

        var setup = await rig.Backup().ScheduleAsync(service.Id, null, default);

        setup.Scheduled.Should().BeTrue(setup.Message);
        rig.Schedules().Should().ContainSingle();
    }

    [Fact]
    public async Task A_read_replica_gets_none_because_its_primary_holds_the_same_data()
    {
        using var rig = new Rig();
        var primary = rig.H.SeedService("orders");
        var replica = rig.H.SeedService("orders-replica", primaryId: primary.Id);

        var setup = await rig.Backup().ScheduleAsync(replica.Id, null, default);

        setup.Scheduled.Should().BeFalse();
        setup.Message.Should().Contain("read replica");
        setup.MessageFa.Should().Contain("رپلیکا");
        rig.Schedules().Should().BeEmpty();
    }

    [Fact]
    public async Task A_database_on_a_server_the_backup_engine_refuses_gets_none_and_the_sentence_names_the_server()
    {
        using var rig = new Rig();
        var server = new Server { Name = "far-away", Hostname = "far.example.com", IsLocal = false, AgentEndpoint = "https://far.example.com:8443" };
        rig.H.Db.Servers.Add(server);
        await rig.H.Db.SaveChangesAsync();
        // Neither the factory's own local engine nor a node: the older inbound agent, which
        // BackupEngine.RequireCapableHost refuses before any helper starts.
        rig.Engines.On(server.Id, new FakeDockerEngine());
        rig.H.Scheduler.PlacedOn = server.Id;

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        outcome.Ok.Should().BeTrue("the database is never refused for want of a backup");
        rig.Schedules().Should().BeEmpty();
        outcome.Value!.Backup!.Scheduled.Should().BeFalse();
        outcome.Value.Backup.Message.Should().Contain("far-away");
        outcome.Value.Backup.MessageFa.Should().Contain("far-away");
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_at_all_gets_none_rather_than_an_exception()
    {
        using var rig = new Rig();
        var serverId = Guid.CreateVersion7();
        rig.Engines.Unreachable(serverId, "Node n-1 was revoked and takes no commands.");
        rig.H.Scheduler.PlacedOn = serverId;

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        outcome.Ok.Should().BeTrue();
        outcome.Value!.Backup!.Scheduled.Should().BeFalse();
        outcome.Value.Backup.Message.Should().Contain(serverId.ToString(), "with no row to name it, the id is all there is");
        rig.Schedules().Should().BeEmpty();
    }

    // ---- the plan ------------------------------------------------------------------------------

    [Fact]
    public async Task A_plan_with_no_room_for_another_schedule_gets_none_and_the_database_is_still_created()
    {
        using var rig = new Rig();
        rig.GivePlan(maxSchedules: 1);
        var existing = rig.H.SeedService("older");
        rig.H.Db.BackupSchedules.Add(new BackupSchedule
        {
            WorkspaceId = rig.H.WorkspaceId, DestinationId = Guid.CreateVersion7(), Type = BackupType.Database,
            TargetRef = existing.Id.ToString()
        });
        await rig.H.Db.SaveChangesAsync();

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        outcome.Ok.Should().BeTrue();
        rig.H.Db.ManagedServices.IgnoreQueryFilters().Should().HaveCount(2);
        rig.Schedules().Should().ContainSingle("only the schedule that was already there");
        var backup = outcome.Value!.Backup!;
        backup.Scheduled.Should().BeFalse();
        backup.Message.Should().Contain("The plan allows 1 backup schedule(s)").And.Contain("Backups page");
        backup.MessageFa.Should().Contain("این پلن حداکثر 1");
    }

    [Fact]
    public async Task A_plan_that_keeps_fewer_than_seven_gets_none_rather_than_a_schedule_past_its_limit()
    {
        using var rig = new Rig();
        rig.GivePlan(maxRetention: 3);

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        outcome.Ok.Should().BeTrue();
        rig.Schedules().Should().BeEmpty();
        outcome.Value!.Backup!.Message.Should().Contain("at most 3 backup(s)");
        outcome.Value.Backup.MessageFa.Should().Contain("حداکثر 3 نسخه");
    }

    [Fact]
    public async Task A_plan_with_room_gets_its_schedule_counted_like_any_other()
    {
        using var rig = new Rig();
        rig.GivePlan(maxSchedules: 1, maxRetention: 7);

        var first = await rig.Provisioning().CreateAsync(rig.H.Request("first"), default);
        var second = await rig.Provisioning().CreateAsync(rig.H.Request("second"), default);

        first.Value!.Backup!.Scheduled.Should().BeTrue();
        second.Value!.Backup!.Scheduled.Should().BeFalse("the first one took the plan's only schedule");
        rig.Schedules().Should().ContainSingle();
    }

    // ---- never twice, never half -----------------------------------------------------------------

    [Fact]
    public async Task A_database_that_already_has_a_schedule_is_not_given_a_second()
    {
        using var rig = new Rig();
        var service = rig.H.SeedService("orders");

        var first = await rig.Backup().ScheduleAsync(service.Id, null, default);
        var second = await rig.Backup().ScheduleAsync(service.Id, null, default);

        rig.Schedules().Should().ContainSingle();
        second.Scheduled.Should().BeTrue("the database is covered");
        second.ScheduleId.Should().Be(first.ScheduleId);
        second.Message.Should().Contain("already has a backup schedule");
    }

    [Fact]
    public async Task Something_going_wrong_while_setting_it_up_still_leaves_the_database_created_and_says_so()
    {
        using var rig = new Rig();
        rig.Quota = new ThrowingScheduleQuota();

        var outcome = await rig.Provisioning().CreateAsync(rig.H.Request(), default);

        outcome.Ok.Should().BeTrue();
        rig.H.Db.ManagedServices.IgnoreQueryFilters().Should().ContainSingle();
        rig.H.Engine.Provisioned.Should().ContainSingle("the provision was queued before the schedule was tried");
        outcome.Value!.Backup!.Scheduled.Should().BeFalse();
        outcome.Value.Backup.Message.Should().Contain("could not be saved");
        outcome.Value.Backup.MessageFa.Should().Contain("ذخیره نشد");
        rig.Schedules().Should().BeEmpty();
    }

    [Fact]
    public async Task A_save_that_fails_leaves_nothing_tracked_for_the_callers_next_save_to_write()
    {
        var options = new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("auto-backup-" + Guid.NewGuid()).Options;
        await using var db = new BrittleContext(options);
        var workspaceId = Guid.CreateVersion7();
        db.Workspaces.Add(new Workspace { Id = workspaceId, Name = "Acme", Slug = "acme" });
        var service = new ManagedService
        {
            WorkspaceId = workspaceId, EnvironmentId = Guid.CreateVersion7(), ServerId = Guid.CreateVersion7(),
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16", ContainerName = "harbora-svc-orders",
            VolumeName = "harbora-svc-orders-data", InternalPort = 5432, Username = "harbora", EncryptedPassword = "x"
        };
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync();
        db.FailTheNextSaveWith = new DbUpdateException("the disk is full");

        var setup = await new AutomaticDatabaseBackup(db, new DatabaseServiceHarness.ScriptedQuota(),
                new FakeServerEngineFactory(new FakeDockerEngine()), new DatabaseServiceHarness.RecordingAudit(),
                new FixedClock(), NullLogger<AutomaticDatabaseBackup>.Instance)
            .ScheduleAsync(service.Id, null, default);

        setup.Scheduled.Should().BeFalse();
        db.ChangeTracker.Entries().Should().NotContain(e => e.State == EntityState.Added,
            "Launch saves its own row with this context straight after, and would write a half-made schedule");
        await db.SaveChangesAsync();
        db.BackupSchedules.IgnoreQueryFilters().Should().BeEmpty();
        db.BackupDestinations.IgnoreQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public async Task A_service_built_without_it_creates_exactly_what_it_always_did()
    {
        using var h = new DatabaseServiceHarness();

        var outcome = await h.Provisioning().CreateAsync(h.Request(), default);

        outcome.Value!.Backup.Should().BeNull();
        h.Db.BackupSchedules.IgnoreQueryFilters().Should().BeEmpty();
    }

    // ---- removal ---------------------------------------------------------------------------------

    [Fact]
    public async Task Removing_a_database_removes_its_schedules_and_keeps_the_backups_it_took()
    {
        using var h = new ServiceFailureHarness();
        var service = await h.SeedServiceAsync();
        var other = Guid.CreateVersion7();
        h.Db.BackupSchedules.AddRange(
            new BackupSchedule { WorkspaceId = h.WorkspaceId, Type = BackupType.Database, TargetRef = service.Id.ToString() },
            new BackupSchedule { WorkspaceId = h.WorkspaceId, Type = BackupType.PostgresBaseBackup, TargetRef = service.Id.ToString() },
            new BackupSchedule { WorkspaceId = h.WorkspaceId, Type = BackupType.Database, TargetRef = other.ToString() });
        h.Db.Backups.Add(new Backup
        {
            WorkspaceId = h.WorkspaceId, Type = BackupType.Database, TargetRef = service.Id.ToString(),
            Status = BackupStatus.Completed, ArtifactPath = "database-orders.sql.gz"
        });
        await h.Db.SaveChangesAsync();

        await h.Engine().RemoveAsync(service.Id, deleteData: false, default);

        h.Db.BackupSchedules.IgnoreQueryFilters().AsNoTracking().Select(s => s.TargetRef).ToList()
            .Should().Equal([other.ToString()], "another database's schedule is not this removal's to touch");
        h.Db.Backups.IgnoreQueryFilters().Should().ContainSingle("what it already took stays restorable");
    }

    private static ManagedService Seed(Rig rig, string name, ManagedServiceType type)
    {
        var service = rig.H.SeedService(name);
        service.Type = type;
        rig.H.Db.SaveChanges();
        return service;
    }

    /// <summary>Records the order locks are taken and committed in.</summary>
    private sealed class OrderRecordingQuota : IQuotaService
    {
        public List<string> Events { get; } = [];

        public Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
        {
            Events.Add("lock");
            return Task.FromResult<IQuotaReservation>(new Reservation(Events));
        }

        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();

        private sealed class Reservation(List<string> events) : IQuotaReservation
        {
            public Task CommitAsync(CancellationToken ct)
            {
                events.Add("commit");
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    /// <summary>Lets the database be created and then throws on the schedule's own plan check.</summary>
    private sealed class ThrowingScheduleQuota : IQuotaService
    {
        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) => Task.FromResult(QuotaCheck.Ok);
        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? s, Guid? e, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();

        public Task<QuotaCheck> CanAddGovernedResourcesAsync(Guid w, GovernanceQuotaDelta delta, CancellationToken ct) =>
            throw new InvalidOperationException("the plan table could not be read");
    }
}
