using Harbora.Domain.Storage;

namespace Harbora.Web.ViewModels;

/// <param name="SecretKey">Non-null only for the one bucket somebody clicked to reveal.</param>
/// <param name="UsedBytes">Null means never measured, which is shown differently from empty.</param>
/// <param name="AttachedApps">Apps this bucket is currently attached to (F5, 2026-08-21
/// functions-and-services plan). Carries the app's id, unlike <see cref="AttachedAppRow"/>'s
/// config-group shape, because the detach form here lives on the bucket's own page and needs it to
/// post — a config group's detach form lives on the app's own page instead, where the id is
/// already the route.</param>
/// <param name="AvailableApps">Apps in this workspace not yet attached to this bucket, for the
/// attach form.</param>
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
    string? PlanName,
    IReadOnlyList<StorageAttachedAppViewModel> AttachedApps,
    IReadOnlyList<StorageAttachableAppViewModel> AvailableApps);

/// <summary>One app a bucket could be attached to next (F5, 2026-08-21 functions-and-services plan).</summary>
public sealed record StorageAttachableAppViewModel(Guid Id, string Name);

/// <summary>One app a bucket is currently attached to (F5, 2026-08-21 functions-and-services plan).</summary>
public sealed record StorageAttachedAppViewModel(Guid AppId, string AppName, bool HasUnpublishedChanges);

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
