using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Jobs;
using Harbora.Domain.Servers;
using Harbora.Domain.Templates;
using Harbora.Domain.Tenancy;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Jobs;
using Harbora.Infrastructure.Networking;
using Harbora.Infrastructure.Projects;
using Harbora.Infrastructure.Templates;
using Harbora.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Postgres.Tests;

/// <summary>
/// A database made from a template is still there afterwards (round nine, brief H).
///
/// <para>
/// The database-only branch of <see cref="TemplateDeploymentService.DeployAsync"/> returned without
/// committing the creation lock, which here — and only here, EF InMemory has no transaction — is a
/// real transaction. Disposing it rolled back the project, the database, its first hour and its
/// provision job, and the page was handed the ids of rows that no longer existed.
/// <c>TemplateDatabaseCommitTests</c> in the fast suite asserts the commit; this asserts what the
/// commit is for: a second connection sees every one of those rows once the creating context is gone.
/// </para>
/// </summary>
[Collection(PostgresLane.Collection)]
public sealed class TemplateDatabaseCommitPostgresTests(PostgresLane lane)
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid WorkspaceId = new("72222222-0000-0000-0000-000000000001");
    private static readonly Guid TemplateId = new("72222222-0000-0000-0000-000000000002");
    private const string SizeKey = "lane-small";
    private const long HourlyRate = 1_500;
    private const long StartingBalance = 100_000;

    private sealed class Clock : ISystemClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class IdentityProtector : ISecretProtector
    {
        public string Protect(string plaintext) => plaintext;
        public string Unprotect(string ciphertext) => ciphertext;
        public byte[] DeriveKey(string purpose) => throw new NotSupportedException();
    }

    /// <summary>
    /// Queues through the real durable queue on the creating context, as <c>ManagedServiceEngine</c>
    /// does, so the job row is written inside the creation's transaction. Nothing else is reachable
    /// from a database template.
    /// </summary>
    private sealed class QueueingEngine(IJobQueue jobs) : IManagedServiceEngine
    {
        public IReadOnlyList<ServiceCatalogEntry> Catalog { get; } = [];

        public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) =>
            jobs.EnqueueAsync(JobKind.ServiceProvision, serviceId, WorkspaceId, ct);

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

    [PostgresFact]
    public async Task The_database_its_first_hour_and_its_provision_job_are_all_still_there_afterwards()
    {
        var connectionString = await lane.FreshlyMigratedAsync("template_database_commit");
        await SeedAsync(connectionString);

        TemplateDeployResult result;
        await using (var db = PostgresLane.Open(connectionString))
        {
            var clock = new Clock();
            var billing = Options.Create(new BillingOptions { Enabled = true });
            var quota = new QuotaService(db, billing);
            var service = new TemplateDeploymentService(
                db,
                new ProjectService(db, clock, quota),
                quota,
                new IdentityProtector(),
                new QueueingEngine(new DatabaseJobQueue(db, clock, new JobCancellationRegistry(), new JobSignal())),
                new NoDeployments(),
                new ResourceCreationBilling(db, clock, billing),
                new AppAddressAssigner(db, new ConfigurationBuilder().Build()));

            result = await service.DeployAsync(new TemplateDeployRequest(
                WorkspaceId, Guid.CreateVersion7(), TemplateId, "Orders", "Orders",
                RepositoryUrl: null, GitRef: null, Variables: new Dictionary<string, string>(), DeployNow: false,
                InstanceSizeKey: SizeKey), default);
        }

        // A second connection, after the creating context is gone: only what was committed is here.
        await using var reader = PostgresLane.Open(connectionString);
        var serviceId = result.ServiceId!.Value;

        var database = await reader.ManagedServices.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        database.Id.Should().Be(serviceId);
        database.Status.Should().Be(ServiceStatus.Provisioning);
        (await reader.ManagedServiceDatabases.IgnoreQueryFilters().AsNoTracking()
                .CountAsync(d => d.ManagedServiceId == serviceId))
            .Should().Be(1, "a PostgreSQL instance's own admin database is created with it");
        (await reader.Projects.IgnoreQueryFilters().AsNoTracking().SingleAsync()).Id.Should().Be(result.ProjectId);

        var job = await reader.Jobs.AsNoTracking().SingleAsync();
        job.Kind.Should().Be(JobKind.ServiceProvision);
        job.TargetId.Should().Be(serviceId);
        job.Status.Should().Be(JobStatus.Pending);

        var charge = await reader.BillingLedger.IgnoreQueryFilters().AsNoTracking().SingleAsync();
        charge.Kind.Should().Be(LedgerKind.Charge);
        charge.ResourceId.Should().Be(serviceId);
        charge.AmountMinor.Should().Be(-HourlyRate);
        (await reader.Wallets.IgnoreQueryFilters().AsNoTracking().SingleAsync()).BalanceMinor
            .Should().Be(StartingBalance - HourlyRate, "the first hour that was taken is the one recorded");
    }

    private static async Task SeedAsync(string connectionString)
    {
        await using var db = PostgresLane.Open(connectionString);
        db.Workspaces.Add(new Workspace { Id = WorkspaceId, Name = "Template", Slug = "template" });
        db.Wallets.Add(new Wallet { WorkspaceId = WorkspaceId, BalanceMinor = StartingBalance });
        db.Servers.Add(new Server { Name = "local", IsLocal = true, Architecture = "amd64" });
        db.InstanceSizes.Add(new InstanceSize
        {
            Key = SizeKey, Name = "Lane small", CpuCores = 0.5, MemoryBytes = 512L * 1024 * 1024,
            DiskBytes = 5L * 1024 * 1024 * 1024, RunningRatePerHourMinor = HourlyRate, IsEnabled = true
        });
        db.AppTemplates.Add(new AppTemplate
        {
            Id = TemplateId, Key = "postgres", Name = "PostgreSQL", Category = "databases",
            IsBuiltIn = true, IsEnabled = true, Status = TemplateStatus.Approved,
            ManifestJson = """{"service":"postgres"}"""
        });
        await db.SaveChangesAsync();
    }
}
