using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// The ceiling on a file a person uploads through a form, said the same way everywhere it is stated:
/// in the refusal, beside the file input, and in the script that stops a browser sending one too big.
///
/// <para>
/// <b>Why an import used to stop at 128 MB.</b> An action that takes a large upload needs three
/// limits to agree. <c>[RequestSizeLimit]</c> is the server's limit on the body;
/// <c>[RequestFormLimits]</c> is the form reader's limit on one multipart section, 128 MB by default
/// and blind to the other; and <c>[ValidateAntiForgeryToken]</c> reads the whole form before the
/// action runs, because the token is in it. With only the first, a 300 MB dump hit the form reader's
/// default inside the antiforgery check, which turns any failure to read the form into a bare 400 —
/// so the database import that said "up to 2 GB" refused everything over 128 MB, and said nothing
/// about size. <see cref="UploadedArchive.MaxBytes"/> documents the same trap from the zip side.
/// </para>
/// </summary>
public static class UploadLimit
{
    /// <summary>
    /// Room for everything in the request that is not the file: the multipart boundaries and headers,
    /// the antiforgery token, a typed confirmation. Added to a stated ceiling, so a file of exactly that
    /// size goes through.
    /// </summary>
    public const long FormOverheadBytes = 1024 * 1024;

    /// <summary>"2 GB", "512 MB": the ceiling the way a person reads one.</summary>
    public static string Describe(long bytes, bool isFa)
    {
        const long mb = 1024 * 1024;
        const long gb = 1024 * mb;
        var (amount, unit, unitFa) = bytes % gb == 0 ? (bytes / gb, "GB", "گیگابایت") : (bytes / mb, "MB", "مگابایت");
        var digits = amount.ToString(CultureInfo.InvariantCulture);
        return isFa
            ? $"{string.Concat(digits.Select(d => (char)('۰' + (d - '0'))))} {unitFa}"
            : $"{digits} {unit}";
    }

    /// <summary>The refusal, for the server and for the page's own script alike.</summary>
    public static string TooLarge(long maxBytes, bool isFa) => isFa
        ? $"این فایل از {Describe(maxBytes, isFa: true)} بزرگ‌تر است، یعنی بیشتر از سقفی که اینجا پذیرفته می‌شود. چیزی از آن خوانده نشد."
        : $"This file is larger than {Describe(maxBytes, isFa: false)}, the most this accepts. Nothing was read from it.";

    /// <summary>What the file input says beside it.</summary>
    public static string UpTo(long maxBytes, bool isFa) =>
        isFa ? $"تا {Describe(maxBytes, isFa: true)}" : $"Up to {Describe(maxBytes, isFa: false)}";
}

/// <summary>
/// Turns an upload over the ceiling into a sentence on the page it came from, instead of the bare 400
/// or 413 it was. Runs among the authorization filters, after the two limit attributes (order 900)
/// have set the body and form ceilings and before the antiforgery check (order 1000) reads the form:
/// that check is what used to answer.
///
/// <para>
/// A body declared longer than the ceiling is refused without reading a byte of it. Anything else is
/// read here — which the antiforgery check would have done next anyway, and the form is kept for it —
/// so a body that runs over a ceiling without saying how long it is gets the same sentence.
/// Authorization has already run by then, so nobody who may not import has a form read on their
/// behalf.
/// </para>
///
/// <para>
/// A browser may still give up on a large upload the server has answered before it finished sending
/// it, which is why each form also carries <c>data-upload-limit</c> and refuses a file that is too big
/// before anything is sent (<c>Scripts/uploadLimit.js</c>).
/// </para>
/// </summary>
/// <param name="maxFileBytes">The ceiling the page states; the body may be <see cref="UploadLimit.FormOverheadBytes"/> more.</param>
/// <param name="redirectToAction">Where the sentence is shown — this controller's action, given the route's own values.</param>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RefuseOversizedUploadAttribute(long maxFileBytes, string redirectToAction)
    : Attribute, IAsyncAuthorizationFilter, IOrderedFilter
{
    public long MaxFileBytes { get; } = maxFileBytes;
    public string RedirectToAction { get; } = redirectToAction;

    public int Order => 950;

    public async Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        // Somebody earlier already answered: a sign-in challenge, a forbidden.
        if (context.Result is not null) return;

        var request = context.HttpContext.Request;
        var tooLarge = request.ContentLength > MaxFileBytes + UploadLimit.FormOverheadBytes;

        if (!tooLarge && request.HasFormContentType)
        {
            try
            {
                await request.ReadFormAsync(context.HttpContext.RequestAborted);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
            {
                // The form reader's own ceiling on one section.
                tooLarge = true;
            }
            catch (Microsoft.AspNetCore.Http.BadHttpRequestException ex)
                when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                // The server's ceiling on the body, for one that did not say how long it was.
                tooLarge = true;
            }
        }

        if (!tooLarge) return;

        var isFa = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa";
        var tempData = context.HttpContext.RequestServices.GetRequiredService<ITempDataDictionaryFactory>()
            .GetTempData(context.HttpContext);
        tempData["Error"] = UploadLimit.TooLarge(MaxFileBytes, isFa);
        // Saved here: a result an authorization filter returns skips the filters that would save it.
        tempData.Save();

        var routeValues = new RouteValueDictionary(context.RouteData.Values);
        routeValues.Remove("action");
        routeValues.Remove("controller");
        context.Result = new RedirectToActionResult(RedirectToAction, controllerName: null, routeValues);
    }
}
