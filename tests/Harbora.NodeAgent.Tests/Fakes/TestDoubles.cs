using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Harbora.NodeAgent;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Enrollment;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Tests.Fakes;

/// <summary>A clock the test moves by hand. Anything time-dependent is then deterministic.</summary>
public sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void Set(DateTimeOffset to) => _now = to;
}

public sealed class FakeHostFacts : IHostFacts
{
    public string Hostname { get; set; } = "test-node";
    public string OsName { get; set; } = "Debian GNU/Linux";
    public string OsVersion { get; set; } = "12";
    public string KernelVersion { get; set; } = "6.1.0-test";
    public string Architecture { get; set; } = "amd64";
    public int CpuCores { get; set; } = 4;
    public long TotalMemoryBytes { get; set; } = 8L * 1024 * 1024 * 1024;
    public long FreeMemoryBytes { get; set; } = 4L * 1024 * 1024 * 1024;
    public LoadAverage Load { get; set; } = new(0.4, 0.5, 0.6);
    public DiskSpace DiskSpace { get; set; } = new(200L * 1024 * 1024 * 1024, 100L * 1024 * 1024 * 1024);
    public List<string> Ips { get; set; } = ["203.0.113.10"];
    public List<int> Ports { get; set; } = [22, 443];

    /// <summary>
    /// Runs at the start of every disk read. A heartbeat samples the host part-way through its
    /// gathering, so this is where a test parks one loop to let another overtake it.
    /// </summary>
    public Action? BeforeDiskRead { get; set; }

    public DiskSpace Disk(string path)
    {
        BeforeDiskRead?.Invoke();
        return DiskSpace;
    }
    public IReadOnlyList<string> IpAddresses() => Ips;
    public IReadOnlyList<int> ListeningPorts() => Ports;
    public string MachineFingerprint() => "fingerprint-test";
}

/// <summary>Scripted enrollment answers, plus a record of what was asked.</summary>
public sealed class FakeEnrollmentClient : IEnrollmentClient
{
    private readonly TestCertificateAuthority _ca;

    public FakeEnrollmentClient(TestCertificateAuthority ca) => _ca = ca;

    public List<EnrollmentRequest> EnrollRequests { get; } = [];
    public List<CredentialRenewalRequest> RenewRequests { get; } = [];

    public NodeError? EnrollFailure { get; set; }
    public NodeError? RenewFailure { get; set; }
    public int ProtocolVersion { get; set; } = NodeContract.ProtocolVersion;
    public TimeSpan CertificateLifetime { get; set; } = TimeSpan.FromDays(90);
    public DateTimeOffset? NotBefore { get; set; }
    public IReadOnlyList<string>? GrantedScopes { get; set; }

    public Task<EnrollmentOutcome<EnrollmentResponse>> EnrollAsync(
        string controlPlaneUrl, string enrollmentToken, EnrollmentRequest request, CancellationToken ct)
    {
        EnrollRequests.Add(request);

        if (EnrollFailure is { } failure)
            return Task.FromResult(EnrollmentOutcome<EnrollmentResponse>.Fail(failure.Code, failure.Message, failure.Retryable));

        var notBefore = NotBefore ?? DateTimeOffset.UtcNow;
        var certificate = _ca.Sign(request.CertificateSigningRequestPem, notBefore, notBefore + CertificateLifetime);

        return Task.FromResult(EnrollmentOutcome<EnrollmentResponse>.Ok(new EnrollmentResponse
        {
            NodeId = "node-test-1",
            CertificatePem = certificate,
            CaCertificatePem = _ca.CertificatePem,
            CertificateNotAfter = notBefore + CertificateLifetime,
            ControlPlaneUrl = controlPlaneUrl,
            ProtocolVersion = ProtocolVersion,
            GrantedScopes = GrantedScopes ?? NodeScopes.Default,
            HeartbeatIntervalSeconds = 30,
        }));
    }

    public Task<EnrollmentOutcome<CredentialRenewalResponse>> RenewAsync(
        string controlPlaneUrl, NodeIdentity identity, CredentialRenewalRequest request, CancellationToken ct)
    {
        RenewRequests.Add(request);

        if (RenewFailure is { } failure)
            return Task.FromResult(EnrollmentOutcome<CredentialRenewalResponse>.Fail(failure.Code, failure.Message, failure.Retryable));

        var notBefore = NotBefore ?? DateTimeOffset.UtcNow;
        var certificate = _ca.Sign(request.CertificateSigningRequestPem, notBefore, notBefore + CertificateLifetime);

        return Task.FromResult(EnrollmentOutcome<CredentialRenewalResponse>.Ok(new CredentialRenewalResponse
        {
            CertificatePem = certificate,
            CaCertificatePem = _ca.CertificatePem,
            CertificateNotAfter = notBefore + CertificateLifetime,
            GrantedScopes = GrantedScopes,
        }));
    }
}

