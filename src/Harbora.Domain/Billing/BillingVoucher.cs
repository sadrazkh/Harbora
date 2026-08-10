using Harbora.Domain.Common;

namespace Harbora.Domain.Billing;

/// <summary>
/// A single-use balance voucher. Only a SHA-256 digest is stored; the redeemable code is returned
/// once to the administrator that creates it and cannot be recovered from the database later.
/// </summary>
public sealed class BillingVoucher : BaseEntity
{
    public string CodeHash { get; set; } = string.Empty;
    public string CodeHint { get; set; } = string.Empty;
    public long AmountMinor { get; set; }
    public string Currency { get; set; } = "IRR";
    public string Note { get; set; } = string.Empty;
    public Guid CreatedByUserId { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public bool IsDisabled { get; set; }

    public DateTimeOffset? RedeemedAt { get; set; }
    public Guid? RedeemedByUserId { get; set; }
    public Guid? RedeemedWorkspaceId { get; set; }

    /// <summary>Prevents two workspaces from both winning the same unused voucher.</summary>
    public Guid ConcurrencyStamp { get; set; } = Guid.CreateVersion7();
}
