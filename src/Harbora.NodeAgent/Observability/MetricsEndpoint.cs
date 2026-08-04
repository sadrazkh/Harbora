using System.Net;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Harbora.NodeAgent.Observability;

/// <summary>
/// A loopback-only HTTP endpoint serving <c>/metrics</c> and <c>/healthz</c>.
///
/// <para>
/// This is the one socket the agent listens on, and it is bound to <c>127.0.0.1</c> — enforced in
/// configuration validation, not merely defaulted. The promise that installing a node opens no
/// inbound port would be worthless if a config typo could quietly break it, and these numbers
/// describe the customer's machine in enough detail to be worth not publishing.
/// </para>
/// </summary>
public sealed class MetricsEndpoint(
    IOptions<NodeAgentOptions> options,
    NodeMetrics metrics,
    ILogger<MetricsEndpoint> log) : BackgroundService
{
    private readonly MetricsOptions _options = options.Value.Metrics;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            log.LogInformation("Metrics endpoint disabled by configuration.");
            return;
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://{_options.BindAddress}:{_options.Port}/");

        try
        {
            listener.Start();
        }
        catch (HttpListenerException e)
        {
            // Not fatal. A node that cannot expose metrics is still a node that can run workloads,
            // and refusing to start over a diagnostics port would be a self-inflicted outage.
            log.LogWarning(e, "Could not bind the metrics endpoint on {Address}:{Port}; continuing without it.",
                _options.BindAddress, _options.Port);
            return;
        }

        log.LogInformation("Metrics available at http://{Address}:{Port}/metrics.", _options.BindAddress, _options.Port);

        stoppingToken.Register(() =>
        {
            // Stop() is what unblocks the pending GetContextAsync; cancellation alone does not.
            try { listener.Stop(); } catch (ObjectDisposedException) { }
        });

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            try
            {
                await RespondAsync(context);
            }
            catch (Exception e) when (e is HttpListenerException or IOException)
            {
                log.LogDebug(e, "Metrics client disconnected mid-response.");
            }
        }
    }

    private async Task RespondAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";

        var (status, contentType, body) = path switch
        {
            "/metrics" => (200, "text/plain; version=0.0.4; charset=utf-8", metrics.Render()),
            "/healthz" => (200, "application/json", HealthBody()),
            _ => (404, "text/plain; charset=utf-8", "Not found. Try /metrics or /healthz.\n"),
        };

        var bytes = Encoding.UTF8.GetBytes(body);

        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = bytes.Length;

        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    /// <summary>
    /// Deliberately thin. A local health probe answers "is this process alive and does it think it
    /// is connected" — anything richer belongs on the metrics endpoint, where it is not on the path
    /// of whatever restarts the service.
    /// </summary>
    private string HealthBody()
    {
        var connected = metrics.GaugeValue("harbora_node_channel_connected") > 0;
        var draining = metrics.GaugeValue("harbora_node_draining") > 0;

        return $$"""
        {"status":"ok","version":"{{AgentVersion.Current}}","connected":{{(connected ? "true" : "false")}},"draining":{{(draining ? "true" : "false")}}}
        """;
    }
}