/// <summary>A throwaway CA so certificate handling can be exercised without a control plane.</summary>
public sealed class TestCertificateAuthority : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly X509Certificate2 _certificate;

    public TestCertificateAuthority()
    {
        var request = new CertificateRequest("CN=Harbora Test Node CA,O=Harbora", _key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));

        // Wide enough that a pinned test clock cannot fall outside it.
        //
        // This was `UtcNow.AddDays(-1)`, while the suites that use it pin their own clock to a fixed
        // date and sign leaf certificates at that date. The two agreed only while the fixed date
        // stayed within a day of the real one — so the whole suite passed for a while and then
        // failed permanently, everywhere, on a date nobody changed anything on. A fixture that
        // expires is a fixture that will strand somebody.
        _certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddYears(-5), DateTimeOffset.UtcNow.AddYears(5));
    }

    public string CertificatePem => _certificate.ExportCertificatePem();

    public string Sign(string csrPem, DateTimeOffset notBefore, DateTimeOffset notAfter)
    {
        var request = CertificateRequest.LoadSigningRequestPem(
            csrPem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

        var serial = new byte[16];
        RandomNumberGenerator.Fill(serial);

        using var issued = request.Create(_certificate, notBefore, notAfter, serial);
        return issued.ExportCertificatePem();
    }

    public void Dispose()
    {
        _certificate.Dispose();
        _key.Dispose();
    }
}

/// <summary>Two transports wired to each other's queues — a control plane without a socket.</summary>
public sealed class InMemoryTransportPair
{
    private readonly ConcurrentQueue<string> _toNode = new();
    private readonly ConcurrentQueue<string> _toControlPlane = new();
    private readonly SemaphoreSlim _nodeSignal = new(0);
    private readonly SemaphoreSlim _controlSignal = new(0);
    private volatile bool _closed;

    public IMessageTransport NodeSide { get; }

    public List<string> SentByNode { get; } = [];

    public InMemoryTransportPair() => NodeSide = new Side(this);

    /// <summary>Queue a frame for the node to read.</summary>
    public void PushToNode(ControlFrame frame) => PushToNode(NodeContract.Serialize(frame));

    public void PushToNode(string json)
    {
        _toNode.Enqueue(json);
        _nodeSignal.Release();
    }

    /// <summary>The next frame the node sent, waiting briefly for it to arrive.</summary>
    public async Task<ControlFrame?> NextFromNodeAsync(TimeSpan? timeout = null)
    {
        if (!await _controlSignal.WaitAsync(timeout ?? TimeSpan.FromSeconds(5))) return null;
        return _toControlPlane.TryDequeue(out var json) ? NodeContract.Deserialize<ControlFrame>(json) : null;
    }

    public void Close()
    {
        _closed = true;
        _nodeSignal.Release();
    }

    private sealed class Side(InMemoryTransportPair pair) : IMessageTransport
    {
        public bool IsOpen => !pair._closed;

        public Task SendAsync(string message, CancellationToken ct)
        {
            lock (pair.SentByNode) pair.SentByNode.Add(message);
            pair._toControlPlane.Enqueue(message);
            pair._controlSignal.Release();
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                if (pair._toNode.TryDequeue(out var json)) return json;
                if (pair._closed) return null;
                await pair._nodeSignal.WaitAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            return null;
        }

