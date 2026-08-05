using Harbora.Domain.Storage;

namespace Harbora.Web.ViewModels;

/// <param name="SecretKey">Non-null only for the one bucket somebody clicked to reveal.</param>
/// <param name="UsedBytes">Null means never measured, which is shown differently from empty.</param>
public sealed record StorageBucketViewModel(
    Guid Id,
    string Name,
    string AccessKey,
    string? SecretKey,
    long QuotaBytes,
    long? UsedBytes,
    DateTimeOffset? MeasuredAt,
    BucketStatus Status,
    string? FailureReason,
    string? PlanName);

public sealed record StoragePageViewModel
{
    /// <summary>Whether object storage exists on this installation at all.</summary>
    public bool IsConfigured { get; init; }

    /// <summary>Which configuration keys are empty, named individually. Null when none are.</summary>
    public string? WhatIsMissing { get; init; }

    /// <summary>What a customer should point their client at.</summary>
    public string Endpoint { get; init; } = string.Empty;

    public IReadOnlyList<StoragePlan> Plans { get; init; } = [];
    public IReadOnlyList<StorageBucketViewModel> Buckets { get; init; } = [];
    public Guid? RevealedBucketId { get; init; }
}
