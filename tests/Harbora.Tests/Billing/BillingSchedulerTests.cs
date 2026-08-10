using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Jobs;
using Harbora.Infrastructure.Billing;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class BillingSchedulerTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_activation_queues_only_the_hour_that_just_ended()
    {
        await using var services = Provider();
        var scheduler = Scheduler(services);

        await scheduler.ScheduleDueAsync(default);
        await scheduler.ScheduleDueAsync(default);

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await db.BillingRuns.SingleAsync()).BillingHour.Should().Be(Now.AddMinutes(-30).AddHours(-1));
        (await db.Jobs.SingleAsync()).Kind.Should().Be(JobKind.BillingHour);
    }

    [Fact]
    public async Task A_restart_fills_every_missing_ended_hour_oldest_first()
    {
        await using var services = Provider();
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            db.BillingRuns.Add(new BillingRun
            {
                BillingHour = new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero),
                Status = BillingRunStatus.Succeeded
            });
            await db.SaveChangesAsync();
        }

        await Scheduler(services).ScheduleDueAsync(default);

        await using var read = services.CreateAsyncScope();
        var hours = await read.ServiceProvider.GetRequiredService<HarboraDbContext>().BillingRuns
            .OrderBy(r => r.BillingHour).Select(r => r.BillingHour).ToListAsync();
        hours.Should().Equal(
            new DateTimeOffset(2026, 8, 10, 6, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 7, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 8, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task An_incomplete_hour_is_offered_to_the_queue_again_without_deleting_its_history()
    {
        await using var services = Provider();
        Guid runId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var run = new BillingRun
            {
                BillingHour = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
                Status = BillingRunStatus.Incomplete,
                UpdatedAt = Now.AddHours(-1)
            };
            db.BillingRuns.Add(run);
            db.Jobs.Add(new Job
            {
                Kind = JobKind.BillingHour,
                TargetId = run.Id,
                Status = JobStatus.Succeeded,
                FinishedAt = Now.AddHours(-1)
            });
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await Scheduler(services).ScheduleDueAsync(default);

        await using var read = services.CreateAsyncScope();
        var jobs = await read.ServiceProvider.GetRequiredService<HarboraDbContext>().Jobs
            .Where(j => j.TargetId == runId).OrderBy(j => j.CreatedAt).ToListAsync();
        jobs.Should().HaveCount(2);
        jobs.Should().ContainSingle(j => j.Status == JobStatus.Pending);
    }

    [Theory]
    [InlineData((int)BillingRunStatus.Queued, 0)]
    [InlineData((int)BillingRunStatus.Running, -60)]
    public async Task A_run_left_without_a_live_job_is_recovered(int status, int ageMinutes)
    {
        await using var services = Provider();
        Guid runId;
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var run = new BillingRun
            {
                BillingHour = new DateTimeOffset(2026, 8, 10, 9, 0, 0, TimeSpan.Zero),
                Status = (BillingRunStatus)status,
                UpdatedAt = Now.AddMinutes(ageMinutes)
            };
            db.BillingRuns.Add(run);
            await db.SaveChangesAsync();
            runId = run.Id;
        }

        await Scheduler(services).ScheduleDueAsync(default);

        await using var read = services.CreateAsyncScope();
        var dbAfter = read.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await dbAfter.BillingRuns.SingleAsync(r => r.Id == runId)).Status
            .Should().Be(BillingRunStatus.Queued);
        (await dbAfter.Jobs.SingleAsync(j => j.TargetId == runId)).Status.Should().Be(JobStatus.Pending);
    }

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        var store = "billing-scheduler-" + Guid.NewGuid();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(store));
        services.AddSingleton<ISystemClock>(new FixedClock(Now));
        return services.BuildServiceProvider();
    }

    private static BillingScheduler Scheduler(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new BillingOptions { Enabled = true, MaxBackfillHours = 72 }),
        NullLogger<BillingScheduler>.Instance);
}
