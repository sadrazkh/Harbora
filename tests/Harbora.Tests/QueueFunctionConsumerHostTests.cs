using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Features;
using Harbora.Domain.Functions;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Services;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Features;
using Harbora.Infrastructure.Functions;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F2 (2026-08-21 functions-and-services plan, "Queue-triggered functions"): the panel-side RabbitMQ
/// bridge, proven through a fake at the broker seam — there is no Docker and no live RabbitMQ on this
/// machine, so nothing about a real AMQP round-trip is exercised here, only
/// <see cref="QueueFunctionConsumerHost"/>'s own ack/nack/dead-letter/reconnect/tenancy behaviour
/// against something shaped exactly like the real thing.
///
/// <para>
/// The four things the plan says decide correctness, each with its own test below: a message never
/// vanishing silently (ack / requeue-once / dead-letter), restart survival (a fresh instance
/// re-derives its worker set from the database alone, the same way
/// <c>PanelNetworkRebinderTests</c> proves boot re-derives network membership), a broker that is
/// down surfacing rather than going quiet, and tenancy in both directions.
/// </para>
/// </summary>
public class QueueFunctionConsumerHostTests
{
    private static (ServiceProvider Services, string DbName) BuildProvider(IHttpClientFactory http)
    {
        var dbName = "queue-consumer-" + Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<ISystemClock>(new FixedClock());
        services.AddSingleton<ISecretProtector, PassthroughProtector>();
        services.AddSingleton<IJobQueue, NoOpJobQueue>();
        services.AddSingleton<IEventPublisher, RecordingEventPublisher>();
        services.AddSingleton(http);
        services.AddScoped<IFeatureGate>(sp => new FeatureGate(sp.GetRequiredService<HarboraDbContext>()));
        services.AddScoped<IFunctionInvoker, FunctionInvoker>();
        return (services.BuildServiceProvider(), dbName);
    }

    private static QueueFunctionConsumerHost BuildHost(ServiceProvider sp, IQueueBrokerConnectionFactory broker) =>
        new(sp.GetRequiredService<IServiceScopeFactory>(), broker,
            sp.GetRequiredService<ISecretProtector>(), sp.GetRequiredService<ISystemClock>(),
            NullLogger<QueueFunctionConsumerHost>.Instance);

    private sealed record World(Guid WorkspaceId, App App, FunctionDefinition Fn, ManagedService Broker);

