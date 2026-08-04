using System.Diagnostics;
using System.Security.Cryptography;
using Harbora.NodeAgent.Auditing;
using Harbora.NodeAgent.Contracts;
using Harbora.NodeAgent.Observability;
using Harbora.NodeAgent.Runtime;
using Harbora.NodeAgent.State;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Updates;

/// <summary>Downloads a release artifact. An interface so the update path is testable offline.</summary>
public interface IUpdateDownloader
{
    Task DownloadAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Asks the init system to restart this service.</summary>
public interface IServiceController
{
    Task RestartAsync(CancellationToken ct);

    /// <summary>Path of the executable currently running as the agent.</summary>
    string ExecutablePath { get; }
}

/// <summary>What an in-flight update left behind, so the next start can judge whether it worked.</summary>
public sealed record PendingUpdate
{
    public required string TargetVersion { get; init; }
    public required string PreviousVersion { get; init; }
    public required string PreviousBinaryPath { get; init; }
    public required string ExecutablePath { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public required int VerifyTimeoutSeconds { get; init; }
}

/// <summary>
/// Replaces the agent binary and puts the old one back when the new one does not come up.
///
/// <para>
/// Verification has to happen after the restart, because "does the new binary work" is a question
/// only the new binary can answer. So the update leaves a marker on disk, the process restarts, and
/// the version that comes back decides the outcome — either the marker is cleared or the previous
/// binary is restored. An update that leaves a node unreachable is worse than one that never
/// happened, and a node that cannot report is a node nobody can fix remotely.
/// </para>
/// </summary>
public sealed class AgentUpdater(
    IOptions<NodeAgentOptions> options,
    JsonFileStore<PendingUpdate> pending,
    JsonFileStore<NodeState> state,
    IUpdateDownloader downloader,
    IServiceController service,
    DrainCoordinator drain,
    NodeAuditLog audit,
    NodeMetrics metrics,
    INodeEventPublisher events,
    TimeProvider clock,
    ILogger<AgentUpdater> log)
{
    private readonly NodeAgentOptions _options = options.Value;

    /// <summary>
    /// Download, verify, swap and restart. Returns before the restart takes effect — the process is
    /// about to be replaced, so the result frame is sent first and the outcome is confirmed by the
    /// version that comes back.
    /// </summary>
    public async Task<AgentUpdateResult> ApplyAsync(
        AgentUpdateRequest request, IProgress<string>? progress, CancellationToken ct)
    {
        var current = AgentVersion.Current;

        if (AgentVersion.Compare(current, request.TargetVersion) == 0)
            return new AgentUpdateResult
            {
                Outcome = AgentUpdateOutcome.AlreadyCurrent,
                PreviousVersion = current,
                CurrentVersion = current,
                Message = $"Already running {current}.",
            };

        if (!request.DownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return Failure(current, NodeErrorCode.UpdateVerificationFailed,
                "The download URL must be https. An update binary fetched over plain http is a binary anyone on the path can choose.");

        Directory.CreateDirectory(_options.StagingDirectory);
        FilePermissions.RestrictDirectory(_options.StagingDirectory);

        var staged = Path.Combine(_options.StagingDirectory, $"harbora-node-agent-{request.TargetVersion}");

        try
        {
            metrics.AgentUpdateInProgress(true);

            await events.PublishAsync(new NodeEvent
            {
                Kind = NodeEventKinds.AgentUpdateStarted,
                Message = $"Updating the agent from {current} to {request.TargetVersion}",
            }, ct);

            progress?.Report($"Downloading {request.TargetVersion}…");
            await downloader.DownloadAsync(request.DownloadUrl, staged, progress, ct);

            progress?.Report("Verifying the artifact…");

            var actual = await Sha256Async(staged, ct);

            if (!actual.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                // An unverified binary about to be executed as root is the worst thing an update
                // path can do, so this failure is terminal and loud.
                SafeDelete(staged);
                return Failure(current, NodeErrorCode.UpdateVerificationFailed,
                    $"The downloaded artifact hashes to {actual}, not the expected {request.Sha256}. Nothing was installed.");
            }

            // The platform gate sits here, after verification rather than before it: checking an
            // artifact is platform-independent, and putting the gate first would mean the most
            // important check in this file could only ever run on a production node.
            if (!OperatingSystem.IsLinux())
            {
                SafeDelete(staged);
                return Failure(current, NodeErrorCode.UpdateApplyFailed,
                    "Self-update is only supported on Linux, where the agent runs under systemd.");
            }

            MakeExecutable(staged);

            if (request.DrainFirst)
            {
                progress?.Report("Draining before the swap…");
                await drain.DrainAsync(
                    stopWorkloads: false,
                    TimeSpan.FromSeconds(request.DrainTimeoutSeconds),
                    "agent update", ct);
            }

            var backup = _options.StagingDirectory is { Length: > 0 }
                ? Path.Combine(_options.StagingDirectory, $"harbora-node-agent-{current}.previous")
                : service.ExecutablePath + ".previous";

            File.Copy(service.ExecutablePath, backup, overwrite: true);
            MakeExecutable(backup);

            pending.Save(new PendingUpdate
            {
                TargetVersion = request.TargetVersion,
                PreviousVersion = current,
                PreviousBinaryPath = backup,
                ExecutablePath = service.ExecutablePath,
                StartedAt = clock.GetUtcNow(),
                VerifyTimeoutSeconds = request.VerifyTimeoutSeconds,
            });

            // The marker is written before the swap on purpose: a crash between the two leaves a
            // marker with no swap, which the next start resolves harmlessly. The reverse — a swap
            // with no marker — would leave a broken binary with nothing to roll back to.
            File.Move(staged, service.ExecutablePath, overwrite: true);
            MakeExecutable(service.ExecutablePath);

            audit.Write(new NodeAuditEntry
            {
                Action = "agent.update",
                Outcome = "applied",
                Detail = $"{current} → {request.TargetVersion}, sha256 verified",
            });

            log.LogWarning("Agent binary replaced with {Version}; restarting to complete the update.", request.TargetVersion);

            // Detached: the restart kills this process, and awaiting our own death would prevent
            // the result frame from ever being sent.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                await service.RestartAsync(CancellationToken.None);
            }, CancellationToken.None);

            return new AgentUpdateResult
            {
                Outcome = AgentUpdateOutcome.Updated,
                PreviousVersion = current,
                CurrentVersion = request.TargetVersion,
                Message = "The binary was replaced and the service is restarting. The next heartbeat reports the running version.",
            };
        }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            SafeDelete(staged);
            log.LogError(e, "Agent update to {Version} failed.", request.TargetVersion);
            return Failure(current, NodeErrorCode.UpdateDownloadFailed, e.Message);
        }
        finally
        {
            metrics.AgentUpdateInProgress(false);
        }
    }

