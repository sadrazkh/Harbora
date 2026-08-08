using System.Text.Json;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Nodes;
using Harbora.Infrastructure.Nodes;
using Harbora.NodeAgent.Contracts;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// The fleet screen: which nodes exist, what they are doing, and the three things an operator does
/// to one — drain it, update it, or withdraw its credential.
///
/// <para>
/// Reading is gated by the same capability as managing, for the reason
/// <see cref="ServersController"/> already learned: a list of hostnames, core counts and runtime
/// versions is not a tenant's to read, and an open route is one typed URL away from being read.
/// </para>
/// </summary>
[Authorize(Policy = Capabilities.ServersManage)]
[Route("nodes")]
public sealed class NodesController(
    HarboraDbContext db,
    NodeEnrollmentService enrollment,
    NodeCommandService commands,
    NodeChannelRegistry registry,
    NodeServerLink serverLink,
    NodeIngressRegistry ingress,
    NodeIngressRouter ingressRouter,
    IOptions<NodeAgentControlPlaneOptions> options,
    Harbora.Application.Abstractions.ICurrentUser currentUser,
    TimeProvider clock,
    ILogger<NodesController> log) : Controller
{
    private readonly NodeAgentControlPlaneOptions _options = options.Value;

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Nodes";
        return View(await BuildListAsync(ct));
    }

    [HttpGet("{nodeId}")]
    public async Task<IActionResult> Detail(string nodeId, CancellationToken ct)
    {
        // IgnoreQueryFilters: nodes are platform infrastructure and carry no workspace, so a
        // filtered read would find nothing and every node would 404 for everyone.
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);
        if (node is null) return NotFound();

        var capabilities = Deserialize<NodeCapabilities>(node.CapabilitiesJson);

        var commandRows = await db.NodeCommands.IgnoreQueryFilters()
            .Where(c => c.NodeId == nodeId)
            .OrderByDescending(c => c.IssuedAt)
            .Take(30)
            .Select(c => new NodeCommandRow(
                c.CommandId, c.Command, c.Status, c.IssuedAt, c.CompletedAt,
                c.ErrorCode, c.ErrorMessage, c.IdempotentReplay, c.IssuedByName))
            .ToListAsync(ct);

        var eventRows = await db.NodeEvents.IgnoreQueryFilters()
            .Where(e => e.NodeId == nodeId)
            .OrderByDescending(e => e.At)
            .Take(40)
            .Select(e => new NodeEventRow(e.Kind, e.Message, e.WorkloadId, e.At))
            .ToListAsync(ct);

        ViewData["Title"] = node.Name;

        return View(new NodeDetailViewModel(
            ToRow(node),
            Deserialize<List<string>>(node.GrantedScopesJson) ?? [],
            capabilities?.SupportedCommands ?? [],
            capabilities?.SupportedDatabaseEngines ?? [],
            capabilities?.PrivilegedModeEnabled ?? false,
            capabilities?.SupportsIsolatedDockerWorkspace ?? false,
            node.KernelVersion,
            node.OsVersion,
            node.MachineFingerprint,
            node.EnrolledAt,
            node.LastConnectedAt,
            node.CertificateGeneration,
            node.RevokedReason,
            commandRows,
            eventRows,
            clock.GetUtcNow(),
            await BuildSchedulingAsync(node, ct)));
    }

    /// <summary>
    /// Make this node a scheduling target.
    ///
    /// <para>
    /// Only needed on an install that turned auto-registration off, or for a node an operator
    /// detached earlier — an enrolled node normally acquires its Server row on its first connect.
    /// </para>
    /// </summary>
    [HttpPost("{nodeId}/attach")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Attach(string nodeId, CancellationToken ct)
    {
        var serverId = await serverLink.AttachAsync(nodeId, ct);

        TempData[serverId is null ? "Error" : "Message"] = serverId is null
            ? (Fa() ? "نودی با این شناسه نیست." : "No such node.")
            : (Fa()
                ? "این نود حالا هدف زمان‌بندی است و برنامه‌ها می‌توانند روی آن مستقر شوند."
                : "This node is now a scheduling target and apps can be placed on it.");

        return RedirectToAction(nameof(Detail), new { nodeId });
    }

    /// <summary>
    /// Stop scheduling onto this node without touching its enrollment.
    ///
    /// <para>
    /// Refused while anything is placed on it. The alternative — removing the Server row under a
    /// running app — leaves the panel showing the app as deployed with no way to reach, stop or
    /// delete it.
    /// </para>
    /// </summary>
    /// <summary>
    /// Switch a node between direct routing and its ingress tunnel.
    ///
    /// <para>
    /// The row is written only after the node confirms, and only for the direction that can fail.
    /// Turning the tunnel on and recording it before the node agreed would point every route on the
    /// node at a tunnel that does not exist — which looks exactly like the NAT problem this is here
    /// to solve, and would be blamed on it.
    /// </para>
    /// </summary>
    [HttpPost("{nodeId}/ingress")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Ingress(string nodeId, bool tunnel, CancellationToken ct)
    {
        var node = await db.Nodes.IgnoreQueryFilters().FirstOrDefaultAsync(n => n.NodeId == nodeId, ct);

        if (node is null)
        {
            TempData["Error"] = Fa() ? "نودی با این شناسه نیست." : "No such node.";
            return RedirectToAction(nameof(Index));
        }

        var outcome = await commands.SendAsync(
            nodeId, NodeCommands.ConfigureIngress,
            new ConfigureIngressRequest { Enabled = tunnel },
            idempotencyKey: $"ingress:{nodeId}:{(tunnel ? "on" : "off")}",
            reason: tunnel ? "route through the ingress tunnel" : "route directly",
            sourceIp: HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: ct);

        if (!outcome.Succeeded)
        {
            TempData["Error"] = outcome.ErrorMessage ?? (Fa()
                ? "نود درخواست را نپذیرفت؛ مسیر تغییر نکرد."
                : "The node declined; routing is unchanged.");

            return RedirectToAction(nameof(Detail), new { nodeId });
        }

        node.IngressMode = tunnel ? NodeIngressMode.Tunnel : NodeIngressMode.Direct;
        await db.SaveChangesAsync(ct);

        log.LogWarning(
            "Node {NodeId} now routes {Mode}; its apps need a redeploy before the change reaches their routes.",
            nodeId, tunnel ? "through its ingress tunnel" : "directly");

        // Existing routes still name the old upstream. Saying so is the difference between an
        // operator redeploying and an operator wondering why nothing changed.
        TempData["Message"] = tunnel
            ? (Fa()
                ? "این نود از این پس از طریق تونل ورودی سرویس می‌دهد. برنامه‌های موجود تا استقرار مجدد، مسیر قبلی را نگه می‌دارند."
                : "This node now serves through its ingress tunnel. Existing apps keep the old route until they are redeployed.")
            : (Fa()
                ? "این نود از این پس مستقیم سرویس می‌دهد. برنامه‌های موجود تا استقرار مجدد، مسیر قبلی را نگه می‌دارند."
                : "This node now serves directly. Existing apps keep the old route until they are redeployed.");

        return RedirectToAction(nameof(Detail), new { nodeId });
    }

    [HttpPost("{nodeId}/detach")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Detach(string nodeId, CancellationToken ct)
    {
        var result = await serverLink.DetachAsync(nodeId, ct);

        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (Fa()
                ? "زمان‌بندی روی این نود متوقف شد. نود همچنان ثبت‌شده و قابل فرمان است."
                : "Scheduling onto this node is off. It stays enrolled and commandable.")
            : result.Reason;

        return RedirectToAction(nameof(Detail), new { nodeId });
    }

    /// <summary>
    /// Mint an enrollment token and show it once.
    ///
    /// <para>
    /// Through TempData rather than a redirect parameter: a token in a query string is a token in
    /// the browser history, the access log and whatever the operator pastes into a chat when asking
    /// for help.
    /// </para>
    /// </summary>
    [HttpPost("tokens")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MintToken(
        string? nodeName, string? region, string? environment, int? lifetimeMinutes, CancellationToken ct)
    {
        try
        {
            var token = await enrollment.MintTokenAsync(
                currentUser.UserId ?? Guid.Empty,
                Trimmed(nodeName), Trimmed(region), Trimmed(environment),
                labels: null, scopes: null,
                lifetimeMinutes is > 0 ? TimeSpan.FromMinutes(lifetimeMinutes.Value) : null,
                ct);

            TempData["NodeToken"] = token.Token;
            TempData["NodeTokenInstall"] = InstallCommand(token.Token, Trimmed(nodeName));
            TempData["Message"] = Fa()
                ? $"توکن ساخته شد. تا {token.ExpiresAt.LocalDateTime:HH:mm} معتبر است و فقط یک بار قابل استفاده."
                : $"Token created. Valid until {token.ExpiresAt.LocalDateTime:HH:mm}, single use.";
        }
        catch (ArgumentException e)
        {
            TempData["Error"] = e.Message;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{nodeId}/drain")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Drain(string nodeId, bool drain, bool stopWorkloads, string? reason, CancellationToken ct)
    {
        var request = new DrainNodeRequest
        {
            Drain = drain,
            StopWorkloads = stopWorkloads,
            TimeoutSeconds = 300,
            Reason = Trimmed(reason),
        };

        await SendAsync(nodeId, NodeCommands.DrainNode, request,
            $"drain:{nodeId}:{drain}:{stopWorkloads}", request.Reason,
            drain
                ? (Fa() ? "نود در حال تخلیه است." : "The node is draining.")
                : (Fa() ? "نود دوباره در سرویس است." : "The node is back in service."),
            ct);

        return RedirectToAction(nameof(Detail), new { nodeId });
    }

    [HttpPost("{nodeId}/update-agent")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAgent(
        string nodeId, string targetVersion, string downloadUrl, string sha256, bool drainFirst, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetVersion) || string.IsNullOrWhiteSpace(downloadUrl) || string.IsNullOrWhiteSpace(sha256))
        {
            TempData["Error"] = Fa()
                ? "نسخه، آدرس دانلود و SHA-256 هر سه لازم‌اند."
                : "Version, download URL and SHA-256 are all required.";
            return RedirectToAction(nameof(Detail), new { nodeId });
        }

        if (!downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            TempData["Error"] = Fa()
                ? "آدرس دانلود باید https باشد."
                : "The download URL must be https.";
            return RedirectToAction(nameof(Detail), new { nodeId });
        }

        var request = new AgentUpdateRequest
        {
            TargetVersion = targetVersion.Trim(),
            DownloadUrl = downloadUrl.Trim(),
            Sha256 = sha256.Trim().ToLowerInvariant(),
            DrainFirst = drainFirst,
        };

        log.LogWarning("Updating node {NodeId} to agent {Version} from the panel.", nodeId, request.TargetVersion);

        await SendAsync(nodeId, NodeCommands.UpdateAgent, request,
            $"agent-update:{nodeId}:{request.TargetVersion}", $"update to {request.TargetVersion}",
            Fa()
                ? "به‌روزرسانی آغاز شد. نود پس از راه‌اندازی مجدد نسخه‌ی خود را گزارش می‌کند."
                : "Update started. The node reports its version after it restarts.",
            ct);

        return RedirectToAction(nameof(Detail), new { nodeId });
    }

    /// <summary>
    /// Withdraw a node's credential.
    ///
    /// <para>
    /// Not a command — the node is not asked to cooperate, because a node worth revoking may be one
    /// that stopped answering. The row is what refuses its next connection; closing the live socket
    /// is what ends the current one.
    /// </para>
    /// </summary>
    [HttpPost("{nodeId}/revoke")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Revoke(string nodeId, string? reason, CancellationToken ct)
    {
        var revoked = await enrollment.RevokeAsync(
            nodeId, Trimmed(reason), currentUser.UserId, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);

        if (!revoked)
        {
            TempData["Error"] = Fa() ? "نودی با این شناسه نبود، یا از قبل ابطال شده بود." : "No such node, or it was already revoked.";
            return RedirectToAction(nameof(Index));
        }

        if (registry.Get(nodeId) is { } connection)
            await connection.CloseAsync("credential revoked");

        TempData["Message"] = Fa()
            ? "اعتبارنامه ابطال شد. برای بازگرداندن این ماشین، توکن تازه بسازید."
            : "Credential revoked. Mint a fresh token to re-admit this machine.";

        return RedirectToAction(nameof(Index));
    }

    // --- internals ---

    /// <summary>
    /// What the scheduler sees when it considers this node.
    ///
    /// <para>
    /// Read from the Server row rather than from the node's own inventory: capacity is committed
    /// against the Server, and showing the node's raw totals here would report headroom the
    /// scheduler does not believe in.
    /// </para>
    /// </summary>
    private async Task<NodeSchedulingViewModel> BuildSchedulingAsync(Node node, CancellationToken ct)
    {
        var supported = Deserialize<NodeCapabilities>(node.CapabilitiesJson)?.SupportsHttpIngressTunnel ?? false;

        NodeSchedulingViewModel Unattached() => new(
            null, string.Empty, string.Empty, ServerStatus.Unknown, 0, 0, 0, 0, 0, 0,
            _options.AutoRegisterAsServer,
            node.IngressMode, supported, ingress.IsConnected(node.NodeId), 0, ingressRouter.IngressHost);

        if (node.ServerId is not { } serverId) return Unattached();

        var server = await db.Servers.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == serverId, ct);

        if (server is null) return Unattached();

        // Platform-wide counts, not workspace-scoped: a node may hold another workspace's app, and
        // an operator deciding whether to detach needs to know that it does.
        var apps = await db.Apps.IgnoreQueryFilters().Where(a => a.ServerId == serverId)
            .Select(a => new { a.MemoryLimitBytes, a.CpuLimit })
            .ToListAsync(ct);

        var services = await db.ManagedServices.IgnoreQueryFilters().CountAsync(s => s.ServerId == serverId, ct);

        return new NodeSchedulingViewModel(
            server.Id,
            server.Hostname,
            server.Pool,
            server.Status,
            apps.Count,
            services,
            server.TotalMemoryBytes > 0 ? (long)(server.TotalMemoryBytes * (1 - server.ReservedMemoryRatio)) : 0,
            apps.Sum(a => a.MemoryLimitBytes),
            server.CpuCores > 0 ? server.CpuCores * Math.Max(1, server.CpuOvercommitFactor) : 0,
            apps.Sum(a => a.CpuLimit),
            _options.AutoRegisterAsServer,
            node.IngressMode,
            supported,
            ingress.IsConnected(node.NodeId),
            ingress.Bindings().Count(b => b.NodeId == node.NodeId),
            ingressRouter.IngressHost);
    }

    private async Task<NodeListViewModel> BuildListAsync(CancellationToken ct)
    {
        var nodes = await db.Nodes.IgnoreQueryFilters()
            .OrderBy(n => n.Name)
            .ToListAsync(ct);

        var tokens = await db.NodeEnrollmentTokens.IgnoreQueryFilters()
            .OrderByDescending(t => t.CreatedAt)
            .Take(10)
            .Select(t => new EnrollmentTokenRow(t.Prefix, t.ExpiresAt, t.UsedAt, t.UsedByNodeId, t.RevokedAt, t.NodeNameHint))
            .ToListAsync(ct);

        return new NodeListViewModel(
            nodes.Select(ToRow).ToList(),
            tokens,
            clock.GetUtcNow(),
            EnrollmentUrl,
            TempData["NodeToken"] as string,
            TempData["NodeTokenInstall"] as string);
    }

    private NodeRow ToRow(Node node) => new(
        node.NodeId,
        node.Name,
        node.Status,
        node.Health,
        registry.IsConnected(node.NodeId),
        node.Draining,
        node.IsRevoked,
        node.AgentVersion,
        node.Region,
        node.Environment,
        node.Architecture,
        node.OsName,
        node.ContainerRuntimeVersion,
        node.CpuCores,
        node.TotalMemoryBytes,
        node.FreeMemoryBytes,
        node.TotalDiskBytes,
        node.FreeDiskBytes,
        node.Load1,
        node.RunningWorkloads,
        node.ActiveDatabaseGrants,
        node.LastHeartbeatAt,
        node.CertificateNotAfter,
        Deserialize<List<string>>(node.IpAddressesJson) ?? []);

    private async Task SendAsync(
        string nodeId, string command, object payload, string idempotencyKey,
        string? reason, string successMessage, CancellationToken ct)
    {
        try
        {
            var outcome = await commands.SendAsync(
                nodeId, command, payload, idempotencyKey, reason,
                sourceIp: HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

            if (outcome.Succeeded) TempData["Message"] = successMessage;
            else TempData["Error"] = outcome.ErrorMessage ?? (Fa() ? "نود درخواست را نپذیرفت." : "The node declined the request.");
        }
        catch (NodeNotFoundException)
        {
            TempData["Error"] = Fa() ? "نودی با این شناسه نیست." : "No such node.";
        }
        catch (NodeNotConnectedException)
        {
            // Worth its own message: on a single-instance install this means the node is offline; on
            // several replicas it may be online and attached elsewhere.
            TempData["Error"] = Fa()
                ? "نود به این نمونه‌ی پنل وصل نیست."
                : "The node is not connected to this panel instance.";
        }
    }

    /// <summary>
    /// Where a node <em>enrolls</em>: this panel's own URL, taken from the request that asked for
    /// the token.
    ///
    /// <para>
    /// Deliberately not <c>NodeAgent:PublicUrl</c>, which it used to be. That setting names the node
    /// channel's own host, and every connection to that host must present a client certificate — a
    /// node has none until enrollment has produced one, so it could not complete the handshake.
    /// Enrollment is served on the panel's host; the enrollment response then hands the node
    /// <c>PublicUrl</c>, and it uses that for the channel and for renewals from then on.
    /// </para>
    /// </summary>
    private string EnrollmentUrl => $"{Request.Scheme}://{Request.Host}";

    private string InstallCommand(string token, string? nodeName) =>
        NodeInstallCommand.For(EnrollmentUrl, token, nodeName);

    private static bool Fa() =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json, NodeContract.Json); }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The one-line install command the panel shows beside a freshly minted enrollment token.
///
/// <para>
/// Pure and separate from the controller so the property that matters can be executed in a test
/// rather than asserted about: <c>--control-plane</c> must name the URL enrollment is served on.
/// Point it at the node channel's mTLS host instead and every operator who copies it gets a TLS
/// handshake failure, because that host demands a certificate the node does not have yet.
/// </para>
/// </summary>
public static class NodeInstallCommand
{
    public const string ScriptUrl =
        "https://raw.githubusercontent.com/sadrazkh/Harbora/master/deploy/node-agent/install.sh";

    public static string For(string enrollmentUrl, string token, string? nodeName) =>
        $"curl -fsSL {ScriptUrl} | \\\n" +
        $"  bash -s -- --control-plane {enrollmentUrl.TrimEnd('/')} --token {token} " +
        $"--name {nodeName ?? "<node-name>"}";
}
