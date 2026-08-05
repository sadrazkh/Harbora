using Docker.DotNet;
using Harbora.NodeAgent;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Commands.Handlers;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Database;
using Harbora.NodeAgent.Enrollment;
using Harbora.NodeAgent.Hosting;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Transport;
using Harbora.NodeAgent.Tunnels;
using Harbora.NodeAgent.Updates;
using Harbora.NodeAgent.Workspaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// The installer and the updater both ask the binary what it is before trusting it, so this has to
// answer without touching configuration, the network or the container runtime.
if (args.Contains("--version") || args.Contains("-v"))
{
    Console.WriteLine(AgentVersion.Current);
    return;
}

var builder = Host.CreateApplicationBuilder(args);

// Configuration, lowest precedence first. The installer writes agent.conf, so an operator editing
// that file wins over whatever shipped in the binary — and an environment override wins over both,
// which is what makes a one-off `systemctl edit` work without touching any file on disk.
builder.Configuration
    .AddJsonFile("/etc/harbora-node/agent.conf", optional: true, reloadOnChange: false)
    .AddEnvironmentVariables("HARBORA_NODE_")
    .AddCommandLine(args);

builder.Services
    .AddOptions<NodeAgentOptions>()
    .Bind(builder.Configuration.GetSection(NodeAgentOptions.SectionName));

var redactor = new SecretRedactor();
builder.Services.AddSingleton(redactor);

// The redacting provider replaces the default console logger rather than sitting beside it —
// leaving the console provider registered would print the unredacted line next to the safe one.
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new StructuredLoggerProvider(redactor, ResolveLogLevel(builder.Configuration)));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IHostFacts, HostFacts>();

// --- state on disk ---