    private static async Task<World> SeedAsync(
        ServiceProvider sp, bool enabled = true, bool published = true, bool withSecret = true,
        Guid? brokerWorkspaceOverride = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();

        var plan = new Plan { Name = "Starter", IsEnabled = true };
        var workspace = new Workspace
        {
            Name = "Acme", Slug = "acme-" + Guid.NewGuid().ToString("N")[..8], PlanId = plan.Id
        };
        db.Plans.Add(plan);
        db.Workspaces.Add(workspace);
        db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = FeatureScope.Plan, TargetId = plan.Id,
            FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
        });

        var app = new App
        {
            WorkspaceId = workspace.Id, Name = "worker", Slug = "worker-" + Guid.NewGuid().ToString("N")[..8],
            SourceType = AppSourceType.InlineCode, FunctionRuntime = FunctionRuntime.JavaScript,
            FunctionInvokeSecret = withSecret ? "secret" : null,
            ContainerPort = FunctionProject.DefaultPort,
            PrivateAddressState = PrivateAddressOutcome.Registered,
            ActiveDeploymentId = published ? Guid.CreateVersion7() : null
        };
        db.Apps.Add(app);

        var broker = new ManagedService
        {
            WorkspaceId = brokerWorkspaceOverride ?? workspace.Id,
            ServerId = Guid.CreateVersion7(),
            Name = "broker",
            Type = ManagedServiceType.RabbitMq,
            Version = "4-management-alpine",
            ContainerName = "broker-" + Guid.NewGuid().ToString("N")[..8],
            VolumeName = "broker-data",
            Username = "guest",
            EncryptedPassword = new PassthroughProtector().Protect("hunter2"),
            Status = ServiceStatus.Running
        };
        db.ManagedServices.Add(broker);

        var fn = new FunctionDefinition
        {
            AppId = app.Id, WorkspaceId = workspace.Id, Name = "worker-fn", Slug = "worker-fn",
            Trigger = FunctionTrigger.Queue, QueueServiceId = broker.Id, QueueName = "orders",
            Code = "export default async () => {}", IsEnabled = enabled
        };
        db.FunctionDefinitions.Add(fn);

        await db.SaveChangesAsync();
        return new World(workspace.Id, app, fn, broker);
    }

    // ------------------------------------------------------ #1: never vanishes silently

    [Fact]
    public async Task A_successful_delivery_is_acked_and_becomes_one_invocation()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var connection = new FakeQueueBrokerConnection([new QueueDelivery("order #1", Redelivered: false)]);
        var broker = new FakeQueueBrokerConnectionFactory(_ => connection);
        var host = BuildHost(sp, broker);

        await host.ConsumeOnceAsync(world.Fn.Id, default);

        connection.Outcomes.Should().Equal(QueueAckOutcome.Ack);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.Succeeded.Should().BeTrue();
        invocation.Trigger.Should().Be(FunctionTrigger.Queue);
        (await db.FunctionQueueDeadLetters.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_failed_delivery_is_requeued_once_then_parked_as_a_dead_letter_and_never_a_third_time()
    {
        // No HTTP fake needed: an app with no invoke secret is a deterministic failure (the same path
        // FunctionTriggerTests uses), so this proves the ack sequence without depending on a fake
        // HTTP handler being wired correctly too.
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp, withSecret: false);
        var connection = new FakeQueueBrokerConnection([new QueueDelivery("order #2", Redelivered: false)]);
        var broker = new FakeQueueBrokerConnectionFactory(_ => connection);
        var host = BuildHost(sp, broker);

        await host.ConsumeOnceAsync(world.Fn.Id, default);

        // First attempt fails and is requeued (Redelivered: false); the fake's own requeue puts the
        // same message back marked Redelivered — this asserts on the sequence, not just the count, so
        // a consumer that dead-lettered on the first failure (never earning the customer their one
        // retry) would fail this the same as one that requeued forever.
        connection.Outcomes.Should().Equal(QueueAckOutcome.NackRequeue, QueueAckOutcome.NackDrop);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        (await db.FunctionInvocations.IgnoreQueryFilters().CountAsync()).Should().Be(2,
            "the customer's message was genuinely attempted twice, and both attempts are FunctionInvocation rows");

        var deadLetter = await db.FunctionQueueDeadLetters.IgnoreQueryFilters().SingleAsync();
        deadLetter.FunctionId.Should().Be(world.Fn.Id);
        deadLetter.QueueName.Should().Be("orders");
        deadLetter.Body.Should().Be("order #2");
        deadLetter.Reason.Should().Contain("invoke secret");
    }

    // ------------------------------------------------------ #2: restart survival

    [Fact]
    public async Task A_fresh_instance_with_no_prior_memory_rediscovers_an_already_enabled_function_from_the_database()
    {
        // The PanelNetworkRebinder shape: nothing about this test's host has ever seen this function
        // before — it is seeded directly into the database, exactly as a panel restarting after a
        // redeploy would find rows nobody in this process created.
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var broker = new FakeQueueBrokerConnectionFactory(_ => new FakeQueueBrokerConnection([]));
        var host = BuildHost(sp, broker);
        using var stopping = new CancellationTokenSource();

        await host.ReconcileAsync(stopping.Token);

        host.RunningFunctionIds.Should().Contain(world.Fn.Id);
        stopping.Cancel();
    }

    [Fact]
    public async Task Disabling_a_function_stops_its_worker_on_the_next_reconcile()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var broker = new FakeQueueBrokerConnectionFactory(_ => new FakeQueueBrokerConnection([]));
        var host = BuildHost(sp, broker);
        using var stopping = new CancellationTokenSource();

        await host.ReconcileAsync(stopping.Token);
        host.RunningFunctionIds.Should().Contain(world.Fn.Id);

        using (var scope = sp.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
            var fn = await db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync(f => f.Id == world.Fn.Id);
            fn.IsEnabled = false;
            await db.SaveChangesAsync();
        }

        await host.ReconcileAsync(stopping.Token);

        host.RunningFunctionIds.Should().NotContain(world.Fn.Id);
        stopping.Cancel();
    }

    // ------------------------------------------------------ #3: a broker that is down is not silence

    [Fact]
    public async Task A_broker_that_refuses_the_connection_is_recorded_rather_than_thrown_past()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var broker = new FakeQueueBrokerConnectionFactory(
            _ => throw new InvalidOperationException("connection refused"));
        var host = BuildHost(sp, broker);

        var act = async () => await host.ConsumeOnceAsync(world.Fn.Id, default);
        await act.Should().NotThrowAsync("a down broker is recorded, not left to crash the worker loop");

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync(f => f.Id == world.Fn.Id);
        fn.QueueLastError.Should().Contain("connection refused");
        fn.QueueLastAttemptAt.Should().NotBeNull();
    }

    [Fact]
    public async Task A_broker_error_reaches_the_dashboards_attention_block()
    {
        // Proves the other half of #3: AttentionService actually reads what ConsumeOnceAsync writes.
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var broker = new FakeQueueBrokerConnectionFactory(
            _ => throw new InvalidOperationException("connection refused"));
        var host = BuildHost(sp, broker);
        await host.ConsumeOnceAsync(world.Fn.Id, default);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var attention = new Harbora.Infrastructure.Dashboard.AttentionService(
            db, new FixedClock(), Microsoft.Extensions.Options.Options.Create(new Harbora.Infrastructure.Monitoring.MonitoringOptions()));

        var items = await attention.BuildAsync(world.WorkspaceId, default);

        var item = items.Should().ContainSingle(i =>
            i.DetailKey == Harbora.Infrastructure.Dashboard.AttentionRules.ChannelQueueDetail).Subject;
        item.ActionUrl.Should().Be("/functions");
        item.TitleArgs.Should().Contain("worker-fn (worker)");
    }

    [Fact]
    public async Task A_successful_reconnect_clears_the_broker_error()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var world = await SeedAsync(sp);
        var failingBroker = new FakeQueueBrokerConnectionFactory(
            _ => throw new InvalidOperationException("connection refused"));
        var host = BuildHost(sp, failingBroker);
        await host.ConsumeOnceAsync(world.Fn.Id, default);

        var recoveredBroker = new FakeQueueBrokerConnectionFactory(_ => new FakeQueueBrokerConnection([]));
        var recoveredHost = BuildHost(sp, recoveredBroker);
        await recoveredHost.ConsumeOnceAsync(world.Fn.Id, default);

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync(f => f.Id == world.Fn.Id);
        fn.QueueLastError.Should().BeNull();
    }

    // ------------------------------------------------------ #4: tenancy, both directions

    [Fact]
    public async Task Two_workspaces_own_queue_functions_are_both_found()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var worldA = await SeedAsync(sp);
        var worldB = await SeedAsync(sp);
        var broker = new FakeQueueBrokerConnectionFactory(_ => new FakeQueueBrokerConnection([]));
        var host = BuildHost(sp, broker);
        using var stopping = new CancellationTokenSource();

        await host.ReconcileAsync(stopping.Token);

        host.RunningFunctionIds.Should().Contain([worldA.Fn.Id, worldB.Fn.Id]);
        stopping.Cancel();
    }

    [Fact]
    public async Task A_function_cannot_reach_a_brokers_belonging_to_another_workspace()
    {
        var (sp, _) = BuildProvider(new OkHttp());
        var otherWorkspaceId = Guid.CreateVersion7();
        var world = await SeedAsync(sp, brokerWorkspaceOverride: otherWorkspaceId);
        var broker = new FakeQueueBrokerConnectionFactory(_ => new FakeQueueBrokerConnection([]));
        var host = BuildHost(sp, broker);

        await host.ConsumeOnceAsync(world.Fn.Id, default);

        broker.ConnectAttempts.Should().BeEmpty(
            "the tenancy check must refuse before ever opening a connection to another workspace's broker");

        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<HarboraDbContext>();
        var fn = await db.FunctionDefinitions.IgnoreQueryFilters().SingleAsync(f => f.Id == world.Fn.Id);
        fn.QueueLastError.Should().Contain("another workspace");
    }

    // --------------------------------------------------------------- fakes

    private sealed class NoOpJobQueue : IJobQueue
    {
        public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default) =>
            Task.FromResult(Guid.CreateVersion7());

        public Task<Guid> EnqueueExclusiveAsync(
            JobKind kind, Guid targetId, Guid exclusiveWith, Guid? workspaceId = null, CancellationToken ct = default) =>
            Task.FromResult(Guid.CreateVersion7());

        public Task<bool> RequestCancellationAsync(JobKind kind, Guid targetId, CancellationToken ct = default) =>
            Task.FromResult(false);
    }

    private sealed class OkHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OkHandler());

        private sealed class OkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }

    /// <summary>
    /// Mirrors real broker semantics closely enough for these tests: a nack-requeue puts the same
    /// message back, now marked <see cref="QueueDelivery.Redelivered"/> — never re-delivering after a
    /// drop, which is what keeps "requeue once, then dead-letter" from becoming an infinite loop here.
    /// </summary>
    private sealed class FakeQueueBrokerConnection(IEnumerable<QueueDelivery> deliveries) : IQueueBrokerConnection
    {
        private readonly Queue<QueueDelivery> _pending = new(deliveries);
        public List<QueueAckOutcome> Outcomes { get; } = [];
        public bool Disposed { get; private set; }

        public async Task ConsumeAsync(
            string queueName, Func<QueueDelivery, CancellationToken, Task<QueueAckOutcome>> handle, CancellationToken ct)
        {
            while (_pending.Count > 0 && !ct.IsCancellationRequested)
            {
                var delivery = _pending.Dequeue();
                var outcome = await handle(delivery, ct);
                Outcomes.Add(outcome);

                if (outcome == QueueAckOutcome.NackRequeue)
                    _pending.Enqueue(delivery with { Redelivered = true });
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeQueueBrokerConnectionFactory(Func<QueueBrokerAddress, IQueueBrokerConnection> connect)
        : IQueueBrokerConnectionFactory
    {
        public List<QueueBrokerAddress> ConnectAttempts { get; } = [];

        public Task<IQueueBrokerConnection> ConnectAsync(QueueBrokerAddress address, CancellationToken ct)
        {
            ConnectAttempts.Add(address);
            return Task.FromResult(connect(address));
        }
    }
}
