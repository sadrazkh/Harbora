using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Harbora.NodeAgent.Contracts;

namespace Harbora.NodeAgent.Observability;

/// <summary>
/// The agent's metrics, in Prometheus text format.
///
/// <para>
/// Hand-rolled rather than pulled from a metrics library, because the agent's dependency budget is
/// the thing that keeps it installable on a 1 GB VPS, and this is a few dozen gauges and counters.
/// The endpoint that serves them is loopback-only: these numbers describe a customer's node and
/// belong to whoever is already on the box.
/// </para>
/// </summary>
public sealed class NodeMetrics(TimeProvider clock)
{
    private readonly ConcurrentDictionary<string, double> _gauges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, double> _counters = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Histogram> _histograms = new(StringComparer.Ordinal);

    // --- channel ---

    public void ChannelConnected(DateTimeOffset at)
    {
        SetGauge("harbora_node_channel_connected", 1);
        SetGauge("harbora_node_channel_connected_since_seconds", at.ToUnixTimeSeconds());
        Increment("harbora_node_channel_connects_total");
    }

    public void ChannelDisconnected() => SetGauge("harbora_node_channel_connected", 0);

    // --- identity ---

    public void CertificateExpiry(DateTimeOffset notAfter)
    {
        SetGauge("harbora_node_certificate_expiry_timestamp_seconds", notAfter.ToUnixTimeSeconds());
        SetGauge("harbora_node_certificate_remaining_seconds", (notAfter - clock.GetUtcNow()).TotalSeconds);
    }

    public void CertificateRotated() => Increment("harbora_node_certificate_rotations_total");

    // --- health ---

    public void Health(HealthVerdict verdict)
    {
        // One gauge per state rather than an enum-valued gauge: alerting on "state == 3" requires
        // knowing what 3 means, and that knowledge lives in a different repository from the alert.
        foreach (var state in Enum.GetValues<NodeHealthState>())
            SetGauge($"harbora_node_health{{state=\"{Name(state)}\"}}", verdict.State == state ? 1 : 0);

        SetGauge("harbora_node_disk_pressure", verdict.DiskPressure ? 1 : 0);
        SetGauge("harbora_node_memory_pressure", verdict.MemoryPressure ? 1 : 0);
        SetGauge("harbora_node_cpu_pressure", verdict.CpuPressure ? 1 : 0);
        SetGauge("harbora_node_certificate_expiring_soon", verdict.CertificateExpiringSoon ? 1 : 0);
    }

    public void RunningWorkloads(int count) => SetGauge("harbora_node_workloads_running", count);

    public void Draining(bool draining) => SetGauge("harbora_node_draining", draining ? 1 : 0);

    // --- commands ---

    public void CommandCompleted(string command, CommandStatus status, long durationMs)
    {
        Increment($"harbora_node_commands_total{{command=\"{command}\",status=\"{Name(status)}\"}}");
        Observe($"harbora_node_command_duration_ms{{command=\"{command}\"}}", durationMs);
    }

    // --- deployments ---

    public void DeploymentCompleted(bool succeeded, bool rolledBack, long deployMs, long pullMs)
    {
        Increment(succeeded
            ? "harbora_node_deployments_succeeded_total"
            : "harbora_node_deployments_failed_total");

        if (rolledBack) Increment("harbora_node_deployments_rolled_back_total");

        Observe("harbora_node_deployment_duration_ms", deployMs);
        if (pullMs > 0) Observe("harbora_node_image_pull_duration_ms", pullMs);
    }

    public void ContainerStateChanged(string workloadId, string state)
    {
        _ = workloadId; // Not a label: workload ids are unbounded and would explode the series count.
        Increment($"harbora_node_container_state_changes_total{{state=\"{state}\"}}");
    }

    // --- database access & tunnels ---

    public void ActiveGrants(int count) => SetGauge("harbora_node_database_grants_active", count);

