using System.Net.WebSockets;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Deployments;
using Harbora.Infrastructure.Terminals;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>
/// A shell inside a running container.
///
/// The widest door the panel has, and the last thing built here for that reason. A shell in an
/// application's container is its filesystem, its environment — which holds its database password —
/// and its network. Every decision about who may open one is in <see cref="TerminalAccess"/>; this
/// controller carries bytes between a WebSocket and docker, and writes down that it happened.
///
/// Three things are non-negotiable and none of them is optional configuration:
/// <list type="bullet">
///   <item>Both the page and the socket ask the same rule. A socket that checked less than the page
///   would be the real endpoint, and the page would be decoration.</item>
///   <item>Opening and closing are audited, with who and for how long. A terminal nobody can prove
///   was opened is worse than no terminal.</item>
///   <item>The command is a constant. Accepting one over the wire would make this arbitrary exec
///   with a terminal drawn on it.</item>
/// </list>
/// </summary>
[Authorize]
[Route("apps/{id:guid}/terminal")]
public sealed class TerminalController(
    HarboraDbContext db,
    IServerEngineFactory engines,
    IAuditLogger audit,
    Harbora.Infrastructure.Security.ProjectAccessService access,
    IOptions<TerminalFeatureOptions> features,
    ISystemClock clock,
    ILogger<TerminalController> logger,
    ICurrentUser currentUser) : Controller
{
    /// <summary>What is read from the shell in one go. A screenful of escape sequences is smaller.</summary>
    private const int BufferBytes = 16 * 1024;

    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();
    private static bool IsFa =>
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(Guid id, CancellationToken ct)
    {
        var target = await ResolveAsync(id, ct);

        // The feature being off means the page does not exist, not that it exists and says no.
        if (target.Refusal == TerminalRefusal.FeatureOff || target.App is null) return NotFound();
        if (target.Refusal == TerminalRefusal.NotAllowed) return NotFound();

        ViewData["Title"] = target.App.Name;
        ViewBag.App = target.App;
        ViewBag.Refusal = target.Refusal;
        return View();
    }

    /// <summary>
    /// The socket. Every check the page made is made again here, because this is the endpoint that
    /// actually opens a shell and a caller does not have to have visited the page first.
    /// </summary>
    [HttpGet("ws")]
    public async Task Socket(Guid id, int cols = 80, int rows = 24, CancellationToken ct = default)
    {
        if (!HttpContext.WebSockets.IsWebSocketRequest)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var target = await ResolveAsync(id, ct);
        if (target.Refusal != TerminalRefusal.None || target.App is null || target.ContainerId is null)
        {
            HttpContext.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var app = target.App;
        using var socket = await HttpContext.WebSockets.AcceptWebSocketAsync();

        var startedAt = clock.UtcNow;
        await audit.LogAsync("app.terminal_opened", "app", app.Id.ToString(), ClientIp, ct: ct);

        IContainerExec? exec = null;
        try
        {
            var docker = await engines.ResolveAsync(app.ServerId, ct);
            exec = await docker.ExecAsync(target.ContainerId, TerminalAccess.Command, cols, rows, ct);

            await PumpAsync(socket, exec, startedAt, ct);
        }
        catch (NotSupportedException)
        {
            // The engine said it cannot. Said out loud rather than left as a socket that never
            // carries a byte, which is indistinguishable from a shell with nothing to say.
            await CloseAsync(socket, "This node does not offer a terminal.", ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "The terminal session for app {App} ended with an error.", app.Id);
            await CloseAsync(socket, "The terminal session ended.", ct);
        }
        finally
        {
            if (exec is not null) await exec.DisposeAsync();

            var seconds = (int)(clock.UtcNow - startedAt).TotalSeconds;
            await audit.LogAsync("app.terminal_closed", "app", app.Id.ToString(), ClientIp,
                metadataJson: System.Text.Json.JsonSerializer.Serialize(new { seconds }),
                ct: CancellationToken.None);
        }
    }

    /// <summary>
    /// Bytes both ways until one side stops, or until the session has been idle too long.
    ///
    /// The two directions run as separate loops because they block independently: a shell producing
    /// nothing must not stop keystrokes from arriving, and a person typing nothing must not stop
    /// output from being delivered.
    /// </summary>
    private async Task PumpAsync(
        WebSocket socket, IContainerExec exec, DateTimeOffset startedAt, CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var lastActivity = clock.UtcNow;

        // Guarded because both loops write it and the watchdog reads it.
        var activityLock = new object();
        void Touch() { lock (activityLock) lastActivity = clock.UtcNow; }
        DateTimeOffset LastActivity() { lock (activityLock) return lastActivity; }

        var toContainer = Task.Run(async () =>
        {
            var buffer = new byte[BufferBytes];
            while (!stopping.IsCancellationRequested)
            {
                var received = await socket.ReceiveAsync(buffer, stopping.Token);
                if (received.MessageType == WebSocketMessageType.Close) break;

                Touch();

                // A resize arrives as a text frame the browser sends on its own; anything else is
                // keystrokes and goes through untouched. Keystrokes are binary frames precisely so
                // that a person typing the resize message is not mistaken for the browser sending
                // one.
                if (received.MessageType == WebSocketMessageType.Text)
                {
                    ApplyResize(exec, buffer.AsSpan(0, received.Count), stopping.Token);
                    continue;
                }

                await exec.WriteAsync(buffer.AsMemory(0, received.Count), stopping.Token);
            }
        }, stopping.Token);

        var toBrowser = Task.Run(async () =>
        {
            var buffer = new byte[BufferBytes];
            while (!stopping.IsCancellationRequested)
            {
                var read = await exec.ReadAsync(buffer, stopping.Token);
                if (read == 0) break;   // the shell exited

                Touch();
                await socket.SendAsync(buffer.AsMemory(0, read),
                    WebSocketMessageType.Binary, endOfMessage: true, stopping.Token);
            }
        }, stopping.Token);

        var watchdog = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stopping.Token);
                if (TerminalAccess.ShouldClose(startedAt, LastActivity(), clock.UtcNow)) break;
            }
        }, stopping.Token);

        await Task.WhenAny(toContainer, toBrowser, watchdog);
        await stopping.CancelAsync();

        if (socket.State == WebSocketState.Open)
            await CloseAsync(socket, "The session ended.", CancellationToken.None);
    }

    /// <summary>
    /// A resize message: <c>{"cols":120,"rows":40}</c>. Anything unreadable is dropped rather than
    /// guessed at — a wrongly-sized terminal is a nuisance, and a session ended by a malformed
    /// frame is a lost one.
    /// </summary>
    private static void ApplyResize(IContainerExec exec, ReadOnlySpan<byte> payload, CancellationToken ct)
    {
        int columns, rows;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Encoding.UTF8.GetString(payload));
            if (node?["cols"]?.GetValue<int>() is not { } c || node["rows"]?.GetValue<int>() is not { } r)
                return;
            (columns, rows) = (c, r);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException)
        {
            return;
        }

        var (safeColumns, safeRows) = TerminalAccess.Size(columns, rows);
        _ = exec.ResizeAsync(safeColumns, safeRows, ct);
    }

    private static async Task CloseAsync(WebSocket socket, string reason, CancellationToken ct)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, ct); }
        catch (WebSocketException) { }
    }

    private sealed record Target(
        Harbora.Domain.Apps.App? App, string? ContainerId, TerminalRefusal Refusal);

    /// <summary>
    /// The one place the decision is made, so the page and the socket cannot disagree about it.
    /// </summary>
    private async Task<Target> ResolveAsync(Guid id, CancellationToken ct)
    {
        if (!features.Value.Terminal) return new Target(null, null, TerminalRefusal.FeatureOff);

        var app = await db.Apps
            .Include(a => a.Deployments)
            .FirstOrDefaultAsync(a => a.Id == id && a.WorkspaceId == WorkspaceId, ct);
        if (app is null) return new Target(null, null, TerminalRefusal.NotAllowed);

        // Both, and not either. The volume browser already takes AppsEnv rather than the weaker
        // read capability, because reaching a customer's data deserves the stronger one — and a
        // shell is that plus the ability to stop what is running, which is AppsOperate. A door this
        // wide should not be openable by a role that was only ever given half of it.
        var mayManage = await access.CanTouchAppAsync(app.Id, Capabilities.AppsEnv, ct)
                     && await access.CanTouchAppAsync(app.Id, Capabilities.AppsOperate, ct);

        var isLocal = await db.Servers.AnyAsync(s => s.Id == app.ServerId && s.IsLocal, ct);

        // Only looked up once it is established that the caller may manage this application: which
        // containers are running is information about somebody's application.
        string? containerId = null;
        if (mayManage && isLocal)
        {
            try
            {
                var docker = await engines.ResolveAsync(app.ServerId, ct);
                var containers = await docker.ListContainersAsync(null, ct);
                // Same legacy bridge RetireOldContainersAsync uses: a container with no workspace
                // label is only "mine" when nothing else on the platform could hold this slug.
                var slugExclusive = !await db.Apps.IgnoreQueryFilters()
                    .AnyAsync(a => a.Slug == app.Slug && a.WorkspaceId != app.WorkspaceId, ct);
                containerId = DeploymentPlanning.CurrentContainerId(containers, app.WorkspaceId, app.Slug, slugExclusive);
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "The containers of app {App} could not be listed.", app.Id);
            }
        }

        return new Target(app, containerId,
            TerminalAccess.Decide(featureEnabled: true, mayManage, isLocal, containerId is not null));
    }
}
