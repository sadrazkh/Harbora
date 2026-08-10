using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Billing;

/// <summary>
/// Answers "may this workspace start something right now?" from the balance and the suspension.
///
/// <para>
/// <b>Every read ignores the tenant filter, and has to.</b> The callers that matter most have no
/// session at all: the job worker running a queued deployment, the cron tick, the webhook that
/// queued the deployment in the first place. <c>Wallet</c> carries a tenant filter, and an
/// unauthenticated request resolves to <see cref="Guid.Empty"/>, which matches no tenant — so a
/// filtered read would find no wallet on every one of those paths and answer a question about a
/// workspace it could not see. This is the same call <c>BillingTick</c>, <c>BillingSuspension</c>
/// and the retention sweeper all make, for the same reason.
/// </para>
///
/// <para>
/// Said plainly about the workspace read specifically: <c>Workspace</c> carries no query filter of
/// its own today — it is the table the other filters are written in terms of — so the
/// <c>IgnoreQueryFilters</c> on it currently changes nothing and no test can be made to fail by
/// deleting it. It is here anyway, for the reason <see cref="BillingSuspension"/> gives for its
/// identical line: this class must not depend on which tables happen to be filtered this month.
/// </para>
///
/// <para>
/// <b>No wallet is no money.</b> A workspace that has never been through a tick has no wallet row;
/// the tick creates one holding zero when it first reaches the workspace. Treating "no wallet" as
/// anything other than a balance of zero would make the answer depend on whether the hourly pass
/// happened to have run yet, which is not a thing anybody should be able to get free hosting out of.
/// </para>
///
/// <para>
/// <b>The provider's own workspace is never refused for money.</b> The tick charges it like every
/// other workspace and <see cref="BillingSuspension"/> refuses to suspend it, because the panel's
/// own workloads live in it. Without the same exemption here, the platform's balance reaching zero
/// would stop the platform being able to start anything — including the screen somebody would use
/// to put it right.
/// </para>
/// </summary>
public sealed class BillingGate(
    HarboraDbContext db,
    IOptions<BillingOptions> options,
    ISystemClock? clock = null) : IBillingGate
{
    public Task<QuotaCheck> CanStartAsync(Guid workspaceId, CancellationToken ct) =>
        CanStartAsync(workspaceId, BilledResourceType.None, Guid.Empty, ct);

    public async Task<QuotaCheck> CanStartAsync(
        Guid workspaceId, BilledResourceType resourceType, Guid resourceId, CancellationToken ct)
    {
        // The switch guards the money everywhere else in this feature and it guards it here too. An
        // install that upgraded into billing unasked must not begin refusing to run a tenant's
        // workloads over a balance nobody ever told them existed.
        if (!options.Value.Enabled) return QuotaCheck.Ok;

        var workspace = await db.Workspaces.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, ct);

        if (workspace is null)
            return QuotaCheck.Deny(
                $"There is no workspace with id {workspaceId}, so nothing can be started for it.",
                $"فضای کاری‌ای با شناسه‌ی {workspaceId} وجود ندارد؛ چیزی برای آن اجرا نخواهد شد.");

        if (workspace.IsDefault) return QuotaCheck.Ok;

        var balanceMinor = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => (long?)w.BalanceMinor)
            .FirstOrDefaultAsync(ct) ?? 0;
        var budget = await new WorkspaceBudgetService(db).GetAsync(
            workspaceId, clock?.UtcNow ?? DateTimeOffset.UtcNow, ct);

        if (workspace.IsSuspended)
        {
            // Suspended for an empty balance, and paid since. This is the window
            // BillingSuspension.ResumeAsync works inside: it starts back the apps the suspension
            // stopped and only then clears the flag, and those starts come through
            // IAppOperationsService, which asks this gate. Refusing on the flag alone would mean a
            // customer pays, the platform tries to bring their services back, and the platform
            // refuses itself on the grounds that they have not paid.
            //
            // Asked as "IS NoBalance" rather than "is not Manual", because every workspace suspended
            // before the reason column existed reads as None — the same distinction ResumeAsync
            // draws, over exactly the same rows.
            if (workspace.SuspendedReason == SuspensionReason.NoBalance && balanceMinor > 0)
                return QuotaCheck.Ok;

            // The first request made by automatic monthly resumption must be able to pass through
            // the same gate it is trying to reopen. Raising/removing the cap has the same effect.
            if (WorkspaceBudgetService.CanResetSpendLimit(
                    workspace, clock?.UtcNow ?? DateTimeOffset.UtcNow))
                return QuotaCheck.Ok;

            return workspace.SuspendedReason == SuspensionReason.Manual
                ? QuotaCheck.Deny(
                    "This workspace has been suspended by the provider. Nothing can be started until " +
                    "that is lifted; paying does not lift it.",
                    "این فضای کاری توسط مدیر پلتفرم معلق شده است. تا برداشته‌شدن این تعلیق چیزی در آن " +
                    "اجرا نخواهد شد؛ پرداخت هزینه، تعلیق را برنمی‌دارد.")
                : workspace.SuspendedReason == SuspensionReason.SpendLimit
                ? QuotaCheck.Deny(
                    "This workspace has reached its monthly spend limit. Raise the limit or wait for the next UTC month.",
                    "این فضای کاری به سقف هزینهٔ ماهانه رسیده است؛ سقف را افزایش دهید یا تا ماه بعد به وقت UTC صبر کنید.")
                : QuotaCheck.Deny(
                    "This workspace is suspended, so nothing can be started in it.",
                    "این فضای کاری معلق است، بنابراین چیزی در آن اجرا نمی‌شود.");
        }


        if (budget.SpendLimitReached)
        {
            // Reaching the limit by prepaying this resource's current hour does not invalidate the
            // hour already bought. The identity prevents any other workload piggybacking on it.
            if (resourceType != BilledResourceType.None && resourceId != Guid.Empty)
            {
                var hour = TopOfHour(clock?.UtcNow ?? DateTimeOffset.UtcNow);
                var prepaidAtLimit = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
                    .AnyAsync(l => l.WorkspaceId == workspaceId && l.ResourceType == resourceType
                        && l.ResourceId == resourceId && l.BillingHour == hour
                        && l.Kind == LedgerKind.Charge && l.AmountMinor < 0, ct);
                if (prepaidAtLimit) return QuotaCheck.Ok;
            }

            return QuotaCheck.Deny(
                "This workspace has reached its monthly spend limit. Nothing new can be started until the limit is raised or the next UTC month begins.",
                "این فضای کاری به سقف هزینهٔ ماهانه رسیده است؛ تا افزایش سقف یا شروع ماه بعد به وقت UTC چیزی اجرا نمی‌شود.");
        }

        if (balanceMinor > 0) return QuotaCheck.Ok;

        // Paying exactly one hour leaves the wallet at zero. That must not stop the very resource
        // whose current hour is already on the ledger, but the identity is essential: a workspace-
        // level exception would let every other stopped workload piggyback on one prepaid app.
        if (balanceMinor == 0 && resourceType != BilledResourceType.None && resourceId != Guid.Empty)
        {
            var hour = TopOfHour(clock?.UtcNow ?? DateTimeOffset.UtcNow);
            var prepaid = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
                .AnyAsync(l => l.WorkspaceId == workspaceId
                               && l.ResourceType == resourceType
                               && l.ResourceId == resourceId
                               && l.BillingHour == hour
                               && l.Kind == LedgerKind.Charge
                               && l.AmountMinor < 0, ct);
            if (prepaid) return QuotaCheck.Ok;
        }

        // Zero is not a balance. The gap between the balance running out and the hourly pass
        // suspending the workspace is up to an hour wide, and this is the only thing standing in it.
        return QuotaCheck.Deny(
            "This workspace has no balance left, so nothing new can be started. " +
            "Top it up and try again.",
            "اعتبار این فضای کاری به پایان رسیده است، بنابراین چیز جدیدی نمی‌تواند اجرا شود. " +
            "حساب را شارژ کنید و دوباره تلاش کنید.");
    }

    private static DateTimeOffset TopOfHour(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }
}
