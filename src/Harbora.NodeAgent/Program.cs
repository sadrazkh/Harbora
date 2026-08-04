using Docker.DotNet;
using Harbora.NodeAgent;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Enrollment;
using Harbora.NodeAgent.Hosting;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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

// --- container runtime ---

builder.Services.AddSingleton<IDockerClient>(sp =>
{
    var host = Options(sp).DockerHost;
    return new DockerClientConfiguration(new Uri(host)).CreateClient();
});
builder.Services.AddSingleton<IContainerRuntime, DockerContainerRuntime>();

// --- control plane ---

builder.Services.AddSingleton(sp => new ControlPlaneTls(Options(sp), sp.GetRequiredService<ILogger<ControlPlaneTls>>()));
builder.Services.AddSingleton<IEnrollmentClient, HttpEnrollmentClient>();
builder.Services.AddSingleton<EnrollmentService>();
builder.Services.AddSingleton<IMessageTransportFactory, WebSocketTransportFactory>();
builder.Services.AddSingleton<ChannelOutbox>();
builder.Services.AddSingleton<ControlChannel>();

// --- agent services ---

builder.Services.AddSingleton<InventoryCollector>();
builder.Services.AddSingleton<NodeAuditLog>();
builder.Services.AddSingleton<NodeMetrics>();
builder.Services.AddSingleton<CommandLedger>();
builder.Services.AddSingleton<CommandDispatcher>();

builder.Services.AddHostedService<NodeAgentWorker>();
builder.Services.AddHostedService<MetricsEndpoint>();
builder.Services.AddHostedService<LedgerSweeper>();

await builder.Build().RunAsync();
return;

static NodeAgentOptions Options(IServiceProvider services) =>
    services.GetRequiredService<IOptions<NodeAgentOptions>>().Value;

static LogLevel ResolveLogLevel(IConfiguration configuration) =>
    Enum.TryParse<LogLevel>(configuration["Logging:LogLevel:Default"], ignoreCase: true, out var level)
        ? level
        : LogLevel.Information;
