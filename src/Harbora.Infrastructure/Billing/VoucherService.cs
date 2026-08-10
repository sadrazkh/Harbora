using System.Security.Cryptography;
using System.Text;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Harbora.Infrastructure.Billing;

public sealed record CreatedVoucher(BillingVoucher Voucher, string PlaintextCode);

public sealed record VoucherRedemption(
    Guid VoucherId,
    long AmountMinor,
    long BalanceMinor,
    bool Applied,
    int AppsStarted,
    int DatabasesStarted,
    IReadOnlyList<string> Failures);

/// <summary>Creates and redeems single-use, hashed balance vouchers.</summary>
public sealed class VoucherService(
    HarboraDbContext db,
    WalletService wallets,
    ISystemClock clock,
    IOptions<BillingOptions> options)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<CreatedVoucher> CreateAsync(
        long amountMinor,
        string? requestedCode,
        string? note,
        DateTimeOffset? expiresAt,
        Guid createdByUserId,
        CancellationToken ct)
    {
        if (amountMinor <= 0)
            throw new ArgumentOutOfRangeException(nameof(amountMinor), "A voucher amount must be positive.");
        if (createdByUserId == Guid.Empty)
            throw new ArgumentException("A voucher must name the administrator that created it.", nameof(createdByUserId));
        if (expiresAt is { } expiry && expiry <= clock.UtcNow)
            throw new ArgumentOutOfRangeException(nameof(expiresAt), "A voucher cannot expire in the past.");

        var plaintext = string.IsNullOrWhiteSpace(requestedCode)
            ? GenerateCode()
            : Display(Normalize(requestedCode));
        var normalized = Normalize(plaintext);
        var hash = Hash(normalized);

        if (await db.BillingVouchers.AsNoTracking().AnyAsync(v => v.CodeHash == hash, ct))
            throw new InvalidOperationException("That voucher code already exists. Choose a different code.");

        var voucher = new BillingVoucher
        {
            CodeHash = hash,
            CodeHint = normalized[^4..],
            AmountMinor = amountMinor,
            Currency = options.Value.CurrencyOrDefault,
            Note = string.IsNullOrWhiteSpace(note) ? "Balance voucher" : note.Trim(),
            CreatedByUserId = createdByUserId,
            ExpiresAt = expiresAt?.ToUniversalTime(),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        };

        db.BillingVouchers.Add(voucher);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("That voucher code already exists. Choose a different code.", ex);
        }

        return new CreatedVoucher(voucher, plaintext);
    }

    public async Task<VoucherRedemption> RedeemAsync(
        string? presentedCode,
        Guid workspaceId,
        Guid userId,
        CancellationToken ct)
    {
        if (workspaceId == Guid.Empty || userId == Guid.Empty)
            throw new InvalidOperationException("Sign in to a workspace before redeeming a voucher.");

        var normalized = Normalize(presentedCode);
        var hash = Hash(normalized);
        var voucher = await db.BillingVouchers.FirstOrDefaultAsync(v => v.CodeHash == hash, ct)
                      ?? throw new InvalidOperationException("That voucher code is not valid.");

        if (voucher.IsDisabled) throw new InvalidOperationException("That voucher has been disabled.");
        if (voucher.ExpiresAt is { } expiry && expiry <= clock.UtcNow)
            throw new InvalidOperationException("That voucher has expired.");

        var accountCurrency = await db.Wallets.IgnoreQueryFilters().AsNoTracking()
            .Where(w => w.WorkspaceId == workspaceId)
            .Select(w => w.Currency)
            .FirstOrDefaultAsync(ct) ?? options.Value.CurrencyOrDefault;
        if (!string.Equals(voucher.Currency, accountCurrency, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"That voucher is in {voucher.Currency}, but this account is in {accountCurrency}.");

        if (voucher.RedeemedWorkspaceId is { } redeemedWorkspace)
        {
            if (redeemedWorkspace != workspaceId)
                throw new InvalidOperationException("That voucher has already been used.");
            // Same workspace replay: WalletService's idempotency returns the real balance and also
            // retries any resume that failed after the first request put the money in.
        }

        var isMember = await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);
        if (!isMember)
            throw new InvalidOperationException("You are not a member of the workspace receiving this voucher.");

        CreditResult credited;
        try
        {
            credited = await wallets.CreditAsync(new CreditRequest(
                voucher.Id,
                workspaceId,
                voucher.AmountMinor,
                $"Voucher ••••{voucher.CodeHint}: {voucher.Note}",
                userId), ct);
        }
        catch (InvalidOperationException)
        {
            // A process may have committed the credit and died before marking the voucher. Recover
            // the marker from the append-only line, then return the honest "already used" answer.
            await RecoverRedemptionAsync(voucher, ct);
            throw;
        }

        await MarkRedeemedAsync(voucher, workspaceId, userId, ct);
        return new VoucherRedemption(
            voucher.Id,
            voucher.AmountMinor,
            credited.BalanceMinor,
            credited.Applied,
            credited.AppsStarted,
            credited.DatabasesStarted,
            credited.Failures);
    }

    public async Task DisableAsync(Guid voucherId, CancellationToken ct)
    {
        var voucher = await db.BillingVouchers.FirstOrDefaultAsync(v => v.Id == voucherId, ct)
                      ?? throw new InvalidOperationException("Voucher not found.");
        if (voucher.RedeemedAt is not null)
            throw new InvalidOperationException("A redeemed voucher is already closed and cannot be disabled.");
        voucher.IsDisabled = true;
        voucher.ConcurrencyStamp = Guid.CreateVersion7();
        voucher.UpdatedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
    }

    private async Task MarkRedeemedAsync(
        BillingVoucher voucher, Guid workspaceId, Guid userId, CancellationToken ct)
    {
        if (voucher.RedeemedWorkspaceId is { } existing && existing != workspaceId)
            throw new InvalidOperationException("That voucher has already been used.");

        voucher.RedeemedWorkspaceId = workspaceId;
        voucher.RedeemedByUserId = userId;
        voucher.RedeemedAt ??= clock.UtcNow;
        voucher.ConcurrencyStamp = Guid.CreateVersion7();
        voucher.UpdatedAt = clock.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            await db.Entry(voucher).ReloadAsync(ct);
            if (voucher.RedeemedWorkspaceId != workspaceId)
                throw new InvalidOperationException("That voucher has already been used.");
        }
    }

    private async Task RecoverRedemptionAsync(BillingVoucher voucher, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var line = await db.BillingLedger.IgnoreQueryFilters().AsNoTracking()
            .FirstOrDefaultAsync(l => l.Id == voucher.Id && l.Kind == LedgerKind.Credit, ct);
        if (line is null) return;

        var row = await db.BillingVouchers.FirstAsync(v => v.Id == voucher.Id, ct);
        row.RedeemedWorkspaceId = line.WorkspaceId;
        row.RedeemedByUserId = line.CreatedByUserId;
        row.RedeemedAt ??= line.CreatedAt;
        row.ConcurrencyStamp = Guid.CreateVersion7();
        row.UpdatedAt = clock.UtcNow;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { /* another request recovered the same fact */ }
    }

    internal static string Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new ArgumentException("Enter a voucher code.", nameof(code));

        var normalized = new string(code
            .Where(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            .Select(char.ToUpperInvariant)
            .ToArray());

        if (normalized.Length is < 8 or > 64 || normalized.Any(c => !Alphabet.Contains(c)))
            throw new ArgumentException(
                "Voucher codes must contain 8 to 64 unambiguous Latin letters or digits.", nameof(code));
        return normalized;
    }

    private static string Hash(string normalized) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));

    private static string GenerateCode()
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        var chars = bytes.ToArray().Select(b => Alphabet[b % Alphabet.Length]).ToArray();
        return Display(new string(chars));
    }

    private static string Display(string normalized) => string.Join('-',
        Enumerable.Range(0, (normalized.Length + 4) / 5)
            .Select(i => normalized.Substring(i * 5, Math.Min(5, normalized.Length - i * 5))));
}
