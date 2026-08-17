using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Features;
using Harbora.Infrastructure.Functions;
using Harbora.Infrastructure.Maintenance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// When a function is called, and when it deliberately is not.
///
/// <para>
/// The failure this guards against is the quiet one: a function that stops firing errors nowhere and
/// alerts nobody, and the first symptom is a report that never arrived. So the interesting cases
/// here are all refusals — an app that was never published, a function switched off, and above all a
/// workspace whose entitlement was taken away, whose already-deployed schedules must stop too.
/// </para>
/// </summary>
public class FunctionTriggerTests
{
    private static HarboraDbContext NewDb(string name) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(name).Options);

    private sealed record World(HarboraDbContext Db, Guid WorkspaceId, App App, FunctionDefinition Fn);

    private static async Task<World> SeedAsync(
        FeatureState entitlement = FeatureState.Enabled,
        bool published = true,
        bool functionEnabled = true,
        FunctionTrigger trigger = FunctionTrigger.Cron,
        string? cron = "* * * * *")
    {
        var db = NewDb("fn-trigger-" + Guid.NewGuid());

        var plan = new Plan { Name = "Starter", IsEnabled = true };
        var workspace = new Workspace { Name = "Acme", Slug = "acme", PlanId = plan.Id };
        db.Plans.Add(plan);
        db.Workspaces.Add(workspace);
        db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = FeatureScope.Plan, TargetId = plan.Id,
            FeatureKey = PlatformFeatures.Functions, State = entitlement
        });

        var app = new App
        {
            WorkspaceId = workspace.Id,
            Name = "fns",
            Slug = "fns",
            SourceType = AppSourceType.InlineCode,
            FunctionRuntime = FunctionRuntime.JavaScript,
            FunctionInvokeSecret = "secret",
            ContainerPort = FunctionProject.DefaultPort,
            PrivateAddressState = PrivateAddressOutcome.Registered,
            ActiveDeploymentId = published ? Guid.CreateVersion7() : null
        };
        db.Apps.Add(app);

        var fn = new FunctionDefinition
        {
            AppId = app.Id,
            WorkspaceId = workspace.Id,
            Name = "nightly",
            Slug = "nightly",
            Trigger = trigger,
            CronExpression = trigger == FunctionTrigger.Cron ? cron : null,
            EventKey = trigger == FunctionTrigger.Event ? FunctionEvents.DeploymentSucceeded : null,
            Code = "export default async () => {}",
            IsEnabled = functionEnabled
        };
        db.FunctionDefinitions.Add(fn);

        await db.SaveChangesAsync();
        return new World(db, workspace.Id, app, fn);
    }

    private static FunctionInvoker InvokerFor(HarboraDbContext db, RecordingJobQueue jobs) =>
        new(db, new NoHttp(), new PlainProtector(), jobs, new FeatureGate(db),
            NullLogger<FunctionInvoker>.Instance);

    // ------------------------------------------------------------ queueing

    [Fact]
    public async Task A_due_call_is_written_down_before_it_is_made()
    {
        // Durable by design: the row holds the envelope, and the queue carries only its id, so a
        // restart between the tick and the request still results in a call.
        var world = await SeedAsync();
        var jobs = new RecordingJobQueue();

        var id = await InvokerFor(world.Db, jobs).QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);

        id.Should().NotBeNull();
        var row = await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        row.EnvelopeJson.Should().Contain("\"trigger\":\"cron\"");
        row.CompletedAt.Should().BeNull("nothing has been called yet");
        jobs.Enqueued.Should().ContainSingle()
            .Which.Should().Be((JobKind.FunctionInvoke, row.Id, world.Fn.Id));
    }

    [Fact]
    public async Task Two_calls_of_one_function_are_kept_serial()
    {
        // Exclusive on the function, not the invocation: a handler that takes longer than its own
        // schedule must not end up running twice at once.
        var world = await SeedAsync();
        var jobs = new RecordingJobQueue();
        var invoker = InvokerFor(world.Db, jobs);

        await invoker.QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);
        await invoker.QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);

        jobs.Enqueued.Should().HaveCount(2);
        jobs.Enqueued.Should().OnlyContain(e => e.ExclusiveWith == world.Fn.Id);
    }

    [Fact]
    public async Task A_workspace_that_lost_the_feature_stops_firing()
    {
        // The entitlement has to stop code that is already deployed, not only the page that creates
        // more of it — otherwise a cancelled customer's schedules run until somebody notices.
        var world = await SeedAsync(entitlement: FeatureState.Locked);
        var jobs = new RecordingJobQueue();

        var id = await InvokerFor(world.Db, jobs).QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);

        id.Should().BeNull();
        jobs.Enqueued.Should().BeEmpty();
        (await world.Db.FunctionInvocations.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task An_app_that_was_never_published_is_not_called()
    {
        var world = await SeedAsync(published: false);
        var jobs = new RecordingJobQueue();

        (await InvokerFor(world.Db, jobs).QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task A_switched_off_function_is_not_called()
    {
        var world = await SeedAsync(functionEnabled: false);
        var jobs = new RecordingJobQueue();

        (await InvokerFor(world.Db, jobs).QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default))
            .Should().BeNull();
    }

    [Fact]
    public async Task An_app_with_no_secret_says_so_rather_than_failing_with_a_401()
    {
        var world = await SeedAsync();
        world.App.FunctionInvokeSecret = null;
        await world.Db.SaveChangesAsync();
        var jobs = new RecordingJobQueue();
        var invoker = InvokerFor(world.Db, jobs);

        var id = await invoker.QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);
        await invoker.ExecuteAsync(id!.Value, default);

        var row = await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        row.Succeeded.Should().BeFalse();
        row.Error.Should().Contain("invoke secret");
    }

    [Fact]
    public async Task An_app_with_no_private_name_is_reported_unreachable_rather_than_guessed_at()
    {
        // Without an alias the container answers only to a name that changes every deployment.
        var world = await SeedAsync();
        world.App.PrivateAddressState = PrivateAddressOutcome.Ambiguous;
        await world.Db.SaveChangesAsync();
        var jobs = new RecordingJobQueue();
        var invoker = InvokerFor(world.Db, jobs);

        var id = await invoker.QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);
        await invoker.ExecuteAsync(id!.Value, default);

        var row = await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        row.Error.Should().Contain("not reachable");
    }

    [Fact]
    public async Task A_completed_invocation_is_not_run_again()
    {
        // Jobs are re-claimed after a crash, so the handler has to be idempotent.
        var world = await SeedAsync();
        var jobs = new RecordingJobQueue();
        var invoker = InvokerFor(world.Db, jobs);

        var id = await invoker.QueueAsync(world.Fn.Id, FunctionTrigger.Cron, null, default);
        await invoker.ExecuteAsync(id!.Value, default);
        var firstError = (await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync()).Error;

        await invoker.ExecuteAsync(id.Value, default);

        (await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync()).Error.Should().Be(firstError);
    }

    // --------------------------------------------------------------- events

    [Fact]
    public async Task An_event_reaches_the_functions_that_subscribed_to_it()
    {
        var world = await SeedAsync(trigger: FunctionTrigger.Event);
        var jobs = new RecordingJobQueue();
        var bus = new FunctionEventBus(world.Db, InvokerFor(world.Db, jobs), NullLogger<FunctionEventBus>.Instance);

        await bus.PublishAsync(FunctionEvent.Create(
            FunctionEvents.DeploymentSucceeded, world.WorkspaceId, "api", ("app", "api")), default);

        jobs.Enqueued.Should().ContainSingle();
        var row = await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        row.EnvelopeJson.Should().Contain(FunctionEvents.DeploymentSucceeded);
    }

    [Fact]
    public async Task An_event_never_crosses_into_another_workspace()
    {
        // A customer's code learning that another customer's deployment failed would be a tenancy
        // leak dressed as a feature.
        var world = await SeedAsync(trigger: FunctionTrigger.Event);
        var jobs = new RecordingJobQueue();
        var bus = new FunctionEventBus(world.Db, InvokerFor(world.Db, jobs), NullLogger<FunctionEventBus>.Instance);

        await bus.PublishAsync(FunctionEvent.Create(
            FunctionEvents.DeploymentSucceeded, Guid.CreateVersion7(), "api"), default);

        jobs.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_function_subscribed_to_a_different_event_stays_asleep()
    {
        var world = await SeedAsync(trigger: FunctionTrigger.Event);
        var jobs = new RecordingJobQueue();
        var bus = new FunctionEventBus(world.Db, InvokerFor(world.Db, jobs), NullLogger<FunctionEventBus>.Instance);

        await bus.PublishAsync(FunctionEvent.Create(
            FunctionEvents.BackupFailed, world.WorkspaceId, "nightly"), default);

        jobs.Enqueued.Should().BeEmpty();
    }

    [Fact]
    public async Task Publishing_never_throws_at_the_thing_that_published()
    {
        // Every call site is something that has just succeeded or failed at its own job. A broken
        // handler must not take a deployment down with it.
        var world = await SeedAsync(trigger: FunctionTrigger.Event);
        var bus = new FunctionEventBus(world.Db, new ThrowingInvoker(), NullLogger<FunctionEventBus>.Instance);

        var act = async () => await bus.PublishAsync(
            FunctionEvent.Create(FunctionEvents.DeploymentSucceeded, world.WorkspaceId, "api"), default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task An_event_key_nothing_can_subscribe_to_is_ignored()
    {
        var world = await SeedAsync(trigger: FunctionTrigger.Event);
        var jobs = new RecordingJobQueue();
        var bus = new FunctionEventBus(world.Db, InvokerFor(world.Db, jobs), NullLogger<FunctionEventBus>.Instance);

        await bus.PublishAsync(new FunctionEvent("nothing.raises.this", world.WorkspaceId, null,
            new Dictionary<string, string?>()), default);

        jobs.Enqueued.Should().BeEmpty();
    }

    // ------------------------------------------------------------ scheduling

    [Fact]
    public async Task A_schedule_seen_for_the_first_time_waits_rather_than_firing()
    {
        // "Never run" is not "overdue". Treating it as overdue makes every new function fire the
        // moment it is published, whatever its schedule says.
        var world = await SeedAsync(cron: "0 3 * * *");
        var jobs = new RecordingJobQueue();
        var scheduler = SchedulerFor(world.Db, jobs, out _);

        await scheduler.TickAsync(default);

        jobs.Enqueued.Should().BeEmpty();
        (await world.Db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync()).NextRunAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_due_schedule_fires_and_advances_before_it_is_queued()
    {
        // Advanced first on purpose: a slow call, or a process that dies between the two, must not
        // leave the next tick treating this as still due.
        var world = await SeedAsync();
        world.Fn.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await world.Db.SaveChangesAsync();

        var jobs = new RecordingJobQueue();
        var scheduler = SchedulerFor(world.Db, jobs, out _);

        await scheduler.TickAsync(default);

        jobs.Enqueued.Should().ContainSingle();
        (await world.Db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync())
            .NextRunAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task An_unreadable_schedule_is_recorded_once_rather_than_shouted_every_minute()
    {
        var world = await SeedAsync(cron: "not a schedule");
        world.Fn.NextRunAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await world.Db.SaveChangesAsync();

        var jobs = new RecordingJobQueue();
        var scheduler = SchedulerFor(world.Db, jobs, out _);

        await scheduler.TickAsync(default);

        jobs.Enqueued.Should().BeEmpty();
        (await world.Db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync()).NextRunAt.Should().BeNull();
    }

    [Fact]
    public async Task A_call_that_was_queued_and_never_made_is_written_off()
    {
        // Otherwise it reads as "queued" for ever, and the one page that answers "is this still
        // firing?" fills with calls that are neither running nor finished.
        var world = await SeedAsync();
        world.Db.FunctionInvocations.Add(new FunctionInvocation
        {
            FunctionId = world.Fn.Id,
            AppId = world.App.Id,
            WorkspaceId = world.WorkspaceId,
            Trigger = FunctionTrigger.Cron,
            StartedAt = DateTimeOffset.UtcNow - FunctionCronScheduler.AbandonedAfter - TimeSpan.FromMinutes(1)
        });
        await world.Db.SaveChangesAsync();

        var scheduler = SchedulerFor(world.Db, new RecordingJobQueue(), out _);
        var settled = await scheduler.SettleAbandonedAsync(default);

        settled.Should().Be(1);
        var row = await world.Db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        row.CompletedAt.Should().NotBeNull();
        row.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task A_call_queued_a_moment_ago_is_left_alone()
    {
        var world = await SeedAsync();
        world.Db.FunctionInvocations.Add(new FunctionInvocation
        {
            FunctionId = world.Fn.Id, AppId = world.App.Id, WorkspaceId = world.WorkspaceId,
            Trigger = FunctionTrigger.Cron, StartedAt = DateTimeOffset.UtcNow
        });
        await world.Db.SaveChangesAsync();

        var scheduler = SchedulerFor(world.Db, new RecordingJobQueue(), out _);

        (await scheduler.SettleAbandonedAsync(default)).Should().Be(0);
    }

    // ------------------------------------------------------------- retention

    [Fact]
    public void Retention_takes_finished_calls_and_leaves_queued_ones()
    {
        // A row with no CompletedAt is not history — its job still holds the id, so deleting it
        // would make a call vanish rather than fail.
        var cutoff = DateTimeOffset.UtcNow.AddDays(-30);
        var rule = RetentionRule.FunctionInvocationsToDelete(cutoff).Compile();

        rule(new FunctionInvocation { CompletedAt = cutoff.AddDays(-1) }).Should().BeTrue();
        rule(new FunctionInvocation { CompletedAt = cutoff.AddDays(1) }).Should().BeFalse();
        rule(new FunctionInvocation { CompletedAt = null, StartedAt = cutoff.AddDays(-5) }).Should().BeFalse();
    }

    // --------------------------------------------------------------- helpers

    private static FunctionCronScheduler SchedulerFor(
        HarboraDbContext db, RecordingJobQueue jobs, out IServiceProvider provider)
    {
        var services = new ServiceCollection();
        services.AddSingleton(db);
        services.AddSingleton<ISystemClock>(new SystemClockNow());
        services.AddSingleton<IJobQueue>(jobs);
        services.AddSingleton<ISecretProtector>(new PlainProtector());
        services.AddSingleton<IHttpClientFactory>(new NoHttp());
        services.AddSingleton<IFeatureGate>(new FeatureGate(db));
        services.AddSingleton<IFunctionInvoker>(sp => new FunctionInvoker(
            db, new NoHttp(), new PlainProtector(), jobs, new FeatureGate(db),
            NullLogger<FunctionInvoker>.Instance));

        provider = services.BuildServiceProvider();
        return new FunctionCronScheduler(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<FunctionCronScheduler>.Instance);
    }

    private sealed class SystemClockNow : ISystemClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>Records what was queued instead of running it.</summary>
    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<(JobKind Kind, Guid TargetId, Guid ExclusiveWith)> Enqueued { get; } = [];

        public Task<Guid> EnqueueAsync(
            JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
        {
            Enqueued.Add((kind, targetId, targetId));
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task<Guid> EnqueueExclusiveAsync(
            JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null, CancellationToken ct = default)
        {
            Enqueued.Add((kind, targetId, exclusiveWith));
            return Task.FromResult(Guid.CreateVersion7());
        }

        public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    /// <summary>Every request fails to connect — which is what a panel with no containers would see.</summary>
    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefusingHandler());

        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("connection refused");
        }
    }

    private sealed class PlainProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => new byte[32];
    }

    private sealed class ThrowingInvoker : IFunctionInvoker
    {
        public Task<Guid?> QueueAsync(Guid functionId, FunctionTrigger trigger, FunctionEvent? evt, CancellationToken ct) =>
            throw new InvalidOperationException("the queue is on fire");

        public Task ExecuteAsync(Guid invocationId, CancellationToken ct) => Task.CompletedTask;
    }
}