    public void GrantCreated(string engine, DatabaseAccessMode mode) =>
        Increment($"harbora_node_database_grants_created_total{{engine=\"{engine}\",mode=\"{Name(mode)}\"}}");

    public void GrantEnded(string engine, string reason) =>
        Increment($"harbora_node_database_grants_ended_total{{engine=\"{engine}\",reason=\"{reason}\"}}");

    public void ActiveTunnels(int count) => SetGauge("harbora_node_tunnels_active", count);

    public void TunnelStatus(TunnelStatus status, int count) =>
        SetGauge($"harbora_node_tunnel_status{{status=\"{Name(status)}\"}}", count);

    // --- updates ---

    public void AgentUpdate(AgentUpdateOutcome outcome) =>
        Increment($"harbora_node_agent_updates_total{{outcome=\"{Name(outcome)}\"}}");

    public void AgentUpdateInProgress(bool running) => SetGauge("harbora_node_agent_update_in_progress", running ? 1 : 0);

    // --- rendering ---

    public void SetGauge(string name, double value) => _gauges[name] = value;

    public void Increment(string name, double by = 1) =>
        _counters.AddOrUpdate(name, by, (_, current) => current + by);

    public void Observe(string name, double value) =>
        _histograms.GetOrAdd(name, _ => new Histogram()).Add(value);

    public double GaugeValue(string name) => _gauges.GetValueOrDefault(name);
    public double CounterValue(string name) => _counters.GetValueOrDefault(name);

    /// <summary>Prometheus exposition text for everything recorded so far.</summary>
    public string Render()
    {
        var builder = new StringBuilder(4096);

        builder.Append("# HELP harbora_node_info Agent build information.\n");
        builder.Append("# TYPE harbora_node_info gauge\n");
        builder.Append(CultureInfo.InvariantCulture,
            $"harbora_node_info{{version=\"{AgentVersion.Current}\",protocol=\"{NodeContract.ProtocolVersion}\"}} 1\n");

        foreach (var (name, value) in _gauges.OrderBy(g => g.Key, StringComparer.Ordinal))
            builder.Append(CultureInfo.InvariantCulture, $"{name} {Format(value)}\n");

        foreach (var (name, value) in _counters.OrderBy(c => c.Key, StringComparer.Ordinal))
            builder.Append(CultureInfo.InvariantCulture, $"{name} {Format(value)}\n");

        foreach (var (name, histogram) in _histograms.OrderBy(h => h.Key, StringComparer.Ordinal))
        {
            var (count, sum, max) = histogram.Snapshot();
            var (baseName, labels) = SplitLabels(name);

            builder.Append(CultureInfo.InvariantCulture, $"{baseName}_count{labels} {Format(count)}\n");
            builder.Append(CultureInfo.InvariantCulture, $"{baseName}_sum{labels} {Format(sum)}\n");
            builder.Append(CultureInfo.InvariantCulture, $"{baseName}_max{labels} {Format(max)}\n");
        }

        return builder.ToString();
    }

    private static string Format(double value) =>
        value.ToString("0.######", CultureInfo.InvariantCulture);

    /// <summary>Split <c>name{labels}</c> so the histogram suffix lands before the label set, as Prometheus requires.</summary>
    private static (string Name, string Labels) SplitLabels(string series)
    {
        var brace = series.IndexOf('{');
        return brace < 0 ? (series, string.Empty) : (series[..brace], series[brace..]);
    }

    private static string Name<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString()!);

    private sealed class Histogram
    {
        private readonly Lock _gate = new();
        private long _count;
        private double _sum;
        private double _max;

        public void Add(double value)
        {
            lock (_gate)
            {
                _count++;
                _sum += value;
                if (value > _max) _max = value;
            }
        }

        public (long Count, double Sum, double Max) Snapshot()
        {
            lock (_gate) { return (_count, _sum, _max); }
        }
    }
}
