using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Harbora.Infrastructure.Deployments;

namespace Harbora.Infrastructure.Services;

/// <summary>
/// Provisions backing services as containers on the shared Harbora network. Credentials live
/// encrypted in the DB; the container gets the plaintext only through its seed env on first boot.
/// </summary>
public sealed class ManagedServiceEngine(
    HarboraDbContext db,
    IDockerEngine docker,
    ISecretProtector protector,
    IBackgroundJobQueue queue,
    ISystemClock clock,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<ManagedServiceEngine> logger) : IManagedServiceEngine
{
    private readonly HarboraRuntimeOptions _opt = options.Value;

    public IReadOnlyList<ServiceCatalogEntry> Catalog =>
        ServiceCatalog.All.Values.Select(d => new ServiceCatalogEntry(
            d.Type, d.DisplayName, d.DisplayNameFa, $"{d.ImageRepo}:{d.Versions[0]}",
            d.Versions, d.Port, d.HasDatabaseName)).ToList();

    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) =>
        queue.EnqueueAsync((sp, jobCt) =>
            sp.GetRequiredService<ManagedServiceEngine>().ProvisionAsync(serviceId, jobCt), ct).AsTask();

    /// <summary>Runs on the background worker. Pulls the image and (re)creates the container.</summary>
    public async Task ProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;
        var def = ServiceCatalog.All[svc.Type];

        try
        {
            svc.Status = ServiceStatus.Provisioning;
            await db.SaveChangesAsync(ct);

            var creds = CredsFor(svc);
            var image = $"{def.ImageRepo}:{svc.Version}";

            await docker.EnsureNetworkAsync(_opt.Network, ct);
            await docker.EnsureVolumeAsync(svc.VolumeName, ct);
            await docker.PullImageAsync(image, new Progress<string>(l => logger.LogDebug("{Svc}: {Line}", svc.Name, l)), ct);
            await RemoveContainerByNameAsync(svc.ContainerName, ct);

            await docker.RunContainerAsync(new DockerRunRequest(
                image, svc.ContainerName, _opt.Network,
                def.Env(creds),
                new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = svc.Name },
                new[] { (svc.VolumeName, def.DataMountPath, false) },
                def.Port, 0, 0, null, def.Command(creds)), ct);

            svc.Status = ServiceStatus.Running;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Provisioned managed service {Name} ({Type}).", svc.Name, svc.Type);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to provision service {Name}.", svc.Name);
            svc.Status = ServiceStatus.Failed;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task StartAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        var id = await FindContainerIdAsync(svc.ContainerName, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct); // restart starts a stopped container
        svc.Status = ServiceStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    public async Task StopAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        var id = await FindContainerIdAsync(svc.ContainerName, ct);
        if (id is not null) await docker.StopContainerAsync(id, ct);
        svc.Status = ServiceStatus.Stopped;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;
        var id = await FindContainerIdAsync(svc.ContainerName, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        // With the backup engine in place, honouring deleteData is now safe: the UI warns and
        // users can back up first. Default keeps the volume.
        if (deleteData) await docker.RemoveVolumeAsync(svc.VolumeName, ct);
        db.ManagedServices.Remove(svc);
        await db.SaveChangesAsync(ct);
    }

    public async Task<ServiceConnectionInfo> GetConnectionInfoAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking().FirstAsync(s => s.Id == serviceId, ct);
        var def = ServiceCatalog.All[svc.Type];
        var creds = CredsFor(svc);
        var (full, masked) = def.Conn(creds);
        return new ServiceConnectionInfo(creds.Host, creds.Port, creds.User, creds.Password,
            def.HasDatabaseName ? creds.Database : null, full, masked);
    }

    public async Task<IReadOnlyDictionary<string, string>> BuildAttachEnvAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.AsNoTracking().FirstAsync(s => s.Id == serviceId, ct);
        return ServiceCatalog.All[svc.Type].AttachEnv(CredsFor(svc));
    }

    private ServiceCreds CredsFor(ManagedService svc) =>
        new(svc.ContainerName, ServiceCatalog.All[svc.Type].Port, svc.Username, SafeUnprotect(svc.EncryptedPassword), svc.DatabaseName);

    private async Task<string?> FindContainerIdAsync(string name, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        return containers.FirstOrDefault(c => c.Name == name)?.Id;
    }

    private async Task RemoveContainerByNameAsync(string name, CancellationToken ct)
    {
        var id = await FindContainerIdAsync(name, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