builder.Services.AddSingleton(sp => new NodeIdentityStore(Options(sp).IdentityDirectory));
builder.Services.AddSingleton(sp => new JsonFileStore<NodeState>(Path.Combine(Options(sp).StateDirectory, "node.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<OutboxState>(Path.Combine(Options(sp).StateDirectory, "outbox.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<CommandLedgerState>(Path.Combine(Options(sp).StateDirectory, "commands.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<WorkloadRegistryState>(Path.Combine(Options(sp).StateDirectory, "workloads.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<RouteRegistryState>(Path.Combine(Options(sp).StateDirectory, "routes.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<GrantStoreState>(Path.Combine(Options(sp).StateDirectory, "grants.json")));
builder.Services.AddSingleton(sp => new JsonFileStore<PendingUpdate>(Path.Combine(Options(sp).StateDirectory, "pending-update.json")));

// --- container runtime ---

builder.Services.AddSingleton<IDockerClient>(sp =>
{
    var host = Options(sp).DockerHost;
    return new DockerClientConfiguration(new Uri(host)).CreateClient();
});
builder.Services.AddSingleton<IContainerRuntime, DockerContainerRuntime>();
builder.Services.AddSingleton<WorkloadRegistry>();
builder.Services.AddSingleton<RouteRegistry>();
builder.Services.AddSingleton(sp => new PortAllocator(Options(sp).Ports));
builder.Services.AddSingleton(sp => new HealthProbe(
    sp.GetRequiredService<IContainerRuntime>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<ILogger<HealthProbe>>()));
builder.Services.AddSingleton<VolumeArchiver>();
builder.Services.AddSingleton<DockerWorkspaceProvisioner>();
builder.Services.AddSingleton<WorkloadDeployer>();
builder.Services.AddSingleton<StateReconciler>();

// --- external database access ---

builder.Services.AddSingleton<LocalSecretVault>();
builder.Services.AddSingleton<DatabaseEngineOperations>();
builder.Services.AddSingleton<ITunnelConnectionFactory, TlsTunnelConnectionFactory>();
builder.Services.AddSingleton<ILocalDialer, TcpLocalDialer>();
builder.Services.AddSingleton<TunnelSupervisor>();
builder.Services.AddSingleton<IngressTunnel>();
builder.Services.AddSingleton<DatabaseAccessManager>();

// --- self-update and drain ---

builder.Services.AddSingleton<IUpdateDownloader, HttpUpdateDownloader>();
builder.Services.AddSingleton<IServiceController, SystemdServiceController>();
builder.Services.AddSingleton<DrainCoordinator>();
builder.Services.AddSingleton<AgentUpdater>();

// --- control plane ---

builder.Services.AddSingleton(sp => new ControlPlaneTls(Options(sp), sp.GetRequiredService<ILogger<ControlPlaneTls>>()));
builder.Services.AddSingleton<IEnrollmentClient, HttpEnrollmentClient>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<IMessageTransportFactory, WebSocketTransportFactory>();
builder.Services.AddSingleton<ChannelOutbox>();
builder.Services.AddSingleton<ControlChannel>();

// --- agent services ---

builder.Services.AddSingleton<ImplementedCommands>();
builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<NodeAuditLog>();
builder.Services.AddSingleton<NodeMetrics>();
builder.Services.AddSingleton<CommandLedger>();
builder.Services.AddSingleton<CommandDispatcher>();
builder.Services.AddSingleton<INodeEventPublisher, ChannelEventPublisher>();

// --- command handlers: one registration per verb in the allowlist ---

// Deploy and update are the same operation. Two verbs exist because the control plane's intent
// differs, and an audit line that cannot tell them apart is worth less than one that can.
builder.Services.AddSingleton<INodeCommandHandler>(sp => new DeployWorkloadHandler(
    sp.GetRequiredService<WorkloadDeployer>(),
    sp.GetRequiredService<JsonFileStore<NodeState>>(),
    NodeCommands.DeployWorkload,
    sp.GetRequiredService<ILogger<DeployWorkloadHandler>>()));

builder.Services.AddSingleton<INodeCommandHandler>(sp => new DeployWorkloadHandler(
    sp.GetRequiredService<WorkloadDeployer>(),
    sp.GetRequiredService<JsonFileStore<NodeState>>(),
    NodeCommands.UpdateWorkload,
    sp.GetRequiredService<ILogger<DeployWorkloadHandler>>()));

builder.Services.AddSingleton<INodeCommandHandler, StopWorkloadHandler>();
builder.Services.AddSingleton<INodeCommandHandler, StartWorkloadHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RestartWorkloadHandler>();
builder.Services.AddSingleton<INodeCommandHandler, DeleteWorkloadHandler>();
builder.Services.AddSingleton<INodeCommandHandler, GetWorkloadStatusHandler>();
// Registering it is what advertises it: SupportedCommands is read from the wired handlers, so a
// control plane learns this node can answer for stats without a separate flag to fall out of step.
builder.Services.AddSingleton<INodeCommandHandler, GetWorkloadStatsHandler>();
builder.Services.AddSingleton<INodeCommandHandler, ListWorkloadsHandler>();
builder.Services.AddSingleton<INodeCommandHandler, StreamLogsHandler>();

builder.Services.AddSingleton<INodeCommandHandler, CreateNetworkHandler>();
builder.Services.AddSingleton<INodeCommandHandler, DeleteNetworkHandler>();
builder.Services.AddSingleton<INodeCommandHandler, CreateVolumeHandler>();
builder.Services.AddSingleton<INodeCommandHandler, SnapshotVolumeHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RestoreVolumeHandler>();

builder.Services.AddSingleton<INodeCommandHandler, RegisterHttpRouteHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RegisterTcpRouteHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RemoveRouteHandler>();
builder.Services.AddSingleton<INodeCommandHandler, ConfigureIngressHandler>();

builder.Services.AddSingleton<INodeCommandHandler, CreateDatabaseAccessGrantHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RevokeDatabaseAccessGrantHandler>();
builder.Services.AddSingleton<INodeCommandHandler, RotateDatabaseAccessCredentialHandler>();

builder.Services.AddSingleton<INodeCommandHandler, DrainNodeHandler>();
builder.Services.AddSingleton<INodeCommandHandler, UpdateAgentHandler>();

builder.Services.AddHostedService<NodeAgentWorker>();
builder.Services.AddHostedService<MetricsEndpoint>();
builder.Services.AddHostedService<LedgerSweeper>();
builder.Services.AddHostedService<GrantSweeper>();

await builder.Build().RunAsync();
return;

static NodeAgentOptions Options(IServiceProvider services) =>
    services.GetRequiredService<IOptions<NodeAgentOptions>>().Value;

static LogLevel ResolveLogLevel(IConfiguration configuration) =>
    Enum.TryParse<LogLevel>(configuration["Logging:LogLevel:Default"], ignoreCase: true, out var level)
        ? level
        : LogLevel.Information;