        public Task CloseAsync(string reason, CancellationToken ct)
        {
            pair.Close();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class InMemoryTransportFactory(InMemoryTransportPair pair) : IMessageTransportFactory
{
    public List<Uri> Dialled { get; } = [];

    public Task<IMessageTransport> ConnectAsync(Uri uri, NodeIdentity identity, CancellationToken ct)
    {
        Dialled.Add(uri);
        return Task.FromResult(pair.NodeSide);
    }
}

/// <summary>An in-memory container runtime: enough Docker to test everything above it.</summary>
public sealed class FakeContainerRuntime : IContainerRuntime
{
    public Dictionary<string, RuntimeContainer> Containers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Volumes { get; } = new(StringComparer.Ordinal);
    public HashSet<string> Networks { get; } = new(StringComparer.Ordinal);
    public List<string> PulledImages { get; } = [];
    public List<ContainerCreateRequest> Created { get; } = [];
    public List<OneOffRequest> OneOffs { get; } = [];
    public List<(string Container, IReadOnlyList<string> Argv, string? Stdin)> Execs { get; } = [];

    public bool Available { get; set; } = true;
    public string Version { get; set; } = "27.3.1";
    public NodeErrorCode? PullFailure { get; set; }
    public bool StartFails { get; set; }
    public bool? HealthOverride { get; set; }
    public string ImageArchitecture { get; set; } = "amd64";
    public Func<IReadOnlyList<string>, ExecResult>? ExecHandler { get; set; }
    public int OneOffExitCode { get; set; }
    public string Logs { get; set; } = "log line one\nlog line two\n";

    public Task<RuntimeInfo> GetInfoAsync(CancellationToken ct) =>
        Task.FromResult(new RuntimeInfo("docker", Version, "1.47", Containers.Count, Available,
            Available ? null : "daemon not reachable"));

    public Task PullImageAsync(string reference, IProgress<string>? log, CancellationToken ct)
    {
        if (PullFailure is { } code)
            throw new ContainerRuntimeException(code, $"pull of {reference} refused by the test");

        PulledImages.Add(reference);
        log?.Report($"Pulled {reference}");
        return Task.CompletedTask;
    }

    /// <summary>Lets a test model a registry that hands back something other than what was asked for.</summary>
    public Func<string, string?>? DigestOverride { get; set; }

    public Task<string?> ResolveDigestAsync(string reference, CancellationToken ct)
    {
        if (DigestOverride is { } substitute) return Task.FromResult(substitute(reference));

        var digest = reference.Contains('@') ? reference.Split('@')[1] : null;
        return Task.FromResult(PulledImages.Contains(reference) ? digest : null);
    }

    public Task<IReadOnlyList<string>> GetImageArchitecturesAsync(string reference, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>>([ImageArchitecture]);

    public Task<IReadOnlyList<RuntimeContainer>> ListContainersAsync(
        IReadOnlyDictionary<string, string>? labelFilter, bool includeStopped, CancellationToken ct)
    {
        var matches = Containers.Values
            .Where(c => includeStopped || c.State == "running")
            .Where(c => labelFilter is null || labelFilter.All(f =>
                c.Labels.TryGetValue(f.Key, out var value) && value == f.Value))
            .ToList();

        return Task.FromResult<IReadOnlyList<RuntimeContainer>>(matches);
    }

    public Task<RuntimeContainer?> InspectAsync(string idOrName, CancellationToken ct)
    {
        var found = Containers.Values.FirstOrDefault(c => c.Id == idOrName || c.Name == idOrName);
        if (found is not null && HealthOverride is { } health) found = found with { Healthy = health };
        return Task.FromResult(found);
    }

    /// <summary>
    /// What the runtime would report for a container, keyed by name. Absent means the runtime
    /// declined to answer — the ordinary case for a container that is starting or already gone, and
    /// the one a caller must not read as a reading of zero.
    /// </summary>
    public Dictionary<string, RuntimeContainerStats> Stats { get; } = new(StringComparer.Ordinal);

    public Task<RuntimeContainerStats?> GetStatsAsync(string idOrName, CancellationToken ct) =>
        Task.FromResult(Stats.TryGetValue(idOrName, out var stats) ? stats : null);

    public Task<string> CreateAndStartAsync(ContainerCreateRequest request, CancellationToken ct)
    {
        Created.Add(request);

        if (StartFails)
            throw new ContainerRuntimeException(NodeErrorCode.ContainerStartFailed, $"{request.Name} refused to start");

        var id = $"ctr-{Containers.Count + 1:000}";

        Containers[id] = new RuntimeContainer(
            id, request.Name, request.ImageReference,
            request.ImageReference.Contains('@') ? request.ImageReference.Split('@')[1] : null,
            "running", "Up 1 second",
            HealthOverride ?? true, 0, DateTimeOffset.UtcNow,
            request.Labels, request.Ports.Where(p => p.HostPort is not null)
                .ToDictionary(p => p.ContainerPort, p => p.HostPort!.Value),
            request.Network is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [request.Network] = "172.20.0.5" });

        return Task.FromResult(id);
    }

    public Task StopAsync(string idOrName, int gracePeriodSeconds, CancellationToken ct)
    {
        Mutate(idOrName, c => c with { State = "exited", Healthy = false });
        return Task.CompletedTask;
    }

    public Task StartAsync(string idOrName, CancellationToken ct)
    {
        Mutate(idOrName, c => c with { State = "running", Healthy = HealthOverride ?? true });
        return Task.CompletedTask;
    }

    public Task RestartAsync(string idOrName, CancellationToken ct)
    {
        Mutate(idOrName, c => c with { State = "running", RestartCount = c.RestartCount + 1 });
        return Task.CompletedTask;
    }

    public Task RemoveAsync(string idOrName, bool force, CancellationToken ct)
    {
        var key = Containers.FirstOrDefault(kv => kv.Value.Id == idOrName || kv.Value.Name == idOrName).Key;
        if (key is not null) Containers.Remove(key);
        return Task.CompletedTask;
    }

    public Task<string> GetLogsAsync(string idOrName, int tailLines, CancellationToken ct) => Task.FromResult(Logs);

    public Task StreamLogsAsync(string idOrName, int tailLines, IProgress<string> sink, CancellationToken ct)
    {
        foreach (var line in Logs.Split('\n', StringSplitOptions.RemoveEmptyEntries)) sink.Report(line);
        return Task.CompletedTask;
    }

    public Task EnsureNetworkAsync(NetworkSpec spec, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        Networks.Add(spec.Name);
        return Task.CompletedTask;
    }

    public Task RemoveNetworkAsync(string name, CancellationToken ct)
    {
        Networks.Remove(name);
        return Task.CompletedTask;
    }

    public Task ConnectToNetworkAsync(string containerIdOrName, string network, IReadOnlyList<string> aliases, CancellationToken ct) =>
        Task.CompletedTask;

    public Task EnsureVolumeAsync(string name, IReadOnlyDictionary<string, string> labels, CancellationToken ct)
    {
        Volumes.Add(name);
        return Task.CompletedTask;
    }

    public Task RemoveVolumeAsync(string name, CancellationToken ct)
    {
        Volumes.Remove(name);
        return Task.CompletedTask;
    }

    public Task<bool> VolumeExistsAsync(string name, CancellationToken ct) => Task.FromResult(Volumes.Contains(name));

    /// <summary>Checksum the fake's <c>sha256sum</c> helper reports. Tests override it to model corruption.</summary>
    public string HelperChecksum { get; set; } = new('a', 64);

    public long HelperSize { get; set; } = 4096;

    public Task<int> RunOneOffAsync(OneOffRequest request, IProgress<string>? log, CancellationToken ct)
    {
        OneOffs.Add(request);

        // Model what busybox actually prints, so the archiver's parsing is exercised rather than
        // stubbed past.
        switch (request.Command.FirstOrDefault())
        {
            case "sha256sum":
                log?.Report($"{HelperChecksum}  {request.Command.ElementAtOrDefault(1)}");
                break;
            case "stat":
                log?.Report(HelperSize.ToString());
                break;
        }

        return Task.FromResult(OneOffExitCode);
    }

    public Task<ExecResult> ExecAsync(
        string containerIdOrName, IReadOnlyList<string> argv,
        IReadOnlyDictionary<string, string>? env, string? stdin, CancellationToken ct)
    {
        Execs.Add((containerIdOrName, argv, stdin));
        return Task.FromResult(ExecHandler?.Invoke(argv) ?? new ExecResult(0, string.Empty, string.Empty));
    }

    private void Mutate(string idOrName, Func<RuntimeContainer, RuntimeContainer> change)
    {
        var key = Containers.FirstOrDefault(kv => kv.Value.Id == idOrName || kv.Value.Name == idOrName).Key;
        if (key is not null) Containers[key] = change(Containers[key]);
    }
}

/// <summary>Options wrapper with a temp data directory that cleans itself up.</summary>
public sealed class TempAgent : IDisposable
{
    public TempAgent(Action<NodeAgentOptions>? configure = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "harbora-node-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Root);

        Options = new NodeAgentOptions
        {
            ControlPlaneUrl = "https://panel.test",
            NodeName = "test-node",
            DataDirectory = Root,
        };

        configure?.Invoke(Options);
    }

    public string Root { get; }
    public NodeAgentOptions Options { get; }
    public IOptions<NodeAgentOptions> Wrapped => Microsoft.Extensions.Options.Options.Create(Options);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a leftover temp directory is not a test failure */ }
        catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Records the frames a dispatcher produces.</summary>
public sealed class RecordingResponder : Harbora.NodeAgent.Commands.ICommandResponder
{
    public List<CommandAck> Acks { get; } = [];
    public List<CommandProgress> Progress { get; } = [];
    public List<LogChunk> Logs { get; } = [];
    public List<CommandResult> Results { get; } = [];

    public Task AckAsync(CommandAck ack, string correlationId, CancellationToken ct)
    {
        lock (Acks) Acks.Add(ack);
        return Task.CompletedTask;
    }

    public Task ProgressAsync(CommandProgress progress, string correlationId, CancellationToken ct)
    {
        lock (Progress) Progress.Add(progress);
        return Task.CompletedTask;
    }

    public Task LogAsync(LogChunk chunk, string correlationId, CancellationToken ct)
    {
        lock (Logs) Logs.Add(chunk);
        return Task.CompletedTask;
    }

    public Task ResultAsync(CommandResult result, string correlationId, CancellationToken ct)
    {
        lock (Results) Results.Add(result);
        return Task.CompletedTask;
    }
}
