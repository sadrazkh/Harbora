using System.Globalization;
using Harbora.Data;
using Harbora.Domain.Settings;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Billing;

/// <summary>How much the platform has automatically handed to new workspaces, and to how many of them.</summary>
/// <param name="TotalGrantedMinor">
/// The sum of every trial credit ever actually redeemed. Not "ever attempted" — a grant that lost a
/// concurrency race never wrote a row at all (see <see cref="SignupTrialCreditService.GrantAsync"/>),
/// so there is nothing unfinished to exclude here, the same way an unfinished credit never appears on
/// <see cref="RevenueReport"/> either.
/// </param>
public sealed record SignupCreditTotals(long TotalGrantedMinor, int WorkspacesGranted);

/// <summary>
/// Grants an administrator-configured signup credit to a brand-new workspace, exactly once, through
/// the same voucher-and-ledger machinery every other credit on this platform already uses.
///
/// <para>
/// <b>There is no second door for money here.</b> A grant is exactly two calls:
/// <see cref="VoucherService.CreateAsync"/> with <c>isTrialCredit: true</c>, then
/// <see cref="VoucherService.RedeemAsync"/> — the same two calls a human administrator's own voucher
/// screen makes, in the same order, just made on the platform's own behalf instead of a person
/// typing into a form. Nothing here writes a
/// <see cref="Harbora.Domain.Billing.BillingLedgerEntry"/> or touches a <see cref="Wallet"/> directly.
/// </para>
///
/// <para>
/// <b>The identity of "already granted" is the workspace's owner, not the workspace's own id.</b> A
/// workspace deleted and recreated gets a fresh <see cref="Guid"/> and would look like a brand-new
/// grant target if the check were keyed on that — exactly the abuse this feature has to refuse:
/// delete the trial workspace, sign back in, collect again. The owner survives a delete, so
/// <see cref="Harbora.Domain.Billing.BillingVoucher.CreatedByUserId"/> is set to the owner (never to
/// an administrator — there is none; the platform is the actor here) and is what both the fast-path
/// read and the database's own partial unique index (see <c>HarboraDbContext</c>,
/// <c>IX_BillingVouchers_TrialCreditOwner</c>) key on. One owner, at most one trial-credit voucher,
/// for as long as the install exists — a second workspace by the same owner (a team workspace
/// created after their personal one) is "a new workspace" but not "a new owner", and correctly
/// collects nothing.
/// </para>
///
/// <para>
/// <b>Two layers, the same shape <see cref="WalletService.WriteAsync"/> already uses for a credit's
/// id.</b> The ordinary case — a retried signup request, a resumed registration — is answered by a
/// read before anything is written. The genuine race — two requests for the same brand-new owner
/// landing together — is answered by the database: <see cref="VoucherService.CreateAsync"/>'s single
/// insert either lands or is refused whole by <c>IX_BillingVouchers_TrialCreditOwner</c>, so a lost
/// race leaves no row at all rather than one this call has to notice and clean up afterwards.
/// </para>
/// </summary>
public sealed class SignupTrialCreditService(HarboraDbContext db, VoucherService vouchers)
{
    public async Task<long> GetAmountMinorAsync(CancellationToken ct)
    {
        var raw = await db.Settings.IgnoreQueryFilters()
            .Where(s => s.Key == SettingKeys.SignupTrialCreditMinor)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(ct);
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor) && minor > 0
            ? minor
            : 0;
    }

    /// <summary>Stored as an invariant integer string; zero and blank both read back as "off".</summary>
    public async Task SetAmountMinorAsync(long amountMinor, CancellationToken ct)
    {
        var row = await db.Settings.IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.Key == SettingKeys.SignupTrialCreditMinor, ct);
        if (row is null)
        {
            row = new Setting { Key = SettingKeys.SignupTrialCreditMinor };
            db.Settings.Add(row);
        }

        row.Value = amountMinor > 0 ? amountMinor.ToString(CultureInfo.InvariantCulture) : string.Empty;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What the admin settings page shows beside the amount box: proof the switch has done something.</summary>
    public async Task<SignupCreditTotals> TotalsAsync(CancellationToken ct)
    {
        var granted = await db.BillingVouchers.AsNoTracking()
            .Where(v => v.IsTrialCredit && v.RedeemedAt != null)
            .Select(v => v.AmountMinor)
            .ToListAsync(ct);
        return new SignupCreditTotals(granted.Sum(), granted.Count);
    }

    /// <summary>
    /// Grants the configured signup credit to <paramref name="workspaceId"/>, once for
    /// <paramref name="ownerUserId"/> for the life of the install. A configured amount of zero — the
    /// shipped default — grants nothing and writes nothing; see the class comment for what makes a
    /// second attempt for the same owner a safe no-op rather than a second credit.
    /// </summary>
    /// <param name="workspaceId">The workspace just created. Money lands here.</param>
    /// <param name="ownerUserId">
    /// The workspace's own owner — never an administrator's id, and never <see cref="Guid.Empty"/>.
    /// Doubles as the voucher's <c>CreatedByUserId</c> and as the identity the uniqueness rule keys
    /// on; see the class comment.
    /// </param>
    public async Task GrantAsync(Guid workspaceId, Guid ownerUserId, CancellationToken ct)
    {
        var amountMinor = await GetAmountMinorAsync(ct);
        if (amountMinor <= 0) return;

        // The ordinary case — a retried signup, a resumed registration calling this a second time —
        // is one person's situation arriving twice, not a race, so it is answered by a read first.
        // The genuine race is answered below, by the database, because a read cannot win one.
        if (await db.BillingVouchers.AsNoTracking()
                .AnyAsync(v => v.IsTrialCredit && v.CreatedByUserId == ownerUserId, ct))
            return;

        CreatedVoucher created;
        try
        {
            created = await vouchers.CreateAsync(
                amountMinor,
                requestedCode: null,
                note: "Signup trial credit — issued automatically by the platform, not a purchase or a support voucher.",
                expiresAt: null,
                createdByUserId: ownerUserId,
                ct,
                isTrialCredit: true);
        }
        catch (InvalidOperationException)
        {
            // VoucherService.CreateAsync's own refusal — either a genuine CodeHash collision
            // (astronomically unlikely for a securely random code) or, in practice, another
            // concurrent grant for this same owner reaching IX_BillingVouchers_TrialCreditOwner
            // first. Both mean the same thing here: no row was written, and this owner either
            // already has a trial credit or is about to, from the request that won. Nothing to
            // clean up — a failed insert leaves nothing behind.
            return;
        }

        await vouchers.RedeemAsync(created.PlaintextCode, workspaceId, ownerUserId, ct);
    }
}
