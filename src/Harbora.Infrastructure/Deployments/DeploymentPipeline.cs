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

        // Build/pull progress arrives on the container engine's own threads (IProgress dispatches
        // via the thread pool — there is no SynchronizationContext here). A DbContext is NOT
        // thread-safe, so those lines are queued and persisted on this thread instead; writing them
        // directly would mutate the change tracker mid-SaveChangesAsync. Live streaming is
        // unaffected — it happens immediately, off-thread, and never touches the DbContext.
        var pendingEngineLogs = new System.Collections.Concurrent.ConcurrentQueue<(LogStream Stream, string Message)>();

        void DrainEngineLogs()
        {
            while (pendingEngineLogs.TryDequeue(out var pending))
                db.DeploymentLogs.Add(new DeploymentLog
                {
                    DeploymentId = deploymentId, Stream = pending.Stream, Sequence = seq++,
                    Message = pending.Message, Timestamp = clock.UtcNow
                });
        }

        // Safe to call from any thread.
        async Task LogFromEngine(LogStream s, string message)
        {
            var clean = redactor.Redact(message, secrets);
            pendingEngineLogs.Enqueue((s, clean));
            await stream.PublishLogAsync(deploymentId, s, clean, ct);
        }

        // Pipeline-thread logging only.
        async Task Log(LogStream s, string message)
        {
            var clean = redactor.Redact(message, secrets);
            DrainEngineLogs();
            db.DeploymentLogs.Add(new DeploymentLog
            {
                DeploymentId = deploymentId, Stream = s, Sequence = seq++,
                Message = clean, Timestamp = clock.UtcNow
            });
            await stream.PublishLogAsync(deploymentId, s, clean, ct);
        }

        // All status changes go through the state machine (ADR-004): illegal transitions throw,
        // timestamps are stamped consistently, and the row is persisted + streamed on every change.
        async Task SetStatus(DeploymentStatus status)
        {
            DeploymentStateMachine.Transition(deployment, status, clock.UtcNow);
            await stream.PublishStatusAsync(deploymentId, status, ct);
            DrainEngineLogs();
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await SetStatus(DeploymentStatus.Building);
            await Log(LogStream.System, $"Deployment #{deployment.Number} started ({app.SourceType}).");

            // A Compose app is resolved up front: the file is parsed and validated BEFORE anything is
            // built or started, so an unsupported directive is a plain rejection rather than a
            // half-deployed stack.
            var stackBuildLog = new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l));
            ComposeParseResult? composeStack = null;
            if (app.SourceType == AppSourceType.DockerCompose)
                composeStack = await LoadComposeAsync(app, deployment, stackBuildLog, Log, ct);

            IReadOnlyCollection<string> keepContainers = [];
            string imageTag;
            // An image chosen at deploy time (`harbora deploy --image`) is released as-is: there is
            // no source to fetch and nothing to build, so pull it and go straight to the cutover.
            if (deployment.RolledBackFromId is null && !string.IsNullOrWhiteSpace(deployment.ImageTag))
            {
                imageTag = deployment.ImageTag!;
                await Log(LogStream.System, $"Releasing image {imageTag} (nothing to build).");
                await docker.PullImageAsync(imageTag, new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l)), ct);
            }
            else if (deployment.RolledBackFromId is { } rollbackTargetId)
            {
                // Rollback = re-release a prior artifact. Never rebuild (instant + exact; ADR-006).
                var target = await db.Deployments.FirstOrDefaultAsync(d => d.Id == rollbackTargetId, ct);
                imageTag = DeploymentPlanning.ResolveRollbackImage(target);

                // The artifact must still be on the node. Checking here — before anything is started
                // or changed — turns a confusing mid-deploy failure into a clear "can't do that yet".
                if (!await docker.ImageExistsAsync(imageTag, ct))
                    throw new InvalidOperationException(
                        $"The image for deployment #{target!.Number} ({imageTag}) is no longer on this server, " +
                        "so it cannot be re-released. It was most likely pruned by image retention — " +
                        "deploy that commit from source instead.");

                await Log(LogStream.System,
                    $"Rolling back to deployment #{target!.Number}; re-releasing image {imageTag} (no rebuild).");
            }
            else
            {
                imageTag = $"{_opt.ImagePrefix}/{app.Slug}:build-{deployment.Number}";
                var buildLog = new Progress<string>(l => _ = LogFromEngine(LogStream.Build, l));
                // A Compose stack has no single image; its services are built or pulled by the
                // deployer below. The recorded tag names the stack so history still reads sensibly.
                imageTag = composeStack is null
                    ? await AcquireImageAsync(docker, app, deployment, imageTag, buildLog, Log, ct)
                    : $"{_opt.ImagePrefix}/{app.Slug}:compose-{deployment.Number}";
            }
            deployment.ImageTag = imageTag;

            await SetStatus(DeploymentStatus.Deploying);

            // Per-tenant isolation: each workspace gets its own network. Apps + their attached
            // services share it; other tenants can't reach them.
            var wsSlug = await db.Workspaces.Where(w => w.Id == app.WorkspaceId).Select(w => w.Slug).FirstAsync(ct);
            var network = _opt.WorkspaceNetwork(wsSlug);
            await docker.EnsureNetworkAsync(network, ct);
            foreach (var v in app.Volumes)
                await docker.EnsureVolumeAsync(v.Name, ct);

            // Zero-downtime cutover (ADR-007): the new container gets a versioned name and starts
            // ALONGSIDE the currently-serving one. We only retire the old container(s) AFTER the new
            // one is healthy and traffic has been switched — so a failed deploy never drops traffic.
            var containerName = DeploymentPlanning.ContainerName(app.Slug, deployment.Number);

            // Decide how the proxy reaches this app. On the local node Traefik joins the tenant
            // network and routes by container name. On a remote node there is no shared overlay, so
            // we publish the container port to a per-deployment host port (lets old + new coexist).
            var server = await db.Servers.FirstAsync(s => s.Id == app.ServerId, ct);
            int? publishPort = null;
            string upstreamHost = containerName;
            var upstreamPort = app.ContainerPort;
            if (!server.IsLocal)
            {
                publishPort = DeploymentPlanning.HostPort(app.Slug, deployment.Number);
                upstreamHost = server.Hostname;
                upstreamPort = publishPort.Value;
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

            // A Compose stack starts several containers instead of one, so it takes over from here
            // and returns the name + port the proxy should target. The guarantees are the same:
            // everything new starts alongside the old stack, health is checked before any cutover.
            if (composeStack is not null)
            {
                var web = await StartComposeStackAsync(
                    docker, app, deployment, composeStack, network, labels, server, stackBuildLog, Log, ct);
                containerName = web.ContainerName;
                upstreamHost = server.IsLocal ? web.ContainerName : server.Hostname;
                upstreamPort = server.IsLocal ? web.Port : publishPort ?? web.Port;
                keepContainers = web.AllContainerNames;

                await SetStatus(DeploymentStatus.HealthChecking);
                var stackHealth = await WaitForHealthyAsync(docker, upstreamHost, upstreamPort,
                    web.ContainerName, app.HealthCheckPath, msg => Log(LogStream.System, msg), ct);
                if (!stackHealth.IsHealthy)
                    throw new InvalidOperationException(
                        $"The '{web.ServiceName}' service did not become healthy. " +
                        HealthDiagnosis.Explain(stackHealth, web.ContainerName));

                await WireProxyAsync(app, upstreamHost, upstreamPort, Log, ct);
                await RetireOldContainersAsync(docker, app.Slug, keepContainers, Log, ct);

                if (deployment.RolledBackFromId is not null)
                    await MarkSupersededAsRolledBackAsync(app.ActiveDeploymentId, deployment.Id, Log, ct);

                app.ActiveDeploymentId = deployment.Id;
                app.Status = AppStatus.Running;
                await SetStatus(DeploymentStatus.Succeeded);
                await Log(LogStream.System,
                    $"✅ Deployment #{deployment.Number} succeeded ({composeStack.Services.Count} services).");
                await PruneOldImagesAsync(docker, app, Log, ct);
                return;
            }

            await Log(LogStream.System, $"Starting container {containerName} …");
            var containerId = await docker.RunContainerAsync(new DockerRunRequest(
                imageTag, containerName, network, env, labels,
                app.Volumes.Select(v => (v.Name, v.MountPath, v.ReadOnly)).ToList(),
                app.ContainerPort, app.MemoryLimitBytes, app.CpuLimit, app.HealthCheckPath,
                Command: null, PublishToHostPort: publishPort), ct);

            await Log(LogStream.System, $"Container {containerId[..12]} is up. Verifying health …");
            await SetStatus(DeploymentStatus.HealthChecking);
            var health = await WaitForHealthyAsync(docker, upstreamHost, upstreamPort, containerName, app.HealthCheckPath,
                msg => Log(LogStream.System, msg), ct);
            if (!health.IsHealthy)
                throw new InvalidOperationException(HealthDiagnosis.Explain(health, containerName));

            // New container is healthy → switch traffic to it, THEN retire the old container(s).
            await WireProxyAsync(app, upstreamHost, upstreamPort, Log, ct);
            await RetireOldContainersAsync(docker, app.Slug, keepContainerName: containerName, Log, ct);
            if (!server.IsLocal) app.PublishedHostPort = publishPort;

            // A rollback supersedes whatever was live: mark that deployment RolledBack so the
            // history shows which version was abandoned, not just which one replaced it.
            if (deployment.RolledBackFromId is not null)
                await MarkSupersededAsRolledBackAsync(app.ActiveDeploymentId, deployment.Id, Log, ct);

            app.ActiveDeploymentId = deployment.Id;
            app.Status = AppStatus.Running;
            await SetStatus(DeploymentStatus.Succeeded);
            await Log(LogStream.System, $"✅ Deployment #{deployment.Number} succeeded.");

            // Only after the deployment is recorded as succeeded — pruning is housekeeping and must
            // never be able to turn a live, working deployment into a failure.
            await PruneOldImagesAsync(docker, app, Log, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {Id} failed.", deploymentId);
            // Zero-downtime guarantee: remove only the just-started (failed) container; the previous
            // version — if any — keeps serving untouched.
            await TryRemoveContainerByNameAsync(docker, DeploymentPlanning.ContainerName(app.Slug, deployment.Number), ct);
            // Failed is reachable from any in-flight state; guard against a double-terminal write.
            if (DeploymentStateMachine.IsInFlight(deployment.Status))
                DeploymentStateMachine.Transition(deployment, DeploymentStatus.Failed, clock.UtcNow);
            deployment.ErrorMessage = ex.Message;
            app.Status = app.ActiveDeploymentId is null ? AppStatus.Failed : AppStatus.Running;
            DrainEngineLogs();
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
        // A pushed archive always wins, whatever the app's configured source is: the user just sent
        // this exact code and expects that deployed, not whatever a Git remote happens to hold.
        if (!string.IsNullOrWhiteSpace(deployment.SourceArchivePath))
            return await BuildFromUploadAsync(docker, app, deployment, imageTag, buildLog, log,
                                              forceStatic: app.SourceType == AppSourceType.StaticSite, ct);

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
                return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

            case AppSourceType.StaticSite:
                // A repo of static files served by Nginx — always use the generated static build,
                // ignoring any Dockerfile / stack detection.
                return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: true, ct);

            case AppSourceType.Template:
            {
                var template = app.TemplateId is { } tid
                    ? await db.AppTemplates.FirstOrDefaultAsync(t => t.Id == tid, ct)
                    : null;
                if (template is null)
                    throw new InvalidOperationException("This app references a template that no longer exists.");

                var spec = TemplateResolver.Resolve(template.ManifestJson);
                switch (spec.Kind)
                {
                    case TemplateResolver.TemplateKind.Image:
                        await log(LogStream.System, $"Template '{template.Name}': pulling image {spec.Image} …");
                        await docker.PullImageAsync(spec.Image!, buildLog, ct);
                        return spec.Image!;

                    case TemplateResolver.TemplateKind.Git:
                        if (app.GitRepository is null)
                            throw new InvalidOperationException(
                                $"Template '{template.Name}' deploys from a Git repository — add a repository URL to the app, then redeploy.");
                        return await BuildFromGitAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

                    case TemplateResolver.TemplateKind.ManagedService:
                        throw new InvalidOperationException(
                            $"'{template.Name}' provisions a managed database/cache — create it from the Databases page, not as an app.");

                    default:
                        throw new InvalidOperationException(spec.Reason ?? "This template isn't one-click deployable yet.");
                }
            }

            case AppSourceType.Upload:
                // The app is upload-only and nothing was pushed — BuildFromUploadAsync explains how.
                return await BuildFromUploadAsync(docker, app, deployment, imageTag, buildLog, log, forceStatic: false, ct);

            case AppSourceType.DockerCompose:
                // Multi-service Compose orchestration is planned (see docs/overhaul/12 P7+) but not
                // yet implemented; fail with a clear message rather than a raw NotSupported.
                throw new InvalidOperationException(
                    "Docker Compose deployments aren't supported yet. Deploy from a Git repo, Dockerfile, " +
                    "prebuilt image, static site or template for now.");

            default:
                throw new NotSupportedException($"Source type {app.SourceType} is not supported.");
        }
    }

    /// <summary>
    /// Checkout + build for Git/Dockerfile/StaticSite/Git-templates. When <paramref name="forceStatic"/>
    /// is set, always generate an Nginx static build regardless of any Dockerfile or detected stack.
    /// </summary>
    private async Task<string> BuildFromGitAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        if (app.GitRepository is null)
            throw new InvalidOperationException("No Git repository linked.");

        var token = app.GitRepository.Provider?.EncryptedCredential is { } enc && enc.Length > 0
            ? SafeUnprotect(enc) : null;
        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
        var gitRef = deployment.GitRef ?? app.GitRepository.DefaultBranch;

        await log(LogStream.System, $"Checking out {app.GitRepository.FullName}@{gitRef} …");
        var checkout = await git.CheckoutAsync(app.GitRepository.CloneUrl, gitRef, token, workDir, buildLog, ct);

        deployment.CommitSha = checkout.CommitSha;
        deployment.CommitMessage = checkout.CommitMessage;
        deployment.CommitAuthor = checkout.CommitAuthor;

        return await BuildFromSourceAsync(docker, app, deployment, imageTag, checkout.LocalPath,
                                          buildLog, log, forceStatic, ct);
    }

    /// <summary>
    /// Materialises the source, reads its compose file and validates it. Runs before anything is
    /// built or started so an unsupported directive rejects the deployment cleanly instead of
    /// leaving half a stack running.
    /// </summary>
    private async Task<ComposeParseResult> LoadComposeAsync(
        App app, Deployment deployment, IProgress<string> buildLog,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var sourceRoot = await MaterialiseSourceAsync(app, deployment, buildLog, log, ct);
        var candidates = string.IsNullOrWhiteSpace(app.ComposeFilePath)
            ? new[] { "docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml" }
            : [app.ComposeFilePath!];

        var path = candidates
            .Select(c => Path.Combine(sourceRoot, c))
            .FirstOrDefault(File.Exists);

        if (path is null)
            throw new InvalidOperationException(
                $"No compose file found in the source (looked for {string.Join(", ", candidates)}).");

        await log(LogStream.System, $"Reading {Path.GetFileName(path)} …");
        var stack = ComposeFile.Parse(await File.ReadAllTextAsync(path, ct));

        // Surface warnings before deciding: the operator should see what was ignored even on success.
        foreach (var warning in stack.Warnings)
            await log(LogStream.System, $"⚠ {warning}");

        if (!stack.IsValid)
        {
            foreach (var error in stack.Errors)
                await log(LogStream.System, $"✗ {error}");
            throw new InvalidOperationException(
                "The compose file uses directives Harbora can't run safely: " + string.Join(" ", stack.Errors));
        }

        // Where each service's build context lives, resolved once.
        foreach (var service in stack.Services)
            if (service.Build is not null)
                service.Build = Path.Combine(sourceRoot, service.Build.TrimStart('.', '/', '\\'));

        await log(LogStream.System,
            $"Stack: {string.Join(", ", stack.Services.Select(s => s.Name))} " +
            $"(web = {stack.Web!.Name}:{stack.Web.Port}).");
        return stack;
    }

    /// <summary>
    /// Builds/pulls every service and starts the whole stack under versioned names, alongside
    /// whatever is currently running. Returns the service the proxy should target.
    /// </summary>
    private async Task<(string ServiceName, string ContainerName, int Port, IReadOnlyCollection<string> AllContainerNames)>
        StartComposeStackAsync(
            IDockerEngine docker, App app, Deployment deployment, ComposeParseResult stack,
            string network, Dictionary<string, string> labels, Domain.Servers.Server server,
            IProgress<string> buildLog, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var started = new List<string>();

        // Dependencies first, so a service that needs its database finds it already running. Not a
        // full topological sort: compose's depends_on doesn't wait for readiness either, and the
        // health gate below is what actually decides whether the stack works.
        foreach (var service in stack.Services.OrderByDescending(s => s.DependsOn.Count == 0))
        {
            var containerName = DeploymentPlanning.ComposeContainerName(app.Slug, service.Name, deployment.Number);

            string image;
            if (service.Build is not null)
            {
                image = $"{_opt.ImagePrefix}/{app.Slug}-{service.Name}:build-{deployment.Number}";
                var dockerfile = service.Dockerfile ?? "Dockerfile";
                if (!File.Exists(Path.Combine(service.Build, dockerfile)))
                {
                    var pack = Buildpacks.Detect(service.Build, service.Port ?? app.ContainerPort);
                    if (pack is null)
                        throw new InvalidOperationException(
                            $"Service '{service.Name}' builds from '{service.Build}' but has no Dockerfile " +
                            "and no recognisable stack.");
                    dockerfile = "Dockerfile.harbora";
                    await File.WriteAllTextAsync(Path.Combine(service.Build, dockerfile), pack.Value.Dockerfile, ct);
                    await log(LogStream.System, $"Service '{service.Name}': detected {pack.Value.Stack}.");
                }

                await log(LogStream.System, $"Building {service.Name} → {image} …");
                image = await docker.BuildImageAsync(
                    new DockerBuildRequest(service.Build, dockerfile, image, new Dictionary<string, string>()),
                    buildLog, ct);
            }
            else
            {
                image = service.Image!;
                await log(LogStream.System, $"Pulling {image} for '{service.Name}' …");
                await docker.PullImageAsync(image, buildLog, ct);
            }

            foreach (var (volume, _) in service.Volumes)
                await docker.EnsureVolumeAsync(VolumeNameFor(app, volume), ct);

            // The app's own env vars apply to every service, with the compose file's values winning
            // for that service — the file is the more specific statement.
            var env = BuildEnv(app);
            foreach (var (key, value) in service.Environment) env[key] = value;

            env.TryAdd("HARBORA_SERVICE", service.Name);

            // Under compose, services reach each other by service name. Two deployments of the same
            // app coexist during a cutover, so the alias is scoped per deployment as well: the bare
            // name resolves within a stack, and the versioned one is unambiguous across stacks.
            var aliases = new List<string> { service.Name, $"{service.Name}-{deployment.Number}" };

            // Also export SERVICE_HOST for each sibling, for stacks that prefer configuration over
            // DNS conventions.
            foreach (var sibling in stack.Services)
                env.TryAdd($"{Sanitize(sibling.Name).ToUpperInvariant()}_HOST", sibling.Name);

            var serviceLabels = new Dictionary<string, string>(labels) { ["harbora.compose.service"] = service.Name };

            // Only the web service needs a published host port, and only on a remote node.
            int? publish = service.IsWeb && !server.IsLocal
                ? DeploymentPlanning.HostPort(app.Slug, deployment.Number)
                : null;

            await log(LogStream.System, $"Starting {containerName} …");
            await docker.RunContainerAsync(new DockerRunRequest(
                image, containerName, network, env, serviceLabels,
                service.Volumes.Select(v => (VolumeNameFor(app, v.Volume), v.MountPath, false)).ToList(),
                service.Port, app.MemoryLimitBytes, app.CpuLimit,
                service.IsWeb ? app.HealthCheckPath : null,
                Command: service.Command.Count > 0 ? service.Command : null,
                PublishToHostPort: publish,
                NetworkAliases: aliases), ct);

            started.Add(containerName);

            // Aliases would be the proper way for services to resolve each other by bare name; until
            // the engine exposes them, the versioned name is what works and is what we route to.
            if (service.IsWeb)
                await log(LogStream.System, $"'{service.Name}' will receive inbound traffic.");
        }

        var web = stack.Web!;
        return (web.Name,
                DeploymentPlanning.ComposeContainerName(app.Slug, web.Name, deployment.Number),
                web.Port ?? app.ContainerPort,
                started);
    }

    /// <summary>
    /// Compose volume names are namespaced per app, so two tenants both using "pgdata" don't share
    /// one volume.
    /// </summary>
    private static string VolumeNameFor(App app, string composeVolume) => $"harbora-{app.Slug}-{composeVolume}";

    /// <summary>Service names become env var names, so anything not alphanumeric becomes '_'.</summary>
    private static string Sanitize(string name) =>
        new(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>
    /// Gets the source on disk for whichever way this app supplies it, without building anything.
    /// Shared by the compose path, which has to read a file from the tree before it can plan.
    /// </summary>
    private async Task<string> MaterialiseSourceAsync(
        App app, Deployment deployment, IProgress<string> buildLog,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());

        if (!string.IsNullOrWhiteSpace(deployment.SourceArchivePath))
        {
            if (!File.Exists(deployment.SourceArchivePath))
                throw new InvalidOperationException("The uploaded source archive is missing.");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);

            await log(LogStream.System, "Unpacking uploaded source …");
            await using var stream = File.OpenRead(deployment.SourceArchivePath);
            await SourceArchive.ExtractAsync(stream, workDir, ct);
            try { File.Delete(deployment.SourceArchivePath); } catch { /* best effort */ }
            return workDir;
        }

        if (app.GitRepository is null)
            throw new InvalidOperationException(
                "This app has no source: link a Git repository, or push one with `harbora deploy`.");

        var token = app.GitRepository.Provider?.EncryptedCredential is { } enc && enc.Length > 0
            ? SafeUnprotect(enc) : null;
        var gitRef = deployment.GitRef ?? app.GitRepository.DefaultBranch;

        await log(LogStream.System, $"Checking out {app.GitRepository.FullName}@{gitRef} …");
        var checkout = await git.CheckoutAsync(app.GitRepository.CloneUrl, gitRef, token, workDir, buildLog, ct);

        deployment.CommitSha = checkout.CommitSha;
        deployment.CommitMessage = checkout.CommitMessage;
        deployment.CommitAuthor = checkout.CommitAuthor;
        return checkout.LocalPath;
    }

    /// <summary>
    /// Unpacks the archive pushed by <c>harbora deploy</c> and builds from it. Same build rules as a
    /// Git checkout — the only difference is how the source got here.
    /// </summary>
    private async Task<string> BuildFromUploadAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        var archive = deployment.SourceArchivePath;
        if (string.IsNullOrWhiteSpace(archive) || !File.Exists(archive))
            throw new InvalidOperationException(
                "This app deploys from code pushed with `harbora deploy`, but no source archive was " +
                "uploaded for this deployment. Run `harbora deploy` from your project folder.");

        var workDir = Path.Combine(_opt.WorkDir, app.Slug, deployment.Number.ToString());
        if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);

        await log(LogStream.System, "Unpacking uploaded source …");
        await using (var stream = File.OpenRead(archive))
        {
            var result = await SourceArchive.ExtractAsync(stream, workDir, ct);
            await log(LogStream.System,
                $"Unpacked {result.Files} entries ({result.Bytes / 1024 / 1024.0:0.#} MB).");
        }

        // The upload is consumed; keeping it would double the disk cost of every deployment.
        try { File.Delete(archive); } catch { /* best effort */ }

        return await BuildFromSourceAsync(docker, app, deployment, imageTag, workDir,
                                          buildLog, log, forceStatic, ct);
    }

    /// <summary>
    /// Everything after the source exists on disk: pick the Dockerfile (or generate one from a
    /// detected stack) and build the image. Shared by the Git and upload paths so both get identical
    /// build behaviour.
    /// </summary>
    private async Task<string> BuildFromSourceAsync(
        IDockerEngine docker, App app, Deployment deployment, string imageTag, string sourceRoot,
        IProgress<string> buildLog, Func<LogStream, string, Task> log, bool forceStatic, CancellationToken ct)
    {
        var contextPath = Path.Combine(sourceRoot, app.BuildContextPath?.TrimStart('.', '/', '\\') ?? "");
        if (!Directory.Exists(contextPath)) contextPath = sourceRoot;

        string dockerfile;
        if (forceStatic)
        {
            dockerfile = "Dockerfile.harbora";
            await File.WriteAllTextAsync(Path.Combine(contextPath, dockerfile), Buildpacks.ForStaticSite().Dockerfile, ct);
            await log(LogStream.System, "Static site — using a generated Nginx build.");
        }
        else
        {
            // Use the repo's Dockerfile if present; otherwise auto-detect the stack (buildpack).
            dockerfile = app.DockerfilePath ?? "Dockerfile";
            if (!File.Exists(Path.Combine(contextPath, dockerfile)) &&
                File.Exists(Path.Combine(contextPath, "Dockerfile.harbora")))
            {
                // An inline Dockerfile the client supplied (harbora.yml `dockerfileLines`). It is an
                // explicit instruction, so it outranks stack detection.
                dockerfile = "Dockerfile.harbora";
                await log(LogStream.System, "Using the Dockerfile supplied in harbora.yml.");
            }
            else if (!File.Exists(Path.Combine(contextPath, dockerfile)))
            {
                var pack = Buildpacks.Detect(contextPath, app.ContainerPort);
                if (pack is null)
                    throw new InvalidOperationException(
                        "No Dockerfile found and the stack couldn't be auto-detected. Add a Dockerfile, or deploy a prebuilt image / template.");

                dockerfile = "Dockerfile.harbora";
                await File.WriteAllTextAsync(Path.Combine(contextPath, dockerfile), pack.Value.Dockerfile, ct);
                await log(LogStream.System, $"No Dockerfile — auto-detected {pack.Value.Stack}; using a generated build.");
            }
        }

        await log(LogStream.System, $"Building image {imageTag} …");
        var buildArgs = app.EnvironmentVariables
            .Where(e => e.AvailableAtBuild)
            .ToDictionary(e => e.Key, e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

        return await docker.BuildImageAsync(
            new DockerBuildRequest(contextPath, dockerfile, imageTag, buildArgs), buildLog, ct);
    }

    private Dictionary<string, string> BuildEnv(App app) =>
        app.EnvironmentVariables.ToDictionary(
            e => e.Key,
            e => e.IsSecret ? SafeUnprotect(e.Value) : e.Value);

    /// <summary>
    /// After a rollback cuts over, flag the deployment it displaced as <see cref="DeploymentStatus.RolledBack"/>.
    /// Only a Succeeded deployment can be superseded this way, and never the rollback itself.
    /// </summary>
    private async Task MarkSupersededAsRolledBackAsync(
        Guid? supersededId, Guid currentDeploymentId, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (supersededId is not { } id) return;

        var superseded = await db.Deployments.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (superseded is null) return;
        if (!DeploymentPlanning.ShouldMarkRolledBack(superseded, currentDeploymentId)) return;

        DeploymentStateMachine.Transition(superseded, DeploymentStatus.RolledBack, clock.UtcNow);
        await log(LogStream.System, $"Deployment #{superseded.Number} marked rolled back.");
    }

    /// <summary>
    /// Delete this app's superseded build images, keeping the active one and the newest
    /// <see cref="HarboraRuntimeOptions.ImageRetentionCount"/> rollback targets.
    ///
    /// Without this, every deploy leaves an image on disk forever. With it, retention becomes an
    /// explicit promise: rollback reaches exactly as far back as the retained images, and the depth
    /// is configurable rather than "however long until someone runs docker image prune".
    /// Entirely best-effort — a failure here is logged and never touches the deployment's outcome.
    /// </summary>
    private async Task PruneOldImagesAsync(
        IDockerEngine docker, App app, Func<LogStream, string, Task> log, CancellationToken ct)
    {
        if (_opt.ImageRetentionCount <= 0) return;

        try
        {
            var prefix = DeploymentPlanning.BuildImagePrefix(_opt.ImagePrefix, app.Slug);
            var onNode = await docker.ListImagesAsync(prefix, ct);
            var history = await db.Deployments.Where(d => d.AppId == app.Id).ToListAsync(ct);

            var prunable = DeploymentPlanning.ImagesToPrune(
                onNode, history, app.ActiveDeploymentId, _opt.ImagePrefix, app.Slug, _opt.ImageRetentionCount);
            if (prunable.Count == 0) return;

            foreach (var tag in prunable)
                await docker.RemoveImageAsync(tag, ct);

            await log(LogStream.System,
                $"Retention: removed {prunable.Count} superseded image(s); keeping the newest " +
                $"{_opt.ImageRetentionCount} for rollback.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image retention failed for app {Slug}.", app.Slug);
            await log(LogStream.System, $"(image cleanup skipped: {ex.Message})");
        }
    }

    /// <summary>
    /// Retire this app's previous container(s) after a successful cutover — everything labelled for
    /// the app except the just-deployed container (incl. any legacy unversioned container).
    /// </summary>
    private Task RetireOldContainersAsync(
        IDockerEngine docker, string slug, string keepContainerName, Func<LogStream, string, Task> log, CancellationToken ct) =>
        RetireOldContainersAsync(docker, slug, new[] { keepContainerName }, log, ct);

    /// <summary>Stack form: keeps every container of the new set (see <c>ContainersToRetire</c>).</summary>
    private async Task RetireOldContainersAsync(
        IDockerEngine docker, string slug, IReadOnlyCollection<string> keepContainerNames,
        Func<LogStream, string, Task> log, CancellationToken ct)
    {
        var existing = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
        var toRetire = DeploymentPlanning.ContainersToRetire(existing, slug, keepContainerNames);
        foreach (var id in toRetire)
        {
            await log(LogStream.System, $"Retiring previous container {id[..12]} …");
            try { await docker.RemoveContainerAsync(id, force: true, ct); }
            catch (Exception ex) { await log(LogStream.System, $"(could not remove {id[..12]}: {ex.Message})"); }
        }
    }

    /// <summary>Best-effort removal of a single container by exact name (used to clean up a failed deploy).</summary>
    private static async Task TryRemoveContainerByNameAsync(IDockerEngine docker, string containerName, CancellationToken ct)
    {
        try
        {
            var existing = await docker.ListContainersAsync(DeploymentPlanning.AppLabel, ct);
            var match = existing.FirstOrDefault(c => c.Name == containerName);
            if (match is not null) await docker.RemoveContainerAsync(match.Id, force: true, ct);
        }
        catch { /* best effort — never mask the original failure */ }
    }

    /// <summary>
    /// Health gate: first wait for the container to reach "running" (fail fast if it exits). Then,
    /// for a local-server app with a health path, HTTP-probe it over the shared harbora network
    /// until it returns a success status. Remote nodes fall back to liveness (the panel can't reach
    /// their containers by name without an overlay network).
    /// </summary>
    private async Task<HealthReport> WaitForHealthyAsync(
        IDockerEngine docker, string upstreamHost, int upstreamPort, string containerName, string? healthPath,
        Func<string, Task> log, CancellationToken ct)
    {
        ContainerInfo? last = null;
        var running = false;
        for (var i = 0; i < _opt.HealthRunningAttempts && !running; i++)
        {
            await Task.Delay(_opt.HealthPollInterval, ct);
            var c = (await docker.ListContainersAsync("harbora.app", ct)).FirstOrDefault(x => x.Name == containerName);
            if (c is null) return new HealthReport(HealthFailure.Vanished);
            last = c;
            if (CrashFailure(c) is { } crash) return await FailedAsync(docker, crash, c, ct);
            running = c.State.Equals("running", StringComparison.OrdinalIgnoreCase);
        }
        if (!running) return await FailedAsync(docker, HealthFailure.NeverStarted, last, ct);

        if (string.IsNullOrWhiteSpace(healthPath))
            return HealthReport.Healthy;

        // Probe the same address the proxy will use: container name on the local network, or the
        // node's host:publishedPort for a remote node.
        var url = $"http://{upstreamHost}:{upstreamPort}/{healthPath.TrimStart('/')}";
        await log($"HTTP health check → {url}");
        var client = httpFactory.CreateClient();
        client.Timeout = _opt.HealthHttpTimeout;

        for (var attempt = 0; attempt < _opt.HealthHttpAttempts; attempt++)
        {
            try
            {
                using var res = await client.GetAsync(url, ct);
                if ((int)res.StatusCode < 400) return HealthReport.Healthy;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                // App still booting / not accepting connections yet — keep trying.
            }
            await Task.Delay(_opt.HealthPollInterval, ct);
        }
        await log("Health check did not pass within the timeout.");

        // Re-read the container: an app that fails its health path has often died in the meantime,
        // and the crash is a far more useful thing to report than the unanswered probe.
        var current = (await docker.ListContainersAsync("harbora.app", ct)).FirstOrDefault(x => x.Name == containerName);
        if (current is not null && CrashFailure(current) is { } lateCrash)
            return await FailedAsync(docker, lateCrash, current, ct);

        return await FailedAsync(docker, HealthFailure.NoHealthyResponse, current, ct, url);
    }

    /// <summary>
    /// Whether the container is dead or dying, and in which way.
    ///
    /// App containers run under <c>unless-stopped</c>, so a container that crashes on startup is
    /// revived by Docker within moments: in practice it reports "restarting", and almost never
    /// "exited". Watching only for "exited" is why a crash-looping app was reported as a container
    /// that was "running but never answered" — the opposite of what was happening.
    /// </summary>
    private static HealthFailure? CrashFailure(ContainerInfo c) =>
        c.State.Equals("exited", StringComparison.OrdinalIgnoreCase) ? HealthFailure.Exited
        : c.State.Equals("restarting", StringComparison.OrdinalIgnoreCase) ? HealthFailure.CrashLooping
        : null;

    /// <summary>
    /// Collects the container's own account of what happened, while it still exists — the failure
    /// path removes it moments later.
    /// </summary>
    private static async Task<HealthReport> FailedAsync(
        IDockerEngine docker, HealthFailure failure, ContainerInfo? container, CancellationToken ct,
        string? probeUrl = null)
    {
        string? tail = null;
        if (container is not null)
        {
            try { tail = await docker.GetLogsAsync(container.Id, 30, ct); }
            catch { /* diagnostics must never replace the failure they describe */ }
        }
        return new HealthReport(failure, container?.Status, tail, probeUrl);
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

    private string SafeUnprotect(string value)
    {
        try { return protector.Unprotect(value); }
        catch { return string.Empty; }
    }
}
