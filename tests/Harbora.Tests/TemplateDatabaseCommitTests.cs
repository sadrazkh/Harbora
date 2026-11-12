using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Templates;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// A database made from a template is kept (round nine, brief H).
///
/// <para>
/// The database-only branch of <see cref="TemplateDeploymentService.DeployAsync"/> saved the project,
/// the database and its first hour, queued the provision, and returned without committing the
/// creation lock. On PostgreSQL that lock is a real transaction, so disposing it threw all of it away
/// while the page was handed the ids of rows that no longer existed. EF InMemory has no transaction —
/// the lock is a no-op there — so no test could see the rows go. What is asserted here is the commit
/// itself, and that it comes after everything it has to keep. The same creation against a real
/// PostgreSQL is <c>TemplateDatabaseCommitPostgresTests</c> in Harbora.Postgres.Tests.
/// </para>
/// </summary>
public class TemplateDatabaseCommitTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Says yes, or the refusal a test set, and writes down every lock, commit and dispose.</summary>
    private sealed class RecordingQuota(List<string> events) : IQuotaService
    {
        public QuotaCheck ServiceCheck { get; set; } = QuotaCheck.Ok;

        public Task<IQuotaReservation> AcquireCreationLockAsync(Guid workspaceId, CancellationToken ct)
        {
            events.Add("lock");
            return Task.FromResult<IQuotaReservation>(new Reservation(events));
        }

        public Task<QuotaCheck> CanAddAppAsync(Guid w, string? size, Guid? exclude, CancellationToken ct) =>
            Task.FromResult(QuotaCheck.Ok);

        public Task<QuotaCheck> CanAddServiceAsync(Guid w, string? size, CancellationToken ct) =>
            Task.FromResult(ServiceCheck);

        public Task<WorkspaceUsage> GetUsageAsync(Guid w, CancellationToken ct) => throw new NotSupportedException();

        private sealed class Reservation(List<string> events) : IQuotaReservation
        {
            public Task CommitAsync(CancellationToken ct)
            {
                events.Add("commit");
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync()
            {
                events.Add("dispose");
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Provisions nothing; records, in the same list, that a provision was queued.</summary>
    private sealed class RecordingEngine(List<string> events) : IManagedServiceEngine
    {
        public List<Guid> Queued { get; } = [];
        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = [];

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct)
        {
            events.Add("provision");
            Queued.Add(serviceId);
            return Task.CompletedTask;
        }

        public Task StartAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task StopAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct) => throw new NotSupportedException();
        public Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<RotatedApp>> RotatePasswordAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct) => throw new NotSupportedException();
        public Task<RedisMemoryPolicyOutcome> UpdateRedisMemoryPolicyAsync(Guid serviceId, string? policy, long maxMemoryBytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<(bool Ok, string? Error)> PromoteReplicaAsync(Guid replicaId, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class NoDeployments : IDeploymentEngine
    {
        public Task<Guid> QueueDeploymentAsync(DeploymentRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task CancelAsync(Guid deploymentId, CancellationToken ct) => throw new NotSupportedException();
        public Task ApproveAsync(Guid deploymentId, Guid approverUserId, CancellationToken ct) => throw new NotSupportedException();
        public Task RejectAsync(Guid deploymentId, Guid approverUserId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed record Rig(
        HarboraDbContext Db, TemplateDeploymentService Service, RecordingQuota Quota, RecordingEngine Engine,
        List<string> Events, Guid WorkspaceId, AppTemplate Template);

    private static Rig Build(string manifestJson)
    {
        var db = new HarboraDbContext(new DbContextOptionsBuilder<HarboraDbContext>()
            .UseInMemoryDatabase("template-db-commit-" + Guid.NewGuid()).Options);

        db.Add(new Server { Id = Guid.CreateVersion7(), Name = "local", IsLocal = true, Architecture = "amd64" });
        db.Settings.Add(new Harbora.Domain.Settings.Setting
        {
            Key = Harbora.Domain.Settings.SettingKeys.PlatformRootDomain, Value = "apps.example.test"
        });
        var template = new AppTemplate
        {
            Id = Guid.CreateVersion7(),
            Key = "postgres", Name = "PostgreSQL", Category = "databases",
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = manifestJson
        };
        db.Add(template);
        db.SaveChanges();

        var events = new List<string>();
        var quota = new RecordingQuota(events);
        var engine = new RecordingEngine(events);
        var service = new TemplateDeploymentService(
            db,
            new ProjectService(db, new FixedClock(Now)),
            quota,
            new PassthroughProtector(),
            engine,
            new NoDeployments(),
            new Harbora.Infrastructure.Billing.ResourceCreationBilling(
                db, new FixedClock(Now), Microsoft.Extensions.Options.Options.Create(
                    new Harbora.Infrastructure.Billing.BillingOptions { Enabled = false })),
            new AppAddressAssigner(db, new ConfigurationBuilder().Build()));

        return new Rig(db, service, quota, engine, events, Guid.CreateVersion7(), template);
    }

    private static TemplateDeployRequest Request(Rig rig) =>
        new(rig.WorkspaceId, Guid.CreateVersion7(), rig.Template.Id, "Orders", "Orders",
            RepositoryUrl: null, GitRef: null, Variables: new Dictionary<string, string>(), DeployNow: false);

    [Fact]
    public async Task A_database_template_commits_its_creation_after_queueing_the_provision()
    {
        var rig = Build("""{"service":"postgres"}""");

        var result = await rig.Service.DeployAsync(Request(rig), CancellationToken.None);

        // In this order: the database, its first hour and its provision job are one unit of work,
        // kept together or not at all. Without the commit this read lock, provision, dispose — and on
        // PostgreSQL the dispose is a rollback.
        rig.Events.Should().Equal("lock", "provision", "commit", "dispose");

        result.ServiceId.Should().NotBeNull();
        rig.Engine.Queued.Should().Equal(result.ServiceId!.Value);
        rig.Db.ManagedServices.IgnoreQueryFilters().Should().ContainSingle(s => s.Id == result.ServiceId);
        rig.Db.Projects.IgnoreQueryFilters().Should().ContainSingle(p => p.Id == result.ProjectId);
    }

    [Fact]
    public async Task A_refused_database_template_commits_nothing()
    {
        var rig = Build("""{"service":"postgres"}""");
        rig.Quota.ServiceCheck = QuotaCheck.Deny("Service limit reached (1).");

        var act = () => rig.Service.DeployAsync(Request(rig), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Service limit reached (1).");
        rig.Events.Should().Equal(["lock", "dispose"], "a refusal is exactly what the rollback is for");
        rig.Engine.Queued.Should().BeEmpty();
    }

    [Fact]
    public async Task A_stack_template_still_commits_once_and_before_its_databases_are_provisioned()
    {
        var rig = Build("""{"image":"nginx:alpine","port":80,"requires":["postgres"]}""");

        var result = await rig.Service.DeployAsync(Request(rig), CancellationToken.None);

        result.AppId.Should().NotBeNull();
        rig.Events.Should().Equal("lock", "commit", "provision", "dispose");
    }
}
