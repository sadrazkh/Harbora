using System.Net;
using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Notifications;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Notifications;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The tenant-filter trap, for <c>EventDispatcher.PublishAsync</c> specifically (P6, 2026-08-20
/// platform-options plan). Background jobs and cross-workspace admin reads see an empty database
/// unless they use <c>IgnoreQueryFilters()</c> and then scope explicitly by <c>WorkspaceId</c> — the
/// codebase's own standing trap, proven here in both directions: a publish finds its own workspace's
/// subscriptions, and cannot reach another's.
///
/// <para>
/// The dispatcher's own isolated scope (see <c>EventDispatcher.PublishAsync</c>'s doc) is built here
/// deliberately bound to the WRONG tenant (<see cref="FixedWorkspaceScope"/> over Tenant B) while
/// publishing for Tenant A — the shape a background worker's ambient scope can genuinely be in. If
/// <c>PublishAsync</c>'s explicit <c>IgnoreQueryFilters() + WorkspaceId ==</c> predicate were ever
/// dropped in favour of trusting the ambient scope, this is the test that goes red: EF's own query
/// filter would AND an unrelated <c>WorkspaceId == TenantB</c> onto the query, and it would find
/// nothing at all for Tenant A — the "reports success having done nothing" failure mode the trap
/// describes.
/// </para>
/// </summary>
public class EventSubscriptionTenancyTests
{
    private static readonly Guid TenantA = Guid.NewGuid();
    private static readonly Guid TenantB = Guid.NewGuid();

    /// <summary>The same ingredients <c>NotificationQueueScope</c> gives a hand-built
    /// <c>NotificationService</c>, parameterised on the ambient <see cref="IWorkspaceScope"/> so a
    /// test can deliberately mismatch it against the workspace being published for.</summary>
    private sealed class ScopeContainer(string store, IWorkspaceScope scope) : IDisposable
    {
        private readonly ServiceProvider _provider = new ServiceCollection()
            .AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(store))
            .AddSingleton(scope)
            .AddSingleton<ISystemClock>(new FixedClock())
            .AddSingleton<JobSignal>()
            .AddSingleton<IJobCancellationRegistry, JobCancellationRegistry>()
            .AddScoped<IJobQueue, DatabaseJobQueue>()
            .BuildServiceProvider();

        public IServiceScopeFactory Factory => _provider.GetRequiredService<IServiceScopeFactory>();
        public void Dispose() => _provider.Dispose();
    }

    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    private sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static HarboraDbContext UnscopedDb(string store) => new(
        new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options,
        SystemWorkspaceScope.Instance);

    private static EventSubscription Sub(Guid workspaceId, string name) => new()
    {
        WorkspaceId = workspaceId, Name = name, Channel = AlertChannel.Webhook,
        EncryptedTarget = """{"url":"https://hooks.example.com/x"}""",
        Events = EventKind.DeploymentSucceeded, IsEnabled = true
    };

    [Fact]
    public async Task Publishing_for_tenant_A_finds_tenant_As_subscription_even_though_the_dispatchers_own_ambient_scope_is_tenant_B()
    {
        var store = "event-tenancy-" + Guid.NewGuid();
        var subA = Sub(TenantA, "A-hook");
        var subB = Sub(TenantB, "B-hook");
        using (var seed = UnscopedDb(store))
        {
            seed.EventSubscriptions.AddRange(subA, subB);
            await seed.SaveChangesAsync();
        }

        using var wrongAmbientScope = new ScopeContainer(store, new FixedWorkspaceScope(TenantB));
        using var dispatcherOwnDb = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options,
            new FixedWorkspaceScope(TenantB));

        var dispatcher = new EventDispatcher(
            dispatcherOwnDb, wrongAmbientScope.Factory, new PassthroughProtector(),
            new SingleHandlerFactory(new StubHandler(HttpStatusCode.OK)), new RecordingNotificationService(),
            new FixedClock(), Options.Create(new NotificationOptions()), NullLogger<EventDispatcher>.Instance);

        await dispatcher.PublishAsync(TenantA, EventKind.DeploymentSucceeded,
            new Dictionary<string, string> { ["app"] = "shop" }, default);

        using var verify = UnscopedDb(store);
        var deliveries = await verify.EventDeliveries.ToListAsync();

        deliveries.Should().ContainSingle("Tenant A's subscription must be found despite the dispatcher's own " +
                                           "ambient scope naming a different tenant")
            .Which.SubscriptionId.Should().Be(subA.Id);
    }

    [Fact]
    public async Task Publishing_for_tenant_A_never_creates_a_delivery_for_tenant_Bs_subscription()
    {
        var store = "event-tenancy-" + Guid.NewGuid();
        var subA = Sub(TenantA, "A-hook");
        var subB = Sub(TenantB, "B-hook");
        using (var seed = UnscopedDb(store))
        {
            seed.EventSubscriptions.AddRange(subA, subB);
            await seed.SaveChangesAsync();
        }

        using var systemScope = new ScopeContainer(store, SystemWorkspaceScope.Instance);
        using var dispatcherOwnDb = UnscopedDb(store);

        var dispatcher = new EventDispatcher(
            dispatcherOwnDb, systemScope.Factory, new PassthroughProtector(),
            new SingleHandlerFactory(new StubHandler(HttpStatusCode.OK)), new RecordingNotificationService(),
            new FixedClock(), Options.Create(new NotificationOptions()), NullLogger<EventDispatcher>.Instance);

        await dispatcher.PublishAsync(TenantA, EventKind.DeploymentSucceeded,
            new Dictionary<string, string> { ["app"] = "shop" }, default);

        using var verify = UnscopedDb(store);
        var deliveries = await verify.EventDeliveries.ToListAsync();

        deliveries.Should().OnlyContain(d => d.SubscriptionId != subB.Id,
            "no event published for Tenant A may ever address Tenant B's subscription");
        deliveries.Should().OnlyContain(d => d.WorkspaceId == TenantA);
    }

    [Fact]
    public async Task A_workspace_scoped_read_of_EventSubscriptions_cannot_see_another_tenants_row()
    {
        // The model-level half of the same guarantee — WorkspaceQueryFilterTests' own idiom, pointed
        // at the two tables this sub-project adds.
        var store = "event-tenancy-filter-" + Guid.NewGuid();
        var subA = Sub(TenantA, "A-hook");
        var subB = Sub(TenantB, "B-hook");
        using (var seed = UnscopedDb(store))
        {
            seed.EventSubscriptions.AddRange(subA, subB);
            await seed.SaveChangesAsync();
        }

        using var asTenantA = new HarboraDbContext(
            new DbContextOptionsBuilder<HarboraDbContext>().UseInMemoryDatabase(store).Options,
            new FixedWorkspaceScope(TenantA));

        (await asTenantA.EventSubscriptions.CountAsync()).Should().Be(1,
            "the table's own global query filter is the second, independent layer of protection");
        (await asTenantA.EventSubscriptions.FirstOrDefaultAsync(s => s.Id == subB.Id)).Should().BeNull(
            "tenant A must not resolve tenant B's subscription even with the exact id");
        (await asTenantA.EventSubscriptions.FirstOrDefaultAsync(s => s.Id == subA.Id)).Should().NotBeNull(
            "the filter must not be so aggressive that it hides the caller's own data");
    }
}
