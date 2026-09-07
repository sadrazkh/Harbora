using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Domain.Common;
using Harbora.Domain.Servers;
using Harbora.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Web.Controllers;

/// <summary>
/// Manage server nodes. The local node runs the in-process engine; remote nodes run the Harbora
/// Agent and are reached over HTTP with a bearer token (stored encrypted). "Test" pings the agent.
/// </summary>
[Authorize]
[Route("servers")]
public sealed class ServersController(
    HarboraDbContext db,
    IServerEngineFactory engineFactory,
    INodeCapacityService capacity,
    ISecretProtector protector,
    DiskVolumeOrphanReport diskVolumeReport,
    Harbora.Application.Abstractions.ISystemClock clock) : Controller
{
    // Reading is gated too, not just managing. The sidebar has always hidden this from a member
    // without the capability, but the route was open — so the page was one typed URL away, and a
    // list of node hostnames, core counts and Docker versions is not a tenant's to read.
    [HttpGet("")]
    [Authorize(Policy = Capabilities.ServersManage)]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Servers";
        var servers = await db.Servers.OrderByDescending(s => s.IsLocal).ThenBy(s => s.Name).ToListAsync(ct);
        ViewBag.Capacities = (await capacity.GetAllAsync(ct)).ToDictionary(c => c.ServerId);
        return View(servers);
    }

    [HttpPost("add")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.ServersManage)]
    public async Task<IActionResult> Add(string name, string agentEndpoint, string token, bool useMtls, string? clientCertPfxBase64, CancellationToken ct)
    {
        var host = Uri.TryCreate(agentEndpoint, UriKind.Absolute, out var uri) ? uri.Host : agentEndpoint;
        var server = new Server
        {
            Name = string.IsNullOrWhiteSpace(name) ? host : name,
            Hostname = host,
            IsLocal = false,
            AgentEndpoint = agentEndpoint.TrimEnd('/'),
            AgentTokenHash = string.IsNullOrWhiteSpace(token) ? null : protector.Protect(token),
            AgentUseMtls = useMtls,
            AgentClientCertPfx = useMtls && !string.IsNullOrWhiteSpace(clientCertPfxBase64)
                ? protector.Protect(clientCertPfxBase64.Trim()) : null,
            Status = ServerStatus.Unknown
        };
        db.Servers.Add(server);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Test), new { id = server.Id });
    }

    [HttpGet("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null) return NotFound();

        try
        {
            var engine = await engineFactory.ResolveAsync(id, ct);
            var host = await engine.GetHostInfoAsync(ct);
            server.Status = ServerStatus.Online;
            server.CpuCores = host.CpuCores;
            server.TotalMemoryBytes = host.TotalMemoryBytes;
            server.TotalDiskBytes = host.TotalDiskBytes;
            server.DockerVersion = host.DockerVersion;
            server.Architecture = Harbora.Infrastructure.Monitoring.ReportedFact.Keep(
                server.Architecture, host.Architecture);
            server.LastHeartbeatAt = clock.UtcNow;
            TempData["Message"] = $"{server.Name} is online (Docker {host.DockerVersion}).";
        }
        catch (Exception ex)
        {
            server.Status = ServerStatus.Offline;
            TempData["Error"] = $"Could not reach {server.Name}: {ex.Message}";
        }
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// HARBORA-0033's disk-side half: volumes actually on a server's disk with no database row at
    /// all. Read-only, and gated the same as the rest of this controller — this is the same list of
    /// hostnames, core counts and Docker versions that reading the server list itself is gated on.
    /// Plain text on purpose, matching <c>VolumeOrphanReport</c>'s own CLI rendering rather than a
    /// dedicated page: both reports exist to be read once by an operator chasing missing disk space,
    /// not to be a dashboard someone leaves open.
    /// </summary>
    [HttpGet("disk-volume-report")]
    [Authorize(Policy = Capabilities.ServersManage)]
    public async Task<IActionResult> DiskVolumeReport(CancellationToken ct)
    {
        var report = await diskVolumeReport.BuildAsync(ct);
        return Content(DiskVolumeOrphanReport.Render(report), "text/plain");
    }

    [HttpPost("{id:guid}/remove")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.ServersManage)]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null || server.IsLocal) { TempData["Error"] = "The local server cannot be removed."; return RedirectToAction(nameof(Index)); }

        // Platform-wide on purpose: a node may host other tenants' workloads, and removing it must
        // be blocked by ANY of them — not just the ones this admin's workspace can see.
        if (await db.Apps.IgnoreQueryFilters().AnyAsync(a => a.ServerId == id, ct) ||
            await db.ManagedServices.IgnoreQueryFilters().AnyAsync(s => s.ServerId == id, ct))
        {
            TempData["Error"] = "Move or delete this node's apps and services first.";
            return RedirectToAction(nameof(Index));
        }
        db.Servers.Remove(server);
        await db.SaveChangesAsync(ct);
        return RedirectToAction(nameof(Index));
    }

    /// <summary>
    /// Set a server's commitment ratios directly, without going through a node.
    ///
    /// <para>
    /// <c>NodesController.CapacityPolicy</c> offers the same form, but only for a server reached
    /// through an attached <c>Node</c> row. The <b>Local</b> server never has one — <c>DbSeeder</c>
    /// creates it and only <c>NodeEnrollmentService</c> ever creates a <c>Node</c> — so on a
    /// single-server install, which is where nearly every app actually runs, these values could not
    /// be changed from the panel at all while the page told the operator they were an administrator's
    /// decision. A deploy refused for want of processor capacity had no answer in the UI.
    /// </para>
    /// </summary>
    /// <remarks>
    /// The three fields bind as strings for the reason
    /// <see cref="Harbora.Web.Infrastructure.CapacityPolicyForm.TryParseInvariant"/> gives: the
    /// request culture is Persian and binding a <c>double</c> with it turns "2.5" into 0.
    /// </remarks>
    [HttpPost("{id:guid}/capacity-policy")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.ServersManage)]
    public async Task<IActionResult> CapacityPolicy(
        Guid id, string? reservedMemoryPercent, string? cpuOvercommitFactor, string? memoryOvercommitFactor,
        CancellationToken ct)
    {
        var isFa = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

        var server = await db.Servers.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (server is null)
        {
            TempData["Error"] = isFa ? "سروری با این شناسه نیست." : "No such server.";
            return RedirectToAction(nameof(Index));
        }

        if (!Infrastructure.CapacityPolicyForm.TryParseInvariant(reservedMemoryPercent, out var reservedPercent) ||
            !Infrastructure.CapacityPolicyForm.TryParseInvariant(cpuOvercommitFactor, out var cpuFactor) ||
            !Infrastructure.CapacityPolicyForm.TryParseInvariant(memoryOvercommitFactor, out var memFactor))
        {
            TempData["Error"] = isFa
                ? "مقدار واردشده عدد معتبری نیست."
                : "One of the values entered is not a valid number.";
            return RedirectToAction(nameof(Index));
        }

        var reservedMemoryRatio = reservedPercent / 100.0;
        var error = Infrastructure.CapacityPolicyForm.Validate(reservedMemoryRatio, cpuFactor, memFactor, isFa);
        if (error is not null)
        {
            // Nothing is written when any one field is refused: a half-applied policy is a state the
            // administrator never asked for and would have to discover by reading the values back.
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        server.ReservedMemoryRatio = reservedMemoryRatio;
        server.CpuOvercommitFactor = cpuFactor;
        server.MemoryOvercommitFactor = memFactor;
        await db.SaveChangesAsync(ct);

        // Lowering a factor below what is already committed leaves the server oversubscribed on paper.
        // Not refused — the admin may be correcting a factor that was too generous — but said plainly:
        // what is placed keeps running, and the scheduler simply stops offering this server new work.
        var after = await capacity.GetAsync(id, ct);
        var oversubscribed = after is not null &&
            (after.CommittedMemoryBytes > after.AllocatableMemoryBytes || after.CommittedCpu > after.AllocatableCpu);

        TempData["Message"] = !oversubscribed
            ? (isFa ? "سیاست ظرفیت این سرور ذخیره شد." : "Capacity policy saved for this server.")
            : (isFa
                ? $"ذخیره شد — اما این سرور اکنون بیش از ظرفیت مجازش متعهد شده است " +
                  $"({Infrastructure.CapacityPolicyForm.FormatGb(after!.CommittedMemoryBytes)}/" +
                  $"{Infrastructure.CapacityPolicyForm.FormatGb(after.AllocatableMemoryBytes)} گیگابایت، " +
                  $"{after.CommittedCpu:0.##}/{after.AllocatableCpu:0.##} هسته‌ی پردازنده). آنچه مستقر است همچنان اجرا می‌شود؛ " +
                  "زمان‌بند تا کاهش مصرف یا افزایش دوباره‌ی نسبت، کار تازه‌ای به آن نمی‌دهد."
                : $"Saved — but this server is now committed beyond its allocatable capacity " +
                  $"({Infrastructure.CapacityPolicyForm.FormatGb(after!.CommittedMemoryBytes)}/" +
                  $"{Infrastructure.CapacityPolicyForm.FormatGb(after.AllocatableMemoryBytes)} GB, " +
                  $"{after.CommittedCpu:0.##}/{after.AllocatableCpu:0.##} CPU cores). What is already placed keeps " +
                  "running; the scheduler will not offer it new work until usage drops or the ratio is raised again.");

        return RedirectToAction(nameof(Index));
    }
}
