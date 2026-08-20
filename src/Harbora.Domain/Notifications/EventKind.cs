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
/// <see cref="MaintenanceOn"/> and <see cref="MaintenanceOff"/> are reserved for the maintenance-mode
/// toggle (sub-project 5 of the platform-options plan) — that feature does not exist on this branch,
/// so nothing publishes them yet and the subscription UI does not offer them as a checkbox: showing a
/// box that can never be ticked into firing is the fabricated-capability defect class this codebase
/// has spent weeks removing. The members are reserved here now, rather than appended later, so that
/// a subscription's stored mask never needs its bit positions renumbered once maintenance mode ships.
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

    /// <summary>Reserved — see the type doc. Not publishable yet; not offered in the subscription UI.</summary>
    MaintenanceOn = 1 << 6,
    /// <summary>Reserved — see the type doc. Not publishable yet; not offered in the subscription UI.</summary>
    MaintenanceOff = 1 << 7,

    /// <summary>Every event this codebase can actually raise today — what the subscription UI offers
    /// as checkboxes. Excludes the two reserved members above.</summary>
    Publishable = DeploymentSucceeded | DeploymentFailed | AppCrashed | BackupSucceeded | BackupFailed | ServiceFailed
}
