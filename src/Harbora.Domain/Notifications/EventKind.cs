namespace Harbora.Domain.Notifications;

/// <summary>
/// Outbound event types a workspace can subscribe to (P6, 2026-08-20 platform-options plan,
/// "Outbound event notifications"). Deliberately not <c>Harbora.Domain.Common.AlertEvent</c>: an
/// <c>Alert</c> only ever fires on a failure or threshold fact a person configured a channel to hear
/// about, so it has never needed a "succeeded" member. An <see cref="EventSubscription"/>'s consumer
/// is code, not a person reading a Telegram message, and code cares about the success half too —
/// <see cref="DeploymentSucceeded"/> and <see cref="BackupSucceeded"/> have no equivalent anywhere in
/// <c>AlertEvent</c> and are not added there; this is a second, narrower vocabulary for a second,
/// narrower audience.
///
/// <para>
/// <b>[Flags], stored as a single bitmask column on <see cref="EventSubscription.Events"/></b> — the
/// plan's own words are "a subscription = target + event mask + enabled flag". <c>Alert</c> instead
/// carries one <c>bool</c> column per event because its event set has grown once in this codebase's
/// history; this one starts wider (eight members day one) and a boolean-per-event copy of that would
/// be eight columns and eight parameters on every controller action before the first new event is
/// even added.
/// </para>
///
/// <para>
/// <b>Persisted by value — append only, next power of two, never renumber or reorder</b> (the same
/// law <c>BackupType</c>'s own doc states and for the same reason: a stored mask compares by bit
/// position, and moving a bit silently changes what every existing subscription is already
/// subscribed to, with no exception and no failed test to notice).
/// </para>
///
/// <para>
/// <see cref="MaintenanceOn"/> and <see cref="MaintenanceOff"/> were reserved for the maintenance-mode
/// toggle (sub-project 5 of the platform-options plan) and are now wired: <c>AppOperationsService.
/// SetMaintenanceModeAsync</c> publishes one of them at the exact seam <c>DeploymentPipeline</c> and
/// <c>BackupEngine</c> publish their own events, once the proxy apply that toggle depends on is known
/// to have succeeded. Both are now in <see cref="Publishable"/> too, and offered as a checkbox on the
/// subscription page — an event that fires with no checkbox to hear it is the same half-connected
/// state as a checkbox that can never fire, just the mirror image of it, and this codebase treats
/// both as the same defect. The members were reserved here rather than appended when they were first
/// wired precisely so that offering them today costs no existing subscription's stored mask a
/// renumbered bit.
/// </para>
/// </summary>
[Flags]
public enum EventKind
{
    None = 0,
    DeploymentSucceeded = 1 << 0,
    DeploymentFailed = 1 << 1,
    AppCrashed = 1 << 2,
    BackupSucceeded = 1 << 3,
    BackupFailed = 1 << 4,
    ServiceFailed = 1 << 5,

    /// <summary>Published by AppOperationsService.SetMaintenanceModeAsync — see the type doc. Offered
    /// on the subscription page as of this plan's follow-up.</summary>
    MaintenanceOn = 1 << 6,
    /// <summary>Published by AppOperationsService.SetMaintenanceModeAsync — see the type doc. Offered
    /// on the subscription page as of this plan's follow-up.</summary>
    MaintenanceOff = 1 << 7,

    /// <summary>Every event this codebase can actually raise today — what the subscription UI offers
    /// as checkboxes.</summary>
    Publishable = DeploymentSucceeded | DeploymentFailed | AppCrashed | BackupSucceeded | BackupFailed |
        ServiceFailed | MaintenanceOn | MaintenanceOff
}
