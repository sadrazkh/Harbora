using FluentAssertions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Harbora.Infrastructure.Tenancy;
using Harbora.Tests.Fakes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Harbora.Tests.Billing;

public sealed class StorageMeasurerBillingTests
{
    [Fact]
    public async Task The_automatic_measurement_pass_reaches_a_managed_database_volume()
    {
        var docker = new FakeDockerEngine();
        docker.OneOffOutput.Add("4096");
        var serverId = Guid.CreateVersion7();
        var serviceId = Guid.CreateVersion7();
        var services = new ServiceCollection();
        var store = "db-storage-" + Guid.NewGuid();
        services.AddDbContext<HarboraDbContext>(o => o.UseInMemoryDatabase(store));
        services.AddSingleton<ISystemClock>(new FixedClock(WalletHarness.Now));
        services.AddSingleton<IServerEngineFactory>(new FakeServerEngineFactory(docker).On(serverId, docker));
        await using var provider = services.BuildServiceProvider();

        await using (var seed = provider.CreateAsyncScope())
        {
            seed.ServiceProvider.GetRequiredService<HarboraDbContext>().ManagedServices.Add(new ManagedService
            {
                Id = serviceId,
                WorkspaceId = Guid.CreateVersion7(),
                ServerId = serverId,
                Name = "tenant-db",
                VolumeName = "harbora-svc-tenant-db-data",
                Status = ServiceStatus.Running
            });
            await seed.ServiceProvider.GetRequiredService<HarboraDbContext>().SaveChangesAsync();
        }

        var measurer = new StorageMeasurer(
            provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<StorageMeasurer>.Instance);
        await measurer.MeasureOneAsync(default);

        await using var read = provider.CreateAsyncScope();
        var database = await read.ServiceProvider.GetRequiredService<HarboraDbContext>().ManagedServices
            .SingleAsync(s => s.Id == serviceId);
        database.StorageBytes.Should().Be(4096);
        database.StorageMeasuredAt.Should().Be(WalletHarness.Now);
        docker.OneOffRequests.Single().Binds.Should().ContainSingle(m =>
            m.Source == "harbora-svc-tenant-db-data" && m.ReadOnly);
    }
}
