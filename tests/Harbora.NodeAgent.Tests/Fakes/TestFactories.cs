using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Security;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Harbora.NodeAgent.Tests.Fakes;

/// <summary>Builders for the small graph of collaborators most tests need.</summary>
public static class TestFactories
{
    public static ILogger<T> Log<T>() => NullLogger<T>.Instance;

    public static JsonFileStore<T> Store<T>(TempAgent agent, string name) where T : class =>
        new(Path.Combine(agent.Options.StateDirectory, name));

    public static NodeAuditLog Audit(TempAgent agent, SecretRedactor? redactor = null) =>
        new(agent.Wrapped, redactor ?? new SecretRedactor(), Log<NodeAuditLog>());

    public static CommandLedger Ledger(TempAgent agent, ManualClock clock) =>
        new(Store<CommandLedgerState>(agent, "commands.json"), agent.Wrapped, clock, Log<CommandLedger>());

    /// <summary>A well-formed envelope. Tests override only the field they are about.</summary>
    public static CommandEnvelope Envelope(
        string command,
        object? payload = null,
        string? idempotencyKey = null,
        string? scope = null,
        DateTimeOffset? issuedAt = null,
        string? nonce = null,
        string? tenantId = "tenant-1",
        int? timeoutSeconds = null)
    {
        NodeCommandCatalog.TryGet(command, out var descriptor);

        return new CommandEnvelope
        {
            CommandId = Guid.NewGuid().ToString("n"),
            Command = command,
            IdempotencyKey = idempotencyKey ?? Guid.NewGuid().ToString("n"),
            Nonce = nonce ?? Guid.NewGuid().ToString("n"),
            IssuedAt = issuedAt ?? DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("n"),
            RequiredScope = scope ?? descriptor?.RequiredScope ?? NodeScopes.NodeAdmin,
            TimeoutSeconds = timeoutSeconds,
            Audit = new AuditMetadata { TenantId = tenantId, ActorName = "tester@example.com" },
            Payload = payload is null
                ? System.Text.Json.JsonSerializer.SerializeToElement(new { }, NodeContract.Json)
                : System.Text.Json.JsonSerializer.SerializeToElement(payload, NodeContract.Json),
        };
    }

    /// <summary>An enrolled node state with every scope granted.</summary>
    public static NodeState EnrolledState(bool draining = false) => new()
    {
        NodeId = "node-test-1",
        GrantedScopes = NodeScopes.Default,
        Draining = draining,
        ControlPlaneUrl = "https://panel.test",
        HeartbeatIntervalSeconds = 30,
    };

    /// <summary>A minimal valid workload spec: one pinned container, one volume, no privileges.</summary>
    public static WorkloadSpec Workload(
        string workloadId = "wl-1",
        string tenantId = "tenant-1",
        string? digest = null,
        Action<ContainerSpecBuilder>? container = null)
    {
        var builder = new ContainerSpecBuilder
        {
            Image = new ImageRef
            {
                Repository = "registry.test/app",
                Tag = "1.0.0",
                Digest = digest ?? "sha256:" + new string('a', 64),
            },
        };

        container?.Invoke(builder);

        return new WorkloadSpec
        {
            WorkloadId = workloadId,
            Name = "test-app",
            TenantId = tenantId,
            AppVersion = "1.0.0",
            Containers = [builder.Build()],
            Networks = [new NetworkSpec { Name = $"harbora-{tenantId}" }],
            Volumes = [new VolumeSpec { Name = "test-app-data" }],
        };
    }

    public sealed class ContainerSpecBuilder
    {
        public string Name { get; set; } = "app";
        public ImageRef Image { get; set; } = null!;
        public bool Privileged { get; set; }
        public bool HostNetwork { get; set; }
        public bool HostPidNamespace { get; set; }
        public List<string> CapabilitiesAdd { get; } = [];
        public List<MountSpec> Mounts { get; } = [new() { VolumeName = "test-app-data", MountPath = "/data" }];
        public List<SecretSpec> Secrets { get; } = [];
        public List<PortMapping> Ports { get; } = [new() { ContainerPort = 8080 }];
        public HealthCheckSpec? HealthCheck { get; set; }

        public ContainerSpec Build() => new()
        {
            Name = Name,
            Image = Image,
            Privileged = Privileged,
            HostNetwork = HostNetwork,
            HostPidNamespace = HostPidNamespace,
            CapabilitiesAdd = CapabilitiesAdd,
            Mounts = Mounts,
            Secrets = Secrets,
            Ports = Ports,
            HealthCheck = HealthCheck,
            Resources = new ResourceLimits { CpuCores = 0.5, MemoryBytes = 512 * 1024 * 1024 },
        };
    }
}

/// <summary>A handler that does whatever the test tells it to.</summary>
public sealed class ScriptedHandler(string command, Func<CommandContext, CancellationToken, Task<CommandResult>> body)
    : INodeCommandHandler
{
    public string Command { get; } = command;
    public int Invocations { get; private set; }

    public Task<CommandResult> HandleAsync(CommandContext context, CancellationToken ct)
    {
        Invocations++;
        return body(context, ct);
    }

    public static ScriptedHandler Succeeding(string command, object? result = null) =>
        new(command, (context, _) => Task.FromResult(context.Ok(result ?? new AcknowledgedResult { Applied = true })));

    public static ScriptedHandler Failing(string command, NodeErrorCode code, string message) =>
        new(command, (context, _) => Task.FromResult(context.Fail(code, message)));

    public static ScriptedHandler Hanging(string command) =>
        new(command, async (context, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return context.Ok(new AcknowledgedResult { Applied = true });
        });
}
