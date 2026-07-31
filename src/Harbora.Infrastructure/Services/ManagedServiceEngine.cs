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
    IServerEngineFactory engineFactory,
    ISecretProtector protector,
    IJobQueue jobs,
    IOptions<HarboraRuntimeOptions> options,
    ISystemClock clock,
    ILogger<ManagedServiceEngine> logger) : IManagedServiceEngine
{
    private readonly HarboraRuntimeOptions _opt = options.Value;

    /// <summary>Small image used only to walk a volume and add up what is in it.</summary>
    private const string MeasuringImage = "alpine:3.20";

    public IReadOnlyList<ServiceCatalogEntry> Catalog =>
        ServiceCatalog.All.Values.Select(d => new ServiceCatalogEntry(
            d.Type, d.DisplayName, d.DisplayNameFa, $"{d.ImageRepo}:{d.Versions[0]}",
            d.Versions, d.Port, d.HasDatabaseName)).ToList();

    public Task QueueProvisionAsync(Guid serviceId, CancellationToken ct) =>
        jobs.EnqueueAsync(Harbora.Domain.Jobs.JobKind.ServiceProvision, serviceId, ct);

    /// <summary>Runs on the background worker. Pulls the image and (re)creates the container.</summary>
    public async Task ProvisionAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;
        var def = ServiceCatalog.All[svc.Type];
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);

        try
        {
            svc.Status = ServiceStatus.Provisioning;
            await db.SaveChangesAsync(ct);

            var creds = CredsFor(svc);
            var image = $"{def.ImageRepo}:{svc.Version}";

            // On its environment's own network, so a staging service cannot reach a production
            // database by name. The workspace network stays attached while the platform moves over
            // — see NetworkPlan — or apps that have not redeployed lose their database.
            var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
            var workspaceNetwork = _opt.WorkspaceNetwork(wsSlug);
            var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);
            var networks = Networking.NetworkPlan.For(environmentNetwork, workspaceNetwork, keepWorkspaceNetwork: true);
            var network = Networking.NetworkPlan.Primary(environmentNetwork, workspaceNetwork);

            foreach (var name in networks) await docker.EnsureNetworkAsync(name, ct);
            await docker.EnsureVolumeAsync(svc.VolumeName, ct);
            await docker.PullImageAsync(image, new Progress<string>(l => logger.LogDebug("{Svc}: {Line}", svc.Name, l)), ct);
            await RemoveContainerByNameAsync(docker, svc.ContainerName, ct);

            await docker.RunContainerAsync(new DockerRunRequest(
                image, svc.ContainerName, network,
                def.Env(creds),
                new Dictionary<string, string> { ["harbora.managed"] = "true", ["harbora.service"] = svc.Name },
                new[] { (svc.VolumeName, def.DataMountPath, false) },
                def.Port, 0, 0, null, def.Command(creds)), ct);

            foreach (var extra in networks.Skip(1))
                await docker.ConnectNetworkAsync(svc.ContainerName, extra, ct);

            svc.Status = ServiceStatus.Running;
            // What is actually running, as opposed to what was asked for. They diverge whenever a
            // moving tag is pulled again, and a database that changed major version this way will
            // not start on the data it already has.
            svc.RunningImage = image;
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

    /// <summary>The private network for this resource's environment, or null while it has none.</summary>
    private async Task<string?> ResolveEnvironmentNetworkAsync(Domain.Services.ManagedService svc, CancellationToken ct)
    {
        if (svc.EnvironmentId is not { } environmentId) return null;

        var placement = await db.Environments
            .Where(e => e.Id == environmentId)
            .Select(e => new { e.Slug, ProjectSlug = e.Project!.Slug })
            .FirstOrDefaultAsync(ct);

        return placement is null
            ? null
            : Networking.EnvironmentNetwork.For(placement.ProjectSlug, placement.Slug, environmentId);
    }

    public async Task StartAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.RestartContainerAsync(id, ct); // restart starts a stopped container
        svc.Status = ServiceStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    public async Task StopAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstAsync(s => s.Id == serviceId, ct);
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.StopContainerAsync(id, ct);
        svc.Status = ServiceStatus.Stopped;
        await db.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(Guid serviceId, bool deleteData, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return;
        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var id = await FindContainerIdAsync(docker, svc.ContainerName, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
        // With the backup engine in place, honouring deleteData is now safe: the UI warns and
        // users can back up first. Default keeps the volume.
        if (deleteData) await docker.RemoveVolumeAsync(svc.VolumeName, ct);
        db.ManagedServices.Remove(svc);
        await db.SaveChangesAsync(ct);
    }

    public async Task<string?> TestConnectionAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return "That database no longer exists.";

        if (Networking.ConnectionProbe.WhyUnsupported(svc.Type) is { } unsupported) return unsupported;

        var definition = ServiceCatalog.All[svc.Type];
        var plan = Networking.ConnectionProbe.For(svc.Type, CredsFor(svc))!;

        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var environmentNetwork = await ResolveEnvironmentNetworkAsync(svc, ct);

        // On the network a service would use, not the panel's. Testing from anywhere else would
        // answer a question nobody asked — the failure being looked for here is usually the network.
        var network = Networking.NetworkPlan.Primary(environmentNetwork, _opt.WorkspaceNetwork(wsSlug));

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var output = new System.Text.StringBuilder();
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            $"{definition.ImageRepo}:{svc.Version}", plan.Command, [],
            Env: plan.Env, NetworkMode: network),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        return exit == 0
            ? null
            : Networking.ConnectionProbe.Explain(svc.Type, Deployments.LogText.Clean(output.ToString()).Trim());
    }

    public async Task<long?> MeasureStorageAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null || string.IsNullOrWhiteSpace(svc.VolumeName)) return null;

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var output = new System.Text.StringBuilder();

        // Read-only: measuring must not be able to change what it is measuring.
        var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
            MeasuringImage, StorageMeasurement.Command, [(svc.VolumeName, "/data", true)]),
            new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

        var bytes = exit == 0 ? StorageMeasurement.Parse(output.ToString()) : null;

        // The timestamp is written even when the figure is not: "measured, and it did not work" and
        // "never measured" are different states, and the screen shows them differently.
        svc.StorageBytes = bytes;
        svc.StorageMeasuredAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);

        if (bytes is null)
            logger.LogWarning("Could not measure storage for {Name} (exit {Exit}).", svc.Name, exit);

        return bytes;
    }

    public async Task<IReadOnlyList<string>> RotatePasswordAsync(Guid serviceId, CancellationToken ct)
    {
        var svc = await db.ManagedServices.FirstOrDefaultAsync(s => s.Id == serviceId, ct);
        if (svc is null) return [];

        if (CredentialRotationPlan.WhyUnsupported(svc.Type) is { } reason)
            throw new InvalidOperationException(reason);

        var definition = ServiceCatalog.All[svc.Type];
        var current = CredsFor(svc);
        var newPassword = ServiceCredentials.Generate();
        if (!CredentialRotationPlan.IsSafeToApply(newPassword))
            throw new InvalidOperationException("The generated password could not be applied safely.");

        var docker = await engineFactory.ResolveAsync(svc.ServerId, ct);
        var wsSlug = await db.Workspaces.Where(w => w.Id == svc.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
        var network = _opt.WorkspaceNetwork(wsSlug);

        if (CredentialRotationPlan.For(svc.Type, current, newPassword) is { } plan)
        {
            var output = new System.Text.StringBuilder();
            var exit = await docker.RunOneOffAsync(new DockerOneOffRequest(
                $"{definition.ImageRepo}:{svc.Version}", plan.Command, [],
                Env: plan.Env, NetworkMode: network),
                new Deployments.InlineProgress<string>(line => { lock (output) output.AppendLine(line); }), ct);

            // Nothing is stored unless the database accepted it. The other order locks every
            // attached app out of a database whose password never actually changed.
            if (exit != 0)
                throw new InvalidOperationException(
                    "The database refused the new password, so nothing was changed. " +
                    Deployments.LogText.Clean(output.ToString()).Trim());
        }

        svc.EncryptedPassword = protector.Protect(newPassword);
        await db.SaveChangesAsync(ct);

        // Redis reads its password from its own command line, so it only takes effect on restart.
        if (CredentialRotationPlan.RequiresRecreate(svc.Type))
            await ProvisionAsync(svc.Id, ct);

        // Every app that was pointed at it is rewritten here, or the rotation simply breaks them.
        var attachEnv = definition.AttachEnv(CredsFor(svc));
        var apps = await db.Apps.Include(a => a.EnvironmentVariables)
            .Where(a => a.WorkspaceId == svc.WorkspaceId).ToListAsync(ct);

        var updated = new List<string>();
        foreach (var app in apps)
        {
            var touched = false;
            foreach (var (key, value) in attachEnv)
            {
                var variable = app.EnvironmentVariables.FirstOrDefault(v => v.Key == key);
                if (variable is null) continue;
                variable.Value = protector.Protect(value);
                variable.IsSecret = true;
                touched = true;
            }
            if (touched) updated.Add(app.Name);
        }
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Rotated the password for {Name}; {Count} service(s) updated.", svc.Name, updated.Count);
        return updated;
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

    private static async Task<string?> FindContainerIdAsync(IDockerEngine docker, string name, CancellationToken ct)
    {
        var containers = await docker.ListContainersAsync("harbora.service", ct);
        return containers.FirstOrDefault(c => c.Name == name)?.Id;
    }

    private static async Task RemoveContainerByNameAsync(IDockerEngine docker, string name, CancellationToken ct)
    {
        var id = await FindContainerIdAsync(docker, name, ct);
        if (id is not null) await docker.RemoveContainerAsync(id, force: true, ct);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