    /// <summary>
    /// Called at startup. Decides whether the update that was in flight succeeded, and rolls back
    /// when it did not.
    /// </summary>
    public async Task<AgentUpdateResult?> CompletePendingAsync(CancellationToken ct)
    {
        if (pending.Load() is not { } update) return null;

        var running = AgentVersion.Current;

        if (AgentVersion.Compare(running, update.TargetVersion) == 0)
        {
            pending.Delete();
            SafeDelete(update.PreviousBinaryPath);

            state.Update(s => (s ?? new NodeState()) with { PreviousAgentVersion = update.PreviousVersion });

            audit.Write(new NodeAuditEntry
            {
                Action = "agent.update",
                Outcome = "completed",
                Detail = $"{update.PreviousVersion} → {update.TargetVersion}",
            });

            metrics.AgentUpdate(AgentUpdateOutcome.Updated);

            log.LogInformation("Agent update to {Version} completed.", update.TargetVersion);

            await events.PublishAsync(new NodeEvent
            {
                Kind = NodeEventKinds.AgentUpdateCompleted,
                Message = $"Agent updated to {update.TargetVersion}",
            }, ct);

            await drain.UndrainAsync(ct);

            return new AgentUpdateResult
            {
                Outcome = AgentUpdateOutcome.Updated,
                PreviousVersion = update.PreviousVersion,
                CurrentVersion = running,
            };
        }

        // We are running, so the new binary at least starts — but it is not the version that was
        // installed, which means the swap did not take. Put the old one back.
        log.LogError(
            "An update to {Target} was in flight but this process reports {Running}. Restoring {Previous}.",
            update.TargetVersion, running, update.PreviousVersion);

        var restored = TryRestore(update);

        pending.Delete();

        audit.Write(new NodeAuditEntry
        {
            Action = "agent.update",
            Outcome = restored ? "rolled-back" : "rollback-failed",
            Detail = $"target {update.TargetVersion}, running {running}",
        });

        metrics.AgentUpdate(restored ? AgentUpdateOutcome.RolledBack : AgentUpdateOutcome.Failed);

        await events.PublishAsync(new NodeEvent
        {
            Kind = NodeEventKinds.AgentUpdateRolledBack,
            Message = restored
                ? $"Update to {update.TargetVersion} failed; restored {update.PreviousVersion}"
                : $"Update to {update.TargetVersion} failed and the previous binary could not be restored",
        }, ct);

        await drain.UndrainAsync(ct);

        return new AgentUpdateResult
        {
            Outcome = restored ? AgentUpdateOutcome.RolledBack : AgentUpdateOutcome.Failed,
            PreviousVersion = update.PreviousVersion,
            CurrentVersion = running,
            Error = NodeError.From(
                restored ? NodeErrorCode.UpdateRolledBack : NodeErrorCode.UpdateApplyFailed,
                $"The agent came back as {running} rather than {update.TargetVersion}."),
        };
    }

