using System.Globalization;
using System.Text.Json;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Billing;
using Harbora.Web.Infrastructure;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

/// <summary>Provider-only creation and closure of single-use balance vouchers.</summary>
[Authorize(Policy = Capabilities.TenantsManage)]
[Route("vouchers")]
public sealed class VouchersController(
    HarboraDbContext db,
    VoucherService vouchers,
    ICurrentUser currentUser,
    IAuditLogger audit,
    IOptions<BillingOptions> billing) : Controller
{
    private string? ClientIp => HttpContext.Connection.RemoteIpAddress?.ToString();

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = "Billing vouchers";
        var workspaceNames = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .ToDictionaryAsync(w => w.Id, w => w.Name, ct);
        var rows = await db.BillingVouchers.AsNoTracking()
            .OrderByDescending(v => v.CreatedAt)
            .Take(200)
            .ToListAsync(ct);

        return View(new VoucherAdminPageViewModel
        {
            Currency = billing.Value.CurrencyOrDefault,
            CreatedCode = TempData["CreatedVoucherCode"] as string,
            Vouchers = rows.Select(v => new VoucherAdminRow(
                v.Id, v.CodeHint, v.AmountMinor, v.Currency, v.Note, v.CreatedAt, v.ExpiresAt,
                v.IsDisabled, v.RedeemedAt,
                v.RedeemedWorkspaceId is { } workspaceId
                    ? workspaceNames.GetValueOrDefault(workspaceId, "(deleted workspace)")
                    : null)).ToList()
        });
    }

    [HttpPost("create")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(
        string? amount, string? code, string? note, string? expiresAt, CancellationToken ct)
    {
        IActionResult Again(string error)
        {
            TempData["Error"] = error;
            return RedirectToAction(nameof(Index));
        }

        if (currentUser.UserId is not { } userId) return Again("Sign in again before creating a voucher.");
        if (!MinorUnits.TryParseMajor(amount, out var amountMinor) || amountMinor <= 0)
            return Again("Enter a positive voucher amount.");

        DateTimeOffset? expiry = null;
        if (!string.IsNullOrWhiteSpace(expiresAt))
        {
            if (!DateTimeOffset.TryParse(expiresAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                return Again("Enter a valid expiry date.");
            expiry = parsed;
        }

        try
        {
            var created = await vouchers.CreateAsync(amountMinor, code, note, expiry, userId, ct);
            TempData["CreatedVoucherCode"] = created.PlaintextCode;
            TempData["Message"] = "Voucher created. Copy it now; Harbora stores only its hash.";
            await audit.LogAsync("billing.voucher.create", "voucher", created.Voucher.Id.ToString(), ClientIp,
                metadataJson: JsonSerializer.Serialize(new
                {
                    amountMinor,
                    created.Voucher.Currency,
                    created.Voucher.ExpiresAt,
                    created.Voucher.CodeHint
                }), ct: ct);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return Again(ex.Message);
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost("{id:guid}/disable")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Disable(Guid id, CancellationToken ct)
    {
        try
        {
            await vouchers.DisableAsync(id, ct);
            await audit.LogAsync("billing.voucher.disable", "voucher", id.ToString(), ClientIp, ct: ct);
            TempData["Message"] = "Voucher disabled.";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }
}
