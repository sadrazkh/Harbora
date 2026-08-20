using Harbora.Domain.Common;

namespace Harbora.Domain.Storage;

/// <summary>What a bucket is doing, so a half-made one is never shown as usable.</summary>
public enum BucketStatus
{
    /// <summary>Asked for; the storage server has not confirmed it yet.</summary>
    Provisioning = 0,

    /// <summary>The bucket and its credential exist.</summary>
    Ready = 1,

    /// <summary>Provisioning did not finish. The reason is on the row.</summary>
    Failed = 2
}

/// <summary>
/// One S3 bucket belonging to a workspace, with its own credential and its own ceiling.
///
/// A tier rather than a share of one: each bucket has a key that can reach it and nothing else, so
/// a leaked credential is a leaked bucket rather than a leaked platform. The quota is copied onto
/// the row when it is created, for the same reason an instance's memory limit is — the plan can be
/// edited later, and a page reporting "8 GB of 20 GB" has to go on meaning what it meant.
/// </summary>
public class StorageBucket : BaseEntity
{
    public Guid WorkspaceId { get; set; }

    /// <summary>The bucket's name on the storage server. Unique across the platform.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The access key issued for this bucket. Not a secret on its own.</summary>
    public string AccessKey { get; set; } = string.Empty;

    /// <summary>The secret, encrypted with the platform key like every other stored credential.</summary>
    public string EncryptedSecretKey { get; set; } = string.Empty;

    public Guid? StoragePlanId { get; set; }
    public StoragePlan? StoragePlan { get; set; }

    /// <summary>The ceiling, copied from the plan at creation. Zero means no ceiling.</summary>
    public long QuotaBytes { get; set; }

    /// <summary>
    /// What the bucket is measured to hold, and when. Null means never measured, which is not the
    /// same as empty and is shown differently.
    /// </summary>
    public long? UsedBytes { get; set; }
    public DateTimeOffset? MeasuredAt { get; set; }

    public BucketStatus Status { get; set; } = BucketStatus.Provisioning;

    /// <summary>Why provisioning failed, for the screen. Null when nothing went wrong.</summary>
    public string? FailureReason { get; set; }

    /// <summary>Apps attached to this bucket (F5, 2026-08-21 functions-and-services plan) — see
    /// <see cref="AppStorageBucket"/>.</summary>
    public ICollection<AppStorageBucket> Apps { get; set; } = new List<AppStorageBucket>();
}

/// <summary>
/// A storage tier somebody can be put on.
///
/// Deliberately its own thing rather than a field on <see cref="Harbora.Domain.Tenancy.Plan"/>:
/// object storage is bought in different amounts from compute, by people who may not want more
/// compute at all, and folding it into the compute plan would mean the only way to buy more space
/// is to buy more memory.
/// </summary>
public class StoragePlan : BaseEntity
{
    public string Name { get; set; } = string.Empty;
    public string NameFa { get; set; } = string.Empty;

    /// <summary>How much one bucket on this tier may hold. Zero means no ceiling.</summary>
    public long QuotaBytes { get; set; }

    /// <summary>How many buckets a workspace on this tier may have. Zero means no limit.</summary>
    public int MaxBuckets { get; set; }

    /// <summary>For display. Harbora does not charge.</summary>
    public decimal MonthlyPrice { get; set; }

    public bool IsDefault { get; set; }
    public bool IsEnabled { get; set; } = true;
    public int SortOrder { get; set; }
}
