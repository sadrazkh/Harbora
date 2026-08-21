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

        app.FunctionInvokeSecret = protector.Protect(FunctionInvokeSecret.Mint());
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
                // A platform event from the fixed catalog, or a workspace's own custom.* one — the
                // same acceptance FunctionEventBus itself uses, so a subscription this form accepts
                // is never one the bus would then discard as unknown.
                if (!FunctionEvents.IsSubscribable(candidate.EventKey))
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

    /// <summary>
    /// How many past versions of one function's code stay restorable.
    ///
    /// <para>
    /// Twenty is "the length of one genuinely bad afternoon of edits" — comfortably more than the
    /// two or three saves a person makes shaping one function (the case this whole editor is sized
    /// for, per the design doc's own ranking), while staying a number nobody has to size a sweeper
    /// around: pruned inline on every save, so the table never grows past
    /// <c>MaxRevisions</c> rows times the number of functions that have ever been saved, regardless
    /// of the platform's age. Going back further than that is what Publish's own deployment history
    /// is for — a different table, kept for a different reason.
    /// </para>
    /// </summary>
    public const int MaxRevisions = 20;

    /// <summary>
    /// Snapshots the code about to be saved as a new, immutable revision, then prunes anything past
    /// the newest <see cref="MaxRevisions"/> for this function. Called once per successful save,
    /// including a restore — which is itself a save, so restoring to an old version shows up as its
    /// own new revision rather than rewriting or deleting the one it copied from.
    /// </summary>
    public async Task RecordRevisionAsync(FunctionDefinition function, CancellationToken ct)
    {
        db.FunctionCodeRevisions.Add(new FunctionCodeRevision
        {
            FunctionId = function.Id,
            WorkspaceId = function.WorkspaceId,
            Code = function.Code,
        });

        var existing = await db.FunctionCodeRevisions
            .Where(r => r.FunctionId == function.Id)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);

        // The row added above is tracked but not yet persisted, so it is not in `existing` — what
        // needs pruning is whatever sits beyond the newest MaxRevisions - 1 already-saved rows,
        // leaving exactly MaxRevisions once the new one lands too.
        if (existing.Count > MaxRevisions - 1)
            db.FunctionCodeRevisions.RemoveRange(existing.Skip(MaxRevisions - 1));
    }

    /// <summary>Every kept revision of one function, newest first — at most <see cref="MaxRevisions"/>.</summary>
    public Task<List<FunctionCodeRevision>> RecentRevisionsAsync(Guid functionId, CancellationToken ct) =>
        db.FunctionCodeRevisions
            .Where(r => r.FunctionId == functionId)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync(ct);
}
