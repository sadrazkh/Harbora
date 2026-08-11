using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Authorization;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Mail;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Web.Controllers;

[Authorize]
[Route("mail")]
public sealed class MailController(
    HarboraDbContext db,
    MailPlatformService mail,
    ICurrentUser currentUser,
    IAuthorizationService authorization,
    IOptions<BillingOptions> billing) : Controller
{
    private Guid WorkspaceId => currentUser.WorkspaceId ?? Guid.Empty;
    private bool IsFa => System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        ViewData["Title"] = IsFa ? "ایمیل" : "Email";
        var canManage = (await authorization.AuthorizeAsync(User, Capabilities.PlatformManage)).Succeeded;
        return View(new MailPageViewModel
        {
            Server = await db.MailServers.IgnoreQueryFilters().AsNoTracking()
                .FirstOrDefaultAsync(x => x.IsActive, ct),
            Domains = await db.MailDomains.Include(x => x.Mailboxes)
                .Where(x => x.WorkspaceId == WorkspaceId)
                .OrderBy(x => x.Domain).ToListAsync(ct),
            AvailableServers = canManage
                ? await db.Servers.IgnoreQueryFilters().AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct)
                : [],
            CanManagePlatform = canManage,
            Currency = billing.Value.CurrencyOrDefault
        });
    }

    [HttpPost("server")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> Provision(
        Guid serverId, string hostname, string apiBaseUrl, string? image,
        string? adminUser, string? adminPassword, string? domainRate,
        string? mailboxRate, int maxDomains, int maxMailboxes, CancellationToken ct)
    {
        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseRate(domainRate, out var domainMinor)
            || !Harbora.Web.Infrastructure.MinorUnits.TryParseRate(mailboxRate, out var mailboxMinor))
        {
            TempData["Error"] = IsFa ? "قیمت‌ها باید عدد معتبر باشند." : "Hourly prices must be valid numbers.";
            return RedirectToAction(nameof(Index));
        }

        var result = await mail.ProvisionAsync(
            serverId, hostname, apiBaseUrl, image ?? "", adminUser ?? "admin", adminPassword,
            domainMinor, mailboxMinor, maxDomains, maxMailboxes, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "میل‌سرور ساخته شد. پس از آماده‌شدن، اتصال را آزمایش کنید." : "Mail server provisioned. Test it after startup.")
            : result.Error;
        if (result.Secret is not null) TempData["MailAdminSecret"] = result.Secret;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("server/{id:guid}/activate")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> Activate(
        Guid id, string permanentUser, string permanentPassword, CancellationToken ct)
    {
        var result = await mail.CompleteSetupAsync(id, permanentUser, permanentPassword, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "اتصال مدیریتی برقرار است؛ سرویس ایمیل آماده شد." : "Management connection works; email is ready.")
            : result.Error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("server/{id:guid}/offer")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.PlatformManage)]
    public async Task<IActionResult> UpdateOffer(
        Guid id, string? domainRate, string? mailboxRate,
        int maxDomains, int maxMailboxes, CancellationToken ct)
    {
        if (!Harbora.Web.Infrastructure.MinorUnits.TryParseRate(domainRate, out var domainMinor)
            || !Harbora.Web.Infrastructure.MinorUnits.TryParseRate(mailboxRate, out var mailboxMinor))
        {
            TempData["Error"] = IsFa ? "قیمت‌ها باید عدد معتبر باشند." : "Hourly prices must be valid numbers.";
            return RedirectToAction(nameof(Index));
        }
        var result = await mail.UpdateOfferAsync(id, domainMinor, mailboxMinor, maxDomains, maxMailboxes, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "قیمت و محدودیت‌های سرویس ایمیل به‌روزرسانی شد؛ قیمت منابع قبلی ثابت می‌ماند." : "Email prices and limits updated; existing resource prices remain unchanged.")
            : result.Error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("domains")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> CreateDomain(string domain, CancellationToken ct)
    {
        try
        {
            var result = await mail.CreateDomainAsync(WorkspaceId, domain, ct);
            TempData[result.Ok ? "Message" : "Error"] = result.Ok
                ? (IsFa ? "دامنه ایمیل ساخته شد. رکوردهای DNS پایین صفحه را تنظیم کنید." : "Mail domain created. Configure the DNS records shown below.")
                : result.Error;
        }
        catch (CreationPaymentRequiredException ex)
        {
            TempData["Error"] = IsFa ? ex.ReasonFa : ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("domains/{domainId:guid}/mailboxes")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> CreateMailbox(
        Guid domainId, string localPart, string? displayName, long quotaMb, CancellationToken ct)
    {
        try
        {
            var result = await mail.CreateMailboxAsync(
                WorkspaceId, domainId, localPart, displayName ?? "", quotaMb, ct);
            TempData[result.Ok ? "Message" : "Error"] = result.Ok
                ? (IsFa ? "صندوق ایمیل ساخته شد؛ رمز فقط همین یک‌بار نمایش داده می‌شود." : "Mailbox created; its password is shown only once.")
                : result.Error;
            if (result.Secret is not null) TempData["MailboxSecret"] = result.Secret;
        }
        catch (CreationPaymentRequiredException ex)
        {
            TempData["Error"] = IsFa ? ex.ReasonFa : ex.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("mailboxes/{id:guid}/password")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> ResetPassword(Guid id, CancellationToken ct)
    {
        var result = await mail.ResetMailboxPasswordAsync(WorkspaceId, id, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "رمز صندوق تغییر کرد و فقط همین یک‌بار نمایش داده می‌شود." : "Mailbox password reset and shown only once.")
            : result.Error;
        if (result.Secret is not null) TempData["MailboxSecret"] = result.Secret;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("mailboxes/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> DeleteMailbox(Guid id, CancellationToken ct)
    {
        var result = await mail.DeleteMailboxAsync(WorkspaceId, id, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "صندوق حذف شد و هزینه ساعتی آن متوقف شد." : "Mailbox deleted and its hourly charge stopped.")
            : result.Error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("domains/{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> DeleteDomain(Guid id, string confirmation, CancellationToken ct)
    {
        var result = await mail.DeleteDomainAsync(WorkspaceId, id, confirmation, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "دامنه ایمیل حذف شد و هزینه ساعتی آن متوقف شد." : "Mail domain deleted and its hourly charge stopped.")
            : result.Error;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("domains/{id:guid}/dns")]
    [ValidateAntiForgeryToken]
    [Authorize(Policy = Capabilities.DatabasesManage)]
    public async Task<IActionResult> RefreshDns(Guid id, CancellationToken ct)
    {
        var result = await mail.RefreshDnsAsync(WorkspaceId, id, ct);
        TempData[result.Ok ? "Message" : "Error"] = result.Ok
            ? (IsFa ? "رکوردهای DNS و DKIM از میل‌سرور به‌روز شد." : "DNS and DKIM records refreshed from the mail server.")
            : result.Error;
        return RedirectToAction(nameof(Index));
    }
}
