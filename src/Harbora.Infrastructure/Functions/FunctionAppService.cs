using System.Security.Cryptography;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Apps;
using Harbora.Domain.Common;
using Harbora.Domain.Functions;
using Harbora.Infrastructure.Deployments;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Functions;

/// <summary>Why a function could not be saved, in both languages the panel speaks.</summary>
public sealed record FunctionValidation(bool Ok, string? Field = null, string? Message = null, string? MessageFa = null)
{
    public static readonly FunctionValidation Valid = new(true);

    public static FunctionValidation Fail(string field, string message, string messageFa) =>
        new(false, field, message, messageFa);
}

/// <summary>
/// The rules a function app obeys, in one place away from the controller.
///
/// <para>
/// Everything here is a refusal the server makes on its own. The editor greys out what it can, but a
/// second function claiming a route, an unreadable cron expression or an event key nothing publishes
/// all have to be refused here — the form is a courtesy, never the check.
/// </para>
/// </summary>
public sealed class FunctionAppService(HarboraDbContext db, ISecretProtector protector, IDeploymentEngine deployments)
{
    /// <summary>
    /// Issues the app's invoke secret if it has none, and returns whether anything changed.
    ///
    /// <para>
    /// Rotating it is deliberately not offered: the secret only ever travels from this database into
    /// the app's own container environment, and a rotation that landed between the panel's call and
    /// the container's restart would lock the scheduler out of a running app for no gain.
    /// </para>
    /// </summary>
    public bool EnsureSecret(App app)
    {
        if (!string.IsNullOrEmpty(app.FunctionInvokeSecret)) return false;

        app.FunctionInvokeSecret = protector.Protect(
            Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        return true;
    }

    /// <summary>
    /// Checks one function against its app's other functions.
    /// </summary>
    /// <param name="existing">
    /// Every function already in the app, including the one being edited — which is excluded by
    /// <paramref name="editingId"/> rather than by the caller filtering the list, so a rename cannot
    /// collide with itself.
    /// </param>
    public static FunctionValidation Validate(
        FunctionDefinition candidate, IReadOnlyList<FunctionDefinition> existing, Guid? editingId)
    {
        var others = existing.Where(f => f.Id != editingId).ToList();

        if (string.IsNullOrWhiteSpace(candidate.Name))
            return FunctionValidation.Fail(nameof(candidate.Name),
                "Give the function a name.", "برای فانکشن یک نام بگذارید.");

        if (!FunctionSlug.IsValid(candidate.Slug))
            return FunctionValidation.Fail(nameof(candidate.Name),
                "That name has no usable identifier — use letters, digits and hyphens.",
                "از این نام شناسه‌ای ساخته نمی‌شود — از حروف، رقم و خط تیره استفاده کنید.");

        if (others.Any(f => string.Equals(f.Slug, candidate.Slug, StringComparison.OrdinalIgnoreCase)))
            return FunctionValidation.Fail(nameof(candidate.Name),
                "Another function in this app already uses that name.",
                "فانکشن دیگری در همین اپ این نام را دارد.");

        switch (candidate.Trigger)
        {
            case FunctionTrigger.Http:
            {
                var route = FunctionProject.RouteFor(candidate);
                // Two functions on one route is not a validation nicety: the generated dispatcher
                // picks the longest match and would silently give every request to one of them.
                if (others.Any(f => f.Trigger == FunctionTrigger.Http
                                 && string.Equals(FunctionProject.RouteFor(f), route, StringComparison.OrdinalIgnoreCase)))
                    return FunctionValidation.Fail(nameof(candidate.Route),
                        $"Another function already answers on '/{route}'.",
                        $"فانکشن دیگری روی «/{route}» پاسخ می‌دهد.");
                break;
            }

            case FunctionTrigger.Cron:
                if (!CronSchedule.TryParse(candidate.CronExpression, out _, out var cronError))
                    return FunctionValidation.Fail(nameof(candidate.CronExpression),
                        cronError ?? "That schedule cannot be read.", "این زمان‌بندی خوانده نمی‌شود.");
                break;

            case FunctionTrigger.Event:
                if (!FunctionEvents.IsKnown(candidate.EventKey))
                    return FunctionValidation.Fail(nameof(candidate.EventKey),
                        "Choose an event this function should run on.",
                        "رویدادی را که این فانکشن باید با آن اجرا شود انتخاب کنید.");
                break;
        }

        if (string.IsNullOrWhiteSpace(candidate.Code))
            return FunctionValidation.Fail(nameof(candidate.Code),
                "The function has no code.", "فانکشن هیچ کدی ندارد.");

        return FunctionValidation.Valid;
    }

    /// <summary>Every function in one app, in the order the panel lists them.</summary>
    public Task<List<FunctionDefinition>> ListAsync(Guid appId, CancellationToken ct) =>
        db.FunctionDefinitions
            .Where(f => f.AppId == appId)
            .OrderBy(f => f.Slug)
            .ToListAsync(ct);

    /// <summary>
    /// Builds and releases the current code.
    ///
    /// <para>
    /// An ordinary deployment, queued the ordinary way. The whole point of a function app being an
    /// app is that this line is all "publish" has to be.
    /// </para>
    /// </summary>
    public Task<Guid> PublishAsync(Guid appId, Guid userId, CancellationToken ct) =>
        deployments.QueueDeploymentAsync(
            new DeploymentRequest(appId, DeploymentTrigger.Manual, userId), ct);

    /// <summary>
    /// Marks every function in an app as edited-since-published.
    ///
    /// <para>
    /// Whole-app rather than per-row because the image is whole-app: editing one function and
    /// publishing rebuilds and re-releases all of them, so a page saying only one is unpublished
    /// would be describing something the platform cannot do.
    /// </para>
    /// </summary>
    public async Task MarkDirtyAsync(Guid appId, CancellationToken ct)
    {
        var functions = await db.FunctionDefinitions.Where(f => f.AppId == appId).ToListAsync(ct);
        foreach (var fn in functions)
        {
            fn.HasUnpublishedChanges = true;
            fn.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>The most recent calls of one function, newest first.</summary>
    public Task<List<FunctionInvocation>> RecentInvocationsAsync(Guid functionId, int take, CancellationToken ct) =>
        db.FunctionInvocations
            .Where(i => i.FunctionId == functionId)
            .OrderByDescending(i => i.StartedAt)
            .Take(take)
            .ToListAsync(ct);
}
