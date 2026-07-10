using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Deployments;
using Harbora.Domain.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// The end-to-end deployment pipeline: checkout → build → run → wire proxy → health check →
/// mark active. Each stage streams+persists logs (with secrets redacted). A failure leaves the
/// previously-running container untouched so the app keeps serving traffic.
/// </summary>
public sealed class DeploymentPipeline(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    IGitService git,
    IProxyEngine proxy,
    IDeploymentLogStream stream,
    ISecretProtector protector,
    ISecretRedactor redactor,
    INotificationService notifications,
    IHttpClientFactory httpFactory,
    ISystemClock clock,
    IOptions<HarboraRuntimeOptions> options,
    ILogger<DeploymentPipeline> logger)
{
    private readonly HarboraRuntimeOptions _opt = options.Value;

    public async Task ExecuteAsync(Guid deploymentId, CancellationToken ct)
    {
        var deployment = await db.Deployments.Include(d => d.App)
            .FirstOrDefaultAsync(d => d.Id == deploymentId, ct);
        if (deployment?.App is null) return;

        var app = await db.Apps
            .Include(a => a.EnvironmentVariables)
            .Include(a => a.Volumes)
            .Include(a => a.Domains)
            .Include(a => a.GitRepository)!.ThenInclude(r => r!.Provider)
            .FirstAsync(a => a.Id == deployment.AppId, ct);

        // Resolve the container engine for this app's server (local or remote agent).
        var docker = await engineFactory.ResolveAsync(app.ServerId, ct);

        var secrets = app.EnvironmentVariables.Where(e => e.IsSecret)
            .Select(e => SafeUnprotect(e.Value)).Where(v => v.Length > 0).ToList();
        long seq = 0;

        async Task Log(LogStream s, string message)
        {
            var clean = redactor.Redact(message, secrets);
            db.DeploymentLogs.Add(new DeploymentLog
            {
                DeploymentId = deploymentId, Stream = s, Sequence = seq++,
                Message = clean, Timestamp = clock.UtcNow
            });
            await stream.PublishLogAsync(deploymentId, s, clean, ct);
        }

        async Task SetStatus(DeploymentStatus status)
        {
            deployment.Status = status;
            await stream.PublishStatusAsync(deploymentId, status, ct);
            await db.SaveChangesAsync(ct);
        }

        try
        {
            deployment.StartedAt = clock.UtcNow;
            await SetStatus(DeploymentStatus.Building);
            await Log(LogStream.System, $"Deployment #{deployment.Number} started ({app.SourceType}).");

            var imageTag = $"{_opt.ImagePrefix}/{app.Slug}:build-{deployment.Number}";
            var buildLog = new Progress<string>(l => _ = Log(LogStream.Build, l));

            imageTag = await AcquireImageAsync(docker, app, deployment, imageTag, buildLog, Log, ct);
            deployment.ImageTag = imageTag;

            await SetStatus(DeploymentStatus.Deploying);

            // Per-tenant isolation: each workspace gets its own network. Apps + their attached
            // services share it; other tenants can't reach them.
            var wsSlug = await db.Workspaces.Where(w => w.Id == app.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
            var network = _opt.WorkspaceNetwork(wsSlug);
            await docker.EnsureNetworkAsync(network, ct);
            foreach (var v in app.Volumes)
                await docker.EnsureVolumeAsync(v.Name, ct);

            var containerName = $"harbora-{app.Slug}";
            await RemoveExistingContainerAsync(docker, containerName, Log, ct);

            // Decide how the proxy reaches this app. On the local node Traefik joins the tenant
            // network and routes by container name. On a remote node there is no shared overlay, so
            // we publish the container port to a stable host port and route to the node's host:port.
            var server = await db.Servers.FirstAsync(s => s.Id == app.ServerId, ct);
            int? publishPort = null;
            string upstreamHost = containerName;
            var upstreamPort = app.ContainerPort;
            if (!server.IsLocal)
            {
                app.PublishedHostPort ??= AllocateHostPort(app.Slug);
                publishPort = app.PublishedHostPort;
                upstreamHost = server.Hostname;
                upstreamPort = app.PublishedHostPort!.Value;
            }
            else
            {
                // Give the local Traefik ingress into this tenant's network, and the panel reach
                // for HTTP health checks by container name (both idempotent, best-effort).
                await docker.ConnectNetworkAsync(_opt.ProxyContainerName, network, ct);
                await docker.ConnectNetworkAsync(_opt.PanelContainerName, network, ct);
            }

            var env = BuildEnv(app);
            var labels = new Dictionary<string, string>
            {
                ["harbora.managed"] = "true",
                ["harbora.app"] = app.Slug,
                ["harbora.deployment"] = deployment.Number.ToString()
            };

            await Log(LogStream.System, $"Starting container {containerName} …");
            var containerId = await docker.RunContainerAsync(new DockerRunRequest(
                imageTag, containerName, network, env, labels,
                app.Volumes.Select(v => (v.Name, v.MountPath, v.ReadOnly)).ToList(),
                app.ContainerPort, app.MemoryLimitBytes, app.CpuLimit, app.HealthCheckPath,
                Command: null, PublishToHostPort: publishPort), ct);

            await Log(LogStream.System, $"Container {containerId[..12]} is up. Verifying health …");
            var healthy = await WaitForHealthyAsync(docker, upstreamHost, upstreamPort, containerName, app.HealthCheckPath,
                msg => Log(LogStream.System, msg), ct);
            if (!healthy)
                throw new InvalidOperationException("Container failed its health check.");

            await WireProxyAsync(app, upstreamHost, upstreamPort, Log, ct);

            deployment.Status = DeploymentStatus.Succeeded;
            deployment.FinishedAt = clock.UtcNow;
            app.ActiveDeploymentId = deployment.Id;
            app.Status = AppStatus.Running;
            await db.SaveChangesAsync(ct);
            await stream.PublishStatusAsync(deploymentId, DeploymentStatus.Succeeded, ct);
            await Log(LogStream.System, $"✅ Deployment #{deployment.Number} succeeded.");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {Id} failed.", deploymentId);
            deployment.Status = DeploymentStatus.Failed;
            deployment.FinishedAt = clock.UtcNow;
            deployment.ErrorMessage = ex.Message;
            app.Status = app.ActiveDeploymentId is null ? AppStatus.Failed : AppStatus.Running;
            await db.SaveChangesAsync(ct);
            await stream.PublishStatusAsync(deploymentId, DeploymentStatus.Failed, ct);
            await Log(LogStream.System, $"❌ Deployment failed: {redactor.Redact(ex.Message, secrets)}");
            await notifications.NotifyAsync(app.WorkspaceId, AlertEvent.DeployFailed, AlertSeverity.Critical,
                $"Deploy failed: {app.Name} #{deployment.Number}", redactor.Redact(ex.Message, secrets), ct);
        }
    }

    private async Task<string> AcquireImageAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        switch (app.SourceType)
        {
            case AppSourceType.PrebuiltImage:
                if (string.IsNullOrWhiteSpace(app.PrebuiltImage))
                    throw new InvalidOperationException("No image configured.");
                await log(LogStream.System, $"Pulling image {app.PrebuiltImage} …");
                await docker.PullImageAsync(app.PrebuiltImage, buildLog, ct);
                return app.PrebuiltImage;

            case AppSourceType.GitRepository:
            case AppSourceType.Dockerfile:
            {
                if (app.GitRepository is null)
                    throw new InvalidOperationException("No Git repository linked.");

                var token = app.GitRepository.Provider?.EncryptedCredential is { } enc && enc.Length > 0
                    ? SafeUnprotect(enc) : null;
                var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
                var gitRef = deployment.GitRef ?? app.GitRepository.DefaultBranch;

                await log(LogStream.System, $"Checking out {app.GitRepository.FullName}@{gitRef} …");
                var checkout = await git.CheckoutAsync(
                    app.GitRepository.CloneUrl, gitRef, token, workDir, buildLog, ct);

                deployment.CommitSha = checkout.CommitSha;
                deployment.CommitMessage = checkout.CommitMessage;
                deployment.CommitAuthor = checkout.CommitAuthor;

                var contextPath = Path.Combine(checkout.LocalPath, app.BuildContextPath?.TrimStart('.', '/', '\\') ?? "");
                if (!Directory.Exists(contextPath)) contextPath = checkout.LocalPath;

                // Use the repo's Dockerfile if present; otherwise auto-detect the stack (buildpack).
                var dockerfile = app.DockerfilePath ?? "Dockerfile";
                if (!File.Exists(Path.Combine(contextPath, dockerfile)))
                {
                    var pack = Buildpacks.Detect(contextPath, app.ContainerPort);
                    if (pack is null)
                        throw new InvalidOperationException(
                            "No Dockerfile found and the stack couldn't be auto-detected. Add a Dockerfile, or deploy a prebuilt image / template.");

                    dockerfile = "Dockerfile.harbora";
                    await File.WriteAllTextAsync(Path.Combine(contextPath, dockerfile), pack.Value.Dockerfile, ct);
                    await log(LogStream.System, $"No Dockerfile — auto-detected {pack.Value.Stack}; using a generated build.");
                }

                await log(LogStream.System, $"Building image {imageTag} …");
                var buildArgs = app.EnvironmentVariables
                    .Where(e => e.AvailableAtBuild)
                    .ToDictionary(e => e.Key, e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

                return await docker.BuildImageAsync(
                    new DockerBuildRequest(contextPath, dockerfile, imageTag, buildArgs),
                    buildLog, ct);
            }

            default:
                throw new NotSupportedException($"Source type {app.SourceType} is not yet supported by the build engine.");
        }
    }

    private Dictionary<string, string> BuildEnv(App app) =>
        app.EnvironmentVariables.ToDictionary(
            e => e.Key,
            e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

    private async Task RemoveExistingContainerAsync(IDockerEngine docker, string containerName, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var existing = await docker.ListContainersAsync($"harbora.app", ct);
        var match = existing.FirstOrDefault(c => c.Name == containerName);
        if (match is null) return;
        await log(LogStream.System, $"Replacing previous container {match.Id[..12]} …");
        await docker.RemoveContainerAsync(match.Id, force: true, ct);
    }

    /// <summary>
    /// Health gate: first wait for the container to reach "running" (fail fast if it exits). Then,
    /// for a local-server app with a health path, HTTP-probe it over the shared harbora network
    /// until it returns a success status. Remote nodes fall back to liveness (the panel can't reach
    /// their containers by name without an overlay network).
    /// </summary>
    private async Task<bool> WaitForHealthyAsync(
        IDockerEngine docker, string upstreamHost, int upstreamPort, string containerName, string? healthPath,
        Func<string, Task> log, CancellationToken ct)
    {
        var running = false;
        for (var i = 0; i < 8 && !running; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
            var c = (await docker.ListContainersAsync("harbora.app", ct)).FirstOrDefault(x => x.Name == containerName);
            if (c is null) return false;
            if (c.State.Equals("exited", StringComparison.OrdinalIgnoreCase)) return false;
            running = c.State.Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        if (!running) return false;

        if (string.IsNullOrWhiteSpace(healthPath))
            return true;

        // Probe the same address the proxy will use: container name on the local network, or the
        // node's host:publishedPort for a remote node.
        var url = $"http://{upstreamHost}:{upstreamPort}/{healthPath.TrimStart('/')}";
        await log($"HTTP health check → {url}");
        var client = httpFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var res = await client.GetAsync(url, ct);
                if ((int)res.StatusCode < 400) return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // App still booting / not accepting connections yet — keep trying.
            }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        await log("Health check did not pass within the timeout.");
        return false;
    }

    /// <summary>Materialise a Route per domain then re-apply the whole workspace's proxy config.</summary>
    private async Task WireProxyAsync(App app, string upstreamHost, int upstreamPort, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (app.Domains.Count == 0)
        {
            await log(LogStream.System, "No domains attached; skipping proxy wiring.");
            return;
        }

        foreach (var domain in app.Domains)
        {
            var route = await db.Routes.FirstOrDefaultAsync(r => r.AppId == app.Id && r.Host == domain.Host, ct);
            if (route is null)
            {
                route = new Route { WorkspaceId = app.WorkspaceId, AppId = app.Id, Host = domain.Host };
                db.Routes.Add(route);
            }
            route.TargetService = upstreamHost;
            route.TargetPort = upstreamPort;
            route.SslEnabled = domain.SslEnabled;
            route.RedirectHttpToHttps = domain.ForceHttps;
            route.IsEnabled = true;
        }
        await db.SaveChangesAsync(ct);

        var routes = await db.Routes.Where(r => r.WorkspaceId == app.WorkspaceId && r.IsEnabled).ToListAsync(ct);
        var result = await proxy.ApplyAsync(routes, ct);
        await log(LogStream.System, result.Success
            ? "Proxy configuration applied."
            : $"⚠ Proxy apply failed{(result.RolledBack ? " (rolled back)" : "")}: {result.Error}");
    }

    /// <summary>Deterministic host port (20000–29999) for a remote app's published container port.</summary>
    private static int AllocateHostPort(string slug)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(slug));
        var value = BitConverter.ToUInt32(hash, 0);
        return 20000 + (int)(value % 10000);
    }

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