    private bool TryRestore(PendingUpdate update)
    {
        try
        {
            if (!File.Exists(update.PreviousBinaryPath))
            {
                log.LogCritical(
                    "The previous agent binary at {Path} is gone; this node stays on {Running} and must be repaired by hand.",
                    update.PreviousBinaryPath, AgentVersion.Current);
                return false;
            }

            File.Copy(update.PreviousBinaryPath, update.ExecutablePath, overwrite: true);
            MakeExecutable(update.ExecutablePath);

            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                await service.RestartAsync(CancellationToken.None);
            }, CancellationToken.None);

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.LogCritical(e, "Could not restore the previous agent binary. This node needs manual repair.");
            return false;
        }
    }

    internal static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexStringLower(hash);
    }

    private static void MakeExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;

        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
    }

    private static AgentUpdateResult Failure(string current, NodeErrorCode code, string message) => new()
    {
        Outcome = AgentUpdateOutcome.Failed,
        PreviousVersion = current,
        CurrentVersion = current,
        Error = NodeError.From(code, message),
        Message = message,
    };
}

/// <summary>Streams an artifact to disk over HTTPS, refusing anything implausibly large.</summary>
public sealed class HttpUpdateDownloader(ILogger<HttpUpdateDownloader> log) : IUpdateDownloader
{
    /// <summary>A self-contained agent is tens of megabytes; a gigabyte is someone else's file.</summary>
    private const long MaxBytes = 256L * 1024 * 1024;

    public async Task DownloadAsync(string url, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declared && declared > MaxBytes)
            throw new IOException($"The release artifact declares {declared} bytes, beyond the {MaxBytes} limit.");

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var destination = File.Create(destinationPath);

        var buffer = new byte[81920];
        long total = 0;
        int read;

        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;

            if (total > MaxBytes)
                throw new IOException($"The release artifact exceeded {MaxBytes} bytes mid-download.");

            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await destination.FlushAsync(ct);
        log.LogInformation("Downloaded {Bytes} bytes to {Path}.", total, destinationPath);
        progress?.Report($"Downloaded {total / (1024 * 1024)} MiB.");
    }
}

/// <summary>Restarts the agent through systemd.</summary>
public sealed class SystemdServiceController(ILogger<SystemdServiceController> log) : IServiceController
{
    public const string UnitName = "harbora-node-agent.service";

    public string ExecutablePath { get; } =
        Environment.ProcessPath ?? "/usr/local/bin/harbora-node-agent";

    public async Task RestartAsync(CancellationToken ct)
    {
        log.LogWarning("Restarting {Unit}.", UnitName);

        // Fixed argv, no interpolation: nothing a control plane sends reaches this line.
        using var process = Process.Start(new ProcessStartInfo("systemctl")
        {
            ArgumentList = { "restart", UnitName },
            RedirectStandardError = true,
            UseShellExecute = false,
        });

        if (process is null)
        {
            log.LogError("Could not invoke systemctl; the agent will keep running the binary it started with.");
            return;
        }

        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
            log.LogError("systemctl restart exited {Code}: {Error}", process.ExitCode, await process.StandardError.ReadToEndAsync(ct));
    }
}
