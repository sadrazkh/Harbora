using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Commands;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Enrollment;
using Harbora.NodeAgent.Identity;
using Harbora.NodeAgent.Inventory;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Harbora.NodeAgent.Transport;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Hosting;

/// <summary>
/// The agent's main loop: enroll once, then stay connected — reconnecting, resuming, heartbeating
/// and dispatching for as long as the service runs.
///
/// <para>
/// Structured so that no failure below "the operator must act" stops the process. systemd will
/// restart a crashed agent, but a crash loop hides the reason in a scroll of restarts; a running
/// agent that reports why it cannot connect is diagnosable from <c>journalctl</c>.
/// </para>
/// </summary>
public sealed class NodeAgentWorker(
    IOptions<NodeAgentOptions> options,
    EnrollmentService enrollment,
    ControlChannel channel,
    CommandDispatcher dispatcher,
    CommandLedger ledger,
    StateReconciler reconciler,
    JsonFileStore<NodeState> stateStore,
    InventoryCollector inventory,
    IContainerRuntime runtime,
    IHostFacts host,
    NodeAuditLog audit,
    NodeMetrics metrics,
    IHostApplicationLifetime lifetime,
    TimeProvider clock,
    ILogger<NodeAgentWorker> log) : BackgroundService
{
    private readonly NodeAgentOptions _options = options.Value;
    private readonly ReconnectPolicy _reconnect = new(options.Value.Reconnect);
    private readonly NodeHealthEvaluator _health = new(host, options.Value);

    private NodeIdentity? _identity;
    private bool _credentialRevoked;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Startup()) return;

        _identity = await EnrollWithRetryAsync(stoppingToken);
        if (_identity is null) return;

        // Before the first connection, not after: the control plane's first heartbeat should
        // describe the node as it actually is, not as it was when the machine went down.
        await ReconcileAsync(stoppingToken);

        var attempt = 0;

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = _reconnect.Delay(++attempt);
            if (delay > TimeSpan.Zero)
            {
                log.LogInformation("Reconnecting in {Delay:0.0}s (attempt {Attempt}).", delay.TotalSeconds, attempt);
                await SafeDelayAsync(delay, stoppingToken);
                if (stoppingToken.IsCancellationRequested) break;
            }

            if (!await RenewIfDueAsync(stoppingToken)) return;

            try
            {
                await channel.OpenAsync(_identity!, stoppingToken);

                // Only a completed handshake resets the backoff. Resetting on a successful TCP
                // connect would turn a control plane that accepts and immediately drops into a
                // tight loop that looks, from its side, like a denial of service.
                attempt = 0;
                metrics.ChannelConnected(clock.GetUtcNow());

                await RunSessionAsync(stoppingToken);
            }
            catch (ProtocolNegotiationException e)
            {
                // Not survivable by retrying, and not a reason to stop reporting in either: the
                // node keeps trying so that an operator who updates the agent sees it recover.
                log.LogCritical("{Message} Update this node's agent.", e.Message);
                audit.Write(new NodeAuditEntry { Action = "channel.negotiate", Outcome = "failed", Detail = e.Message });
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Control channel failed; will reconnect.");
            }
            finally
            {
                metrics.ChannelDisconnected();
                await channel.CloseAsync("agent reconnecting");
            }
        }

        log.LogInformation("Node agent stopping.");
    }

    /// <summary>Everything that must be true before the agent is worth starting.</summary>
    private bool Startup()
    {
        var problems = _options.Validate();
        if (problems.Count > 0)
        {
            foreach (var problem in problems) log.LogCritical("Configuration: {Problem}", problem);
            lifetime.StopApplication();
            return false;
        }

        Directory.CreateDirectory(_options.DataDirectory);
        FilePermissions.RestrictDirectory(_options.DataDirectory);

        if (!inventory.ArchitectureIsSupported())
        {
            log.LogCritical(
                "Architecture '{Architecture}' is not supported; Harbora nodes run on amd64 or arm64.",
                host.Architecture);
            lifetime.StopApplication();
            return false;
        }

        if (_options.Security.AllowPrivilegedWorkloads)
            // Recorded as well as logged: turning this on is a change to what the node will let a
            // control plane do to it, and the audit trail is where that belongs.
            audit.Write(new NodeAuditEntry
            {
                Action = "node.startup",
                Outcome = "privileged-workloads-enabled",
                Detail = "Security:AllowPrivilegedWorkloads is on. Privileged containers, host networking and the host PID namespace are permitted for node-admin commands.",
            });

        ledger.Sweep();

        log.LogInformation(
            "Harbora node agent {Version} starting on {Os} {OsVersion} ({Architecture}, {Cores} core(s)).",
            AgentVersion.Current, host.OsName, host.OsVersion, host.Architecture, host.CpuCores);

        return true;
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        try
        {
            var report = await reconciler.ReconcileAsync(ct);

            if (report.Problems.Count > 0)
                log.LogWarning("Reconciliation found {Count} problem(s): {Problems}",
                    report.Problems.Count, string.Join("; ", report.Problems));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            // A node that cannot reconcile is still a node that can take new work. Failing to start
            // over it would turn a recoverable mess into an unreachable one.
            log.LogError(e, "Reconciliation failed; continuing to connect.");
        }
    }

    private async Task<NodeIdentity?> EnrollWithRetryAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            var outcome = await enrollment.EnsureEnrolledAsync(ct);
            if (outcome.Success)
            {
                metrics.CertificateExpiry(outcome.Value!.NotAfter);
                return outcome.Value;
            }

            var error = outcome.Error!;
            audit.Write(new NodeAuditEntry
            {
                Action = "node.enroll",
                Outcome = "failed",
                ErrorCode = error.Code.ToString(),
                Detail = error.Message,
            });

            if (EnrollmentService.IsTerminal(error.Code))
            {
                log.LogCritical(
                    "Enrollment cannot succeed: {Code} — {Message} The agent is stopping; fix this and start it again.",
                    error.Code, error.Message);
                lifetime.StopApplication();
                return null;
            }

            var delay = _reconnect.Delay(++attempt + 1);
            log.LogWarning(
                "Enrollment failed ({Code}: {Message}); retrying in {Delay:0.0}s.",
                error.Code, error.Message, delay.TotalSeconds);

            await SafeDelayAsync(delay, ct);
        }

        return null;
    }

    /// <summary>
    /// Renew the credential when enough of its life has been spent. Returns false only when the
    /// credential is beyond saving and an admin has to re-enroll the node.
    /// </summary>
    private async Task<bool> RenewIfDueAsync(CancellationToken ct)
    {
        if (_identity is null || !enrollment.NeedsRenewal(_identity)) return true;

        var outcome = await enrollment.RenewAsync(_identity, ct);

        if (outcome.Success)
        {
            _identity = outcome.Value;
            _credentialRevoked = false;
            metrics.CertificateExpiry(_identity!.NotAfter);
            metrics.CertificateRotated();

            audit.Write(new NodeAuditEntry
            {
                Action = "node.credential-renew",
                Outcome = "succeeded",
                Detail = $"valid until {_identity.NotAfter:u}",
            });
            return true;
        }

        var error = outcome.Error!;

        audit.Write(new NodeAuditEntry
        {
            Action = "node.credential-renew",
            Outcome = "failed",
            ErrorCode = error.Code.ToString(),
            Detail = error.Message,
        });

        if (error.Code == NodeErrorCode.CredentialRevoked)
        {
            _credentialRevoked = true;
            log.LogCritical(
                "This node's credential has been revoked by the control plane. Re-enroll it with a fresh token: {Message}",
                error.Message);
            lifetime.StopApplication();
            return false;
        }

        // Any other failure is transient by assumption. The certificate is still valid — that is
        // the whole reason renewal starts early — so the node keeps working and tries again.
        log.LogWarning(
            "Credential renewal did not succeed ({Code}); the current certificate is valid until {NotAfter:u}.",
            error.Code, _identity.NotAfter);
        return true;
    }

    /// <summary>Read frames until the channel closes, heartbeating alongside.</summary>
    private async Task RunSessionAsync(CancellationToken stoppingToken)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);

        var responder = new ChannelResponder(channel);
        var heartbeat = HeartbeatLoopAsync(session.Token);
        var commands = new List<Task>();

        try
        {
            await foreach (var frame in channel.ReadAsync(session.Token))
            {
                switch (frame.Type)
                {
                    case ControlFrames.Command when frame.PayloadAs<CommandEnvelope>() is { } envelope:
                        // Not awaited: a 30-minute deploy must not block the heartbeat or the
                        // cancel frame that would stop it.
                        commands.Add(dispatcher.ExecuteAsync(envelope, responder, session.Token));
                        commands.RemoveAll(t => t.IsCompleted);
                        break;

                    case ControlFrames.Cancel when frame.PayloadAs<CommandCancel>() is { } cancel:
                        dispatcher.Cancel(cancel.CommandId, cancel.Reason);
                        break;

                    case ControlFrames.CredentialRotated:
                        log.LogInformation("The control plane asked for a credential rotation.");
                        await RenewIfDueAsync(session.Token);
                        break;

                    default:
                        log.LogDebug("Ignoring an unhandled {Type} frame.", frame.Type);
                        break;
                }
            }
        }
        finally
        {
            await session.CancelAsync();
            await Task.WhenAll(heartbeat).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None)
                .ContinueWith(_ => { }, TaskScheduler.Default);

            // In-flight commands are given a moment to notice the cancellation and report a result.
            if (commands.Count > 0)
                await Task.WhenAll(commands).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None)
                    .ContinueWith(_ => { }, TaskScheduler.Default);
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(
            channel.Session?.HeartbeatIntervalSeconds > 0
                ? channel.Session.HeartbeatIntervalSeconds
                : _options.HeartbeatIntervalSeconds);

        while (!ct.IsCancellationRequested && channel.IsConnected)
        {
            try
            {
                await SendHeartbeatAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogDebug(e, "Heartbeat failed; the channel is probably closing.");
            }

            await SafeDelayAsync(interval, ct);
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        var runtimeInfo = await runtime.GetInfoAsync(ct);

        var managed = runtimeInfo.Available
            ? await runtime.ListContainersAsync(
                new Dictionary<string, string> { [NodeLabels.Managed] = "true" }, includeStopped: false, ct)
            : [];

        var persisted = stateStore.Load() ?? new NodeState();

        var verdict = _health.Evaluate(
            new HealthInputs
            {
                RuntimeAvailable = runtimeInfo.Available,
                Draining = persisted.Draining,
                ChannelConnected = channel.IsConnected,
                CertificateExpiresAt = _identity?.NotAfter,
                CredentialRevoked = _credentialRevoked,
            },
            clock.GetUtcNow());

        metrics.Health(verdict);
        metrics.RunningWorkloads(managed.Count);

        var disk = host.Disk(_options.DataDirectory);

        await channel.SendEphemeralAsync(NodeFrames.Heartbeat, new NodeHeartbeat
        {
            NodeId = persisted.NodeId ?? "unknown",
            AgentVersion = AgentVersion.Current,
            Health = verdict.State,
            Load1 = host.Load.One,
            Load5 = host.Load.Five,
            Load15 = host.Load.Fifteen,
            FreeMemoryBytes = host.FreeMemoryBytes,
            FreeDiskBytes = disk.FreeBytes,
            RunningWorkloads = managed.Count,
            Draining = persisted.Draining,
            CertificateExpiresAt = _identity?.NotAfter,
        }, ct);

        if (verdict.State is NodeHealthState.Degraded or NodeHealthState.Unhealthy)
            log.LogWarning("Node health is {State}: {Reasons}.", verdict.State, string.Join("; ", verdict.Reasons));
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down mid-wait is the normal way this loop ends.
        }
    }
}
