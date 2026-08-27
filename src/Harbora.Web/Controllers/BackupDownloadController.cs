using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Backups;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// Redeems a one-time self-serve database export link — sub-project 10.
///
/// <para>
/// Deliberately outside <c>DatabasesController</c>'s "/databases/{id}/…" prefix and, like
/// <see cref="VolumeDownloadController"/> (D4) which this mirrors, the one other route in the panel
/// that is unauthenticated on purpose: a link that needed a panel session would not be a shareable
/// link, which is the whole point of minting one. What makes that acceptable lives entirely in
/// <see cref="BackupDownloadTokens"/> — single use, self-expiry, the backup fixed at mint time, and a
/// workspace pairing resolved through the minting caller's own tenant-filtered collection. This route
/// adds no gate of its own, the same reasoning <see cref="VolumeDownloadController"/>'s own doc comment
/// gives for the same choice.
/// </para>
/// <para>
/// A refused token — never existed, already spent, past its hour, or naming a backup that is no
/// longer a completed artifact — answers 404 for all of them. The panel is where somebody signed in
/// learns which of those it was; a stranger holding the link does not need to.
/// </para>
/// </summary>
[AllowAnonymous]
[Route("backups/download")]
public sealed class BackupDownloadController(
    BackupDownloadTokens tokens,
    IBackupStorage storage,
    IAuditLogger audit) : Controller
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var redemption = await tokens.RedeemAsync(token, ct);
        if (!redemption.Ok || redemption.Backup is null || redemption.Destination is null) return NotFound();

        var localPath = await storage.GetToLocalAsync(redemption.Destination, redemption.Backup.ArtifactPath!, ct);
        if (!System.IO.File.Exists(localPath)) return NotFound();

        await audit.LogAsync("database.export_downloaded", "backup", redemption.BackupId.ToString(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            workspaceId: redemption.Backup.WorkspaceId, ct: ct);

        // Always as an attachment, and always as bytes — the same reason VolumeDownloadController never
        // serves a redeemed file inline: this response carries no session to protect, but it does share
        // the panel's own origin, and inline content from a customer's own dump is still content on it.
        return PhysicalFile(
            System.IO.Path.GetFullPath(localPath), "application/octet-stream",
            System.IO.Path.GetFileName(localPath));
    }
}
