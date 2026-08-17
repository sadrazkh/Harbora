using FluentAssertions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Networking;
using Harbora.Modules.Backup.Contracts;
using Harbora.Modules.Backup.Infrastructure;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Harbora.Tests;

/// <summary>
/// The backup module's own database stager — the fourth of the four one-off container paths P3
/// (2026-08-17 app-environment-management design) moves off the shared workspace network and onto
/// the database's own environment network.
/// </summary>
public class DatabaseTargetStagerNetworkTests
{
    private static HarboraDbContext Db() => new(new DbContextOptionsBuilder<HarboraDbContext>()
        .UseInMemoryDatabase("stager-network-" + Guid.NewGuid()).Options);

    private static DatabaseTargetStager Stager(HarboraDbContext db) => new(
        db, new PassthroughProtector(), new AlwaysResolves(),
        Options.Create(new BackupModuleOptions()),
        NullLogger<DatabaseTargetStager>.Instance);

    [Fact]
    public async Task Planning_a_database_export_reaches_it_on_its_environments_own_network()
    {
        using var db = Db();
        var workspace = new Workspace { Id = Guid.NewGuid(), Name = "Acme", Slug = "acme" };
        var project = new Harbora.Domain.Projects.Project
        { Id = Guid.NewGuid(), WorkspaceId = workspace.Id, Name = "Shop", Slug = "shop" };
        var environment = new Harbora.Domain.Projects.Environment
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, ProjectId = project.Id,
            Name = "Production", Slug = "prod", IsDefault = true
        };
        var service = new ManagedService
        {
            Id = Guid.NewGuid(), WorkspaceId = workspace.Id, EnvironmentId = environment.Id,
            Name = "orders", Type = ManagedServiceType.PostgreSql, Version = "16-alpine",
            ContainerName = "harbora-orders", InternalPort = 5432, Username = "harbora",
            EncryptedPassword = "s3cret", DatabaseName = "orders", VolumeName = "orders-data"
        };
        db.Workspaces.Add(workspace);
        db.Projects.Add(project);
        db.Environments.Add(environment);
        db.ManagedServices.Add(service);
        await db.SaveChangesAsync();

        var (plan, error) = await Stager(db).PlanAsync(service.Id, default);

        error.Should().BeNull();
        plan.Should().NotBeNull();
        EnvironmentNetwork.IsEnvironmentNetwork(plan!.Execution.NetworkMode).Should().BeTrue(
            $"the stager planned to export on '{plan.Execution.NetworkMode}', not this database's " +
            "own environment network");
    }

    private sealed class AlwaysResolves : IDatabaseBackupProviderResolver
    {
        public bool TryResolve(DatabaseEngine engine, out IDatabaseBackupProvider provider)
        {
            provider = new NeverCalled();
            return true;
        }
    }

    /// <summary>PlanAsync never touches a provider's methods — only StageAsync does.</summary>
    private sealed class NeverCalled : IDatabaseBackupProvider
    {
        public DatabaseEngine Engine => DatabaseEngine.PostgreSql;

        public Task<DatabaseBackupResult> CreateBackupAsync(DatabaseBackupContext context, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DatabaseRestoreResult> RestoreAsync(DatabaseRestoreContext context, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<DatabaseBackupVerificationResult> VerifyAsync(
            DatabaseBackupVerificationContext context, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
