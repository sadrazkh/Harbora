using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Monitoring;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Monitoring;
using Harbora.Infrastructure.Services;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// P4's surfacing half (2026-08-17 app-environment-management design): a managed service that fails
/// to provision used to leave only <c>Status = Failed</c> behind — no reason on the row, no incident,
/// no alert, nothing a dashboard could show. <see cref="ManagedServiceEngine.ProvisionAsync"/> now
/// writes <see cref="ManagedService.ErrorMessage"/>, opens an <see cref="AlertIncident"/> through the
/// same <c>IncidentService</c> Phase 6 built, and raises <see cref="AlertEvent.ServiceProvisionFailed"/>
/// through <c>NotificationService</c> — the same three surfaces a failed deployment already gets.
/// </summary>
public class ManagedServiceFailureSurfacingTests
{
    [Fact]
    public async Task A_provision_that_fails_records_the_reason_on_the_row()
    {
        using var h = new ServiceFailureHarness();
        var svc = await h.SeedServiceAsync();
        h.Docker.RunFailure = new InvalidOperationException("container refused to start: port already bound");

        await h.Engine().ProvisionAsync(svc.Id, default);

        var reloaded = await h.ReadServiceAsync(svc.Id);
        reloaded.Status.Should().Be(ServiceStatus.Failed);
        reloaded.ErrorMessage.Should().Contain("port already bound",
            "the reason a provision failed must reach the row, not only the operator's own log");
    }

    [Fact]
    public async Task A_provision_that_fails_opens_an_incident_naming_the_service()
    {
        using var h = new ServiceFailureHarness();
        var svc = await h.SeedServiceAsync();
        h.Docker.RunFailure = new InvalidOperationException("simulated failure");

        await h.Engine().ProvisionAsync(svc.Id, default);

        var incident = h.Db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle().Subject;
        incident.Condition.Should().Be(AlertEvent.ServiceProvisionFailed);
        incident.SubjectRef.Should().Be(svc.Id.ToString());
        incident.WorkspaceId.Should().Be(h.WorkspaceId);
        incident.ClosedAt.Should().BeNull("the incident stays open until acknowledged or it expires");
    }

    [Fact]
    public async Task A_provision_that_fails_notifies_the_workspace_with_the_reason()
    {
        using var h = new ServiceFailureHarness();
        var svc = await h.SeedServiceAsync();
        h.Docker.RunFailure = new InvalidOperationException("simulated failure: disk full");

        await h.Engine().ProvisionAsync(svc.Id, default);

        var sent = h.Notifications.Notifications.Should().ContainSingle().Subject;
        sent.Event.Should().Be(AlertEvent.ServiceProvisionFailed);
        sent.Workspace.Should().Be(h.WorkspaceId);
        sent.Data.Get("ServiceName").Should().Be(svc.Name);
        sent.Data.Get("Reason").Should().Contain("disk full");
    }

    [Fact]
    public async Task A_provision_that_later_succeeds_clears_the_reason_and_resolves_the_incident()
    {
        using var h = new ServiceFailureHarness();
        var svc = await h.SeedServiceAsync();
        h.Docker.RunFailure = new InvalidOperationException("first attempt fails");
        await h.Engine().ProvisionAsync(svc.Id, default);

        h.Docker.RunFailure = null;
        await h.Engine().ProvisionAsync(svc.Id, default);

        var reloaded = await h.ReadServiceAsync(svc.Id);
        reloaded.Status.Should().Be(ServiceStatus.Running);
        reloaded.ErrorMessage.Should().BeNull(
            "a database that came up is not still explaining why an earlier attempt did not");

        var incident = h.Db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle().Subject;
        incident.ClosedAt.Should().NotBeNull("the same row recovering is the condition clearing on its own");
        incident.ClosedReason.Should().Be(IncidentClosedReason.Resolved);
    }

    [Fact]
    public async Task A_billing_refusal_also_records_a_reason_and_opens_an_incident()
    {
        using var h = new ServiceFailureHarness(billingEnabled: true);
        var svc = await h.SeedServiceAsync();

        await h.Engine().ProvisionAsync(svc.Id, default);

        var reloaded = await h.ReadServiceAsync(svc.Id);
        reloaded.Status.Should().Be(ServiceStatus.Failed);
        reloaded.ErrorMessage.Should().NotBeNullOrWhiteSpace(
            "the billing-refusal path used to be the one this gap was named after");
        h.Db.AlertIncidents.IgnoreQueryFilters().Should().ContainSingle()
            .Which.Condition.Should().Be(AlertEvent.ServiceProvisionFailed);
    }
}

/// <summary>The real <see cref="ManagedServiceEngine"/> over a fake daemon, with the notification and
/// incident seams a real workspace would have wired.</summary>
internal sealed class ServiceFailureHarness : IDisposable
{
    private readonly string _database = "svc-failure-" + Guid.NewGuid();
    private readonly bool _billingEnabled;

    public Guid WorkspaceId { get; } = Guid.NewGuid();
    public Guid EnvironmentId { get; } = Guid.NewGuid();
    public HarboraDbContext Db { get; }
    public FakeDockerEngine Docker { get; } = new();
    public PassthroughProtector Protector { get; } = new();
    public FixedClock Clock { get; } = new();
    public RecordingNotificationService Notifications { get; } = new();

    public ServiceFailureHarness(bool billingEnabled = false)
    {
        _billingEnabled = billingEnabled;
        Db = Read();
        Db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Acme", Slug = "acme" });
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = WorkspaceId, Name = "Shop", Slug = "shop" };
        Db.Projects.Add(project);
        Db.Environments.Add(new Harbora.Domain.Projects.Environment
        {
            Id = EnvironmentId, WorkspaceId = WorkspaceId, ProjectId = project.Id,
            Name = "Production", Slug = "production", IsDefault = true
        });
        Db.SaveChanges();
    }

    private HarboraDbContext Read() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase(_database).Options);

    public ManagedServiceEngine Engine() => new(
        Db,
        new SingleEngineFactory(Docker),
        Protector,
        new NoopJobQueue(),
        new Harbora.Infrastructure.Billing.BillingGate(
            Db, Options.Create(new Harbora.Infrastructure.Billing.BillingOptions { Enabled = _billingEnabled })),
        Options.Create(new HarboraRuntimeOptions()),
        Clock,
        NullLogger<ManagedServiceEngine>.Instance,
        new PassthroughRedactor(),
        Notifications,
        new IncidentService(Db));

    public async Task<ManagedService> SeedServiceAsync()
    {
        var service = new ManagedService
        {
            WorkspaceId = WorkspaceId,
            EnvironmentId = EnvironmentId,
            ServerId = Guid.CreateVersion7(),
            Name = "orders",
            Type = ManagedServiceType.PostgreSql,
            Version = "16-alpine",
            ContainerName = "harbora-svc-orders",
            InternalPort = 5432,
            Username = "harbora",
            EncryptedPassword = Protector.Protect("original-pw12"),
            DatabaseName = "orders",
            VolumeName = "harbora-svc-orders-data",
            Status = ServiceStatus.Provisioning
        };
        Db.ManagedServices.Add(service);
        await Db.SaveChangesAsync();
        return service;
    }

    public async Task<ManagedService> ReadServiceAsync(Guid id)
    {
        using var db = Read();
        return await db.ManagedServices.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    public void Dispose() => Db.Dispose();
}
