using Harbora.Application.Abstractions;
using Harbora.Infrastructure.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// Redeems a one-time volume download link — sub-project D4.
///
/// <para>
/// Deliberately outside every app route, and the one route in the whole panel that is
/// unauthenticated on purpose: a link that needed a panel session would not be a shareable link,
/// which is the entire request this feature answers. What makes that acceptable lives entirely in
/// <see cref="VolumeDownloadTokens"/> — single use, self-expiry borrowed from
/// <c>AdminerSession.Lifetime</c>, a path fixed at mint time, and an app/volume pairing resolved
/// through the minting caller's own tenant-filtered collection. This route adds no gate of its own:
/// a second one here would make the link no longer usable with a bare <c>curl</c>, which is the one
/// thing it was built to be.
/// </para>
/// <para>
/// A refused token — never existed, already spent, or past its hour — answers 404 for all three.
/// The panel is where somebody signed in learns which of those it was; a stranger holding the link
/// does not need to.
/// </para>
/// </summary>
[AllowAnonymous]
[Route("dl")]
public sealed class VolumeDownloadController(
    VolumeDownloadTokens tokens,
    VolumeFileService files,
    IAuditLogger audit) : Controller
{
    [HttpGet("{token}")]
    public async Task<IActionResult> Get(string token, CancellationToken ct)
    {
        var redemption = await tokens.RedeemAsync(token, ct);
        if (!redemption.Ok) return NotFound();

        var content = await files.ReadAsync(redemption.ServerId, redemption.VolumeName, redemption.Path, ct);
        if (content is null) return NotFound();

        await audit.LogAsync(
            "app.data_link_redeemed", "volume", $"{redemption.VolumeName}/{redemption.Path}",
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            workspaceId: redemption.WorkspaceId, ct: ct);

        // Always as an attachment, and always as bytes — the same reason AppDataController.Download
        // never serves a volume's contents inline: this response carries no session to protect, but
        // it does share the panel's own origin, and inline script from a customer's file is still
        // script on that origin.
        return File(content, "application/octet-stream", VolumePath.NameOf(redemption.Path));
    }
}
