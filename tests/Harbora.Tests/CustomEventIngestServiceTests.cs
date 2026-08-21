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
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// F3 (2026-08-21 functions-and-services plan, "Custom events from customer apps"): the other
/// direction through <c>FunctionInvoker</c>'s door. The priority the plan itself names is tenancy in
/// both directions — this codebase's background and cross-workspace paths have repeatedly read the
/// wrong scope and reported success — and the second priority is that an unsubscribed key is visible,
/// never silently dropped behind the ingest endpoint's own 200.
/// </summary>
public class CustomEventIngestServiceTests
{
    private static readonly PassthroughProtector Protector = new();

    private sealed record World(Guid WorkspaceId, App App, string PlaintextSecret);

    /// <summary>One shared db, since the tenancy tests need two workspaces' apps to actually coexist
    /// — a fresh in-memory database per world would make "a foreign secret" untestable by
    /// construction.</summary>
    private static HarboraDbContext NewDb() => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase("custom-ingest-" + Guid.NewGuid()).Options);

    private static async Task<World> SeedAppAsync(
        HarboraDbContext db, string label, string? subscribeTo = null, bool asFunctionApp = true)
    {
        var workspace = Guid.CreateVersion7();
        var plan = new Plan { Name = "Starter-" + label, IsEnabled = true };
        db.Plans.Add(plan);
        db.Workspaces.Add(new Workspace { Id = workspace, Name = label, Slug = "ws-" + label, PlanId = plan.Id });
        db.FeatureGrants.Add(new FeatureGrant
        {
            Scope = FeatureScope.Plan, TargetId = plan.Id,
            FeatureKey = PlatformFeatures.Functions, State = FeatureState.Enabled
        });

        var secret = "secret-" + label;
        var app = new App
        {
            WorkspaceId = workspace,
            Name = "fns-" + label,
            Slug = "fns-" + label,
            SourceType = asFunctionApp ? AppSourceType.InlineCode : AppSourceType.GitRepository,
            FunctionRuntime = FunctionRuntime.JavaScript,
            FunctionInvokeSecret = Protector.Protect(secret),
            ContainerPort = FunctionProject.DefaultPort,
            PrivateAddressState = PrivateAddressOutcome.Registered,
            ActiveDeploymentId = Guid.CreateVersion7()
        };
        db.Apps.Add(app);

        if (subscribeTo is not null)
        {
            db.FunctionDefinitions.Add(new FunctionDefinition
            {
                AppId = app.Id, WorkspaceId = workspace, Name = "listener", Slug = "listener",
                Trigger = FunctionTrigger.Event, EventKey = subscribeTo,
                Code = "export default async () => {}", IsEnabled = true
            });
        }

        await db.SaveChangesAsync();
        return new World(workspace, app, secret);
    }

    private static CustomEventIngestService ServiceFor(HarboraDbContext db, RecordingJobQueue jobs) =>
        new(db, Protector,
            new FunctionEventBus(db,
                new FunctionInvoker(db, new NoHttp(), Protector, jobs, new FeatureGate(db),
                    ScopeFactory(), NullLogger<FunctionInvoker>.Instance),
                NullLogger<FunctionEventBus>.Instance),
            NullLogger<CustomEventIngestService>.Instance);

    private static IServiceScopeFactory ScopeFactory()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEventPublisher>(new RecordingEventPublisher());
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    // ------------------------------------------------------------------------------- the happy path

    [Fact]
    public async Task An_app_emits_into_its_own_workspaces_subscribed_function()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme", subscribeTo: "custom.order.paid");
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, world.PlaintextSecret,
            new CustomEventIngestRequest("order.paid", "order-42", new Dictionary<string, string?> { ["amount"] = "1000" }),
            default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Accepted);
        result.Key.Should().Be("custom.order.paid");
        jobs.Enqueued.Should().ContainSingle();
        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.WorkspaceId.Should().Be(world.WorkspaceId);
        invocation.EnvelopeJson.Should().Contain("custom.order.paid");
    }

    // ---------------------------------------------------------------------------------- tenancy: in

    [Fact]
    public async Task A_foreign_workspaces_secret_cannot_emit_into_this_apps_id()
    {
        var db = NewDb();
        var acme = await SeedAppAsync(db, "acme", subscribeTo: "custom.order.paid");
        var globex = await SeedAppAsync(db, "globex", subscribeTo: "custom.order.paid");
        var jobs = new RecordingJobQueue();

        // globex's own, genuine secret — presented against acme's app id.
        var result = await ServiceFor(db, jobs).IngestAsync(
            acme.App.Id, globex.PlaintextSecret,
            new CustomEventIngestRequest("order.paid", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Unauthorized);
        jobs.Enqueued.Should().BeEmpty("a foreign secret proves nothing about acme's app, so nothing may fire in either workspace");
        (await db.FunctionCustomEventKeys.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "an unauthorized attempt is not a sighting — recording it would let a guessed id leave a mark on a workspace that never emitted anything");
    }

    // --------------------------------------------------------------------------------- tenancy: out

    [Fact]
    public async Task An_app_emits_only_into_its_own_workspace_never_the_other()
    {
        var db = NewDb();
        var acme = await SeedAppAsync(db, "acme-out", subscribeTo: "custom.order.paid");
        var globex = await SeedAppAsync(db, "globex-out", subscribeTo: "custom.order.paid");
        var jobs = new RecordingJobQueue();

        await ServiceFor(db, jobs).IngestAsync(
            acme.App.Id, acme.PlaintextSecret,
            new CustomEventIngestRequest("order.paid", null, null), default);

        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        invocation.WorkspaceId.Should().Be(acme.WorkspaceId);
        invocation.WorkspaceId.Should().NotBe(globex.WorkspaceId,
            "globex subscribed to the very same key name — it must never see acme's event");
    }

    [Fact]
    public async Task An_unknown_app_id_is_refused()
    {
        var db = NewDb();
        await SeedAppAsync(db, "acme-unknown");
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            Guid.CreateVersion7(), "whatever", new CustomEventIngestRequest("order.paid", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Unauthorized);
    }

    [Fact]
    public async Task A_wrong_secret_for_a_real_app_id_is_refused()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-wrong");
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, "not-the-secret", new CustomEventIngestRequest("order.paid", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Unauthorized);
    }

    [Fact]
    public async Task An_app_that_is_not_a_function_app_cannot_ingest()
    {
        // Only InlineCode apps are ever issued a FunctionInvokeSecret in the running platform — this
        // pins that the ingest door does not accidentally widen to every app if that ever changed.
        var db = NewDb();
        var world = await SeedAppAsync(db, "not-a-function-app", asFunctionApp: false);
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, world.PlaintextSecret, new CustomEventIngestRequest("order.paid", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Unauthorized);
    }

    // ----------------------------------------------------------------------- visible, never silent

    [Fact]
    public async Task A_key_nobody_subscribes_to_yet_is_still_recorded_as_seen()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-unseen"); // no subscriber at all
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, world.PlaintextSecret,
            new CustomEventIngestRequest("shipment.created", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.Accepted);
        jobs.Enqueued.Should().BeEmpty("nothing subscribed yet — but that is not the same as dropped");
        var seen = await db.FunctionCustomEventKeys.IgnoreQueryFilters().SingleAsync();
        seen.WorkspaceId.Should().Be(world.WorkspaceId);
        seen.Key.Should().Be("custom.shipment.created");
        seen.TimesSeen.Should().Be(1);
    }

    [Fact]
    public async Task Repeated_ingests_of_the_same_key_increment_the_count_on_one_row()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-repeat");
        var jobs = new RecordingJobQueue();
        var service = ServiceFor(db, jobs);

        await service.IngestAsync(world.App.Id, world.PlaintextSecret,
            new CustomEventIngestRequest("shipment.created", null, null), default);
        await service.IngestAsync(world.App.Id, world.PlaintextSecret,
            new CustomEventIngestRequest("shipment.created", null, null), default);

        var seen = await db.FunctionCustomEventKeys.IgnoreQueryFilters().SingleAsync();
        seen.TimesSeen.Should().Be(2);
    }

    // ------------------------------------------------------------------- the namespace is forced

    [Fact]
    public async Task A_caller_cannot_impersonate_a_platform_event()
    {
        var db = NewDb();
        // One function subscribed the real way, one to what an attacker would have to land on
        // instead. If the namespace were not forced, the first would fire on an ingest call.
        var world = await SeedAppAsync(db, "acme-spoof", subscribeTo: FunctionEvents.DeploymentSucceeded);
        db.FunctionDefinitions.Add(new FunctionDefinition
        {
            AppId = world.App.Id, WorkspaceId = world.WorkspaceId, Name = "on-custom-deploy", Slug = "on-custom-deploy",
            Trigger = FunctionTrigger.Event, EventKey = "custom.deployment.succeeded",
            Code = "export default async () => {}", IsEnabled = true
        });
        await db.SaveChangesAsync();
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, world.PlaintextSecret,
            new CustomEventIngestRequest(FunctionEvents.DeploymentSucceeded, null, null), default);

        result.Key.Should().Be("custom.deployment.succeeded");
        // Only the function subscribed to the namespaced key ran — the real deployment.succeeded
        // listener stayed asleep, which is the entire point of forcing the prefix server-side.
        jobs.Enqueued.Should().ContainSingle();
        var fired = await db.FunctionDefinitions.IgnoreQueryFilters()
            .Where(f => f.AppId == world.App.Id).ToListAsync();
        var invocation = await db.FunctionInvocations.IgnoreQueryFilters().SingleAsync();
        fired.Single(f => f.Id == invocation.FunctionId).EventKey.Should().Be("custom.deployment.succeeded");
    }

    [Fact]
    public async Task A_garbage_key_is_refused_and_never_recorded()
    {
        var db = NewDb();
        var world = await SeedAppAsync(db, "acme-garbage");
        var jobs = new RecordingJobQueue();

        var result = await ServiceFor(db, jobs).IngestAsync(
            world.App.Id, world.PlaintextSecret, new CustomEventIngestRequest("!!!", null, null), default);

        result.Outcome.Should().Be(CustomEventIngestOutcome.InvalidKey);
        (await db.FunctionCustomEventKeys.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        jobs.Enqueued.Should().BeEmpty();
    }

    // --------------------------------------------------------------------------------- helpers

    private sealed class RecordingJobQueue : IJobQueue
    {
        public List<(JobKind Kind, Guid TargetId, Guid ExclusiveWith)> Enqueued { get; } = [];

        public Task<Guid> EnqueueAsync(JobKind kind, Guid targetId, Guid? workspaceId = null, CancellationToken ct = default)
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

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new RefusingHandler());

        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                throw new HttpRequestException("connection refused");
        }
    }
}
