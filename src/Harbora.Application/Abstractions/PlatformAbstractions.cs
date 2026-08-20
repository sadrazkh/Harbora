namespace Harbora.Application.Abstractions;

/// <summary>Testable clock.</summary>
public interface ISystemClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Collects host + container metrics into the monitoring store.</summary>
public interface IMetricsCollector
{
    Task CollectAsync(CancellationToken ct);
}

/// <summary>Runs, restores, downloads and prunes backups against a destination.</summary>
public interface IBackupEngine
{
    /// <summary>Create the backup row and queue the work on the background worker; returns the backup id.</summary>
    Task<Guid> QueueBackupAsync(Guid workspaceId, Domain.Common.BackupType type, string targetRef, Guid destinationId, bool scheduled, CancellationToken ct);

    /// <summary>Restore a completed backup. Destructive — callers must confirm first.</summary>
    Task RestoreAsync(Guid backupId, CancellationToken ct);

    /// <summary>
    /// Dry run: fetch the artifact and check it is intact and readable WITHOUT touching live data.
    /// A backup nobody has ever verified is a promise, not a safety net.
    /// </summary>
    Task<BackupVerification> VerifyAsync(Guid backupId, CancellationToken ct);

    /// <summary>Open a completed backup's artifact for download.</summary>
    Task<(Stream Stream, string FileName)> OpenArtifactAsync(Guid backupId, CancellationToken ct);

    /// <summary>Apply retention rules (delete artifacts + rows past the keep window/count).</summary>
    Task EnforceRetentionAsync(CancellationToken ct);

    /// <summary>
    /// Remove one backup: the stored artifact first, then the row.
    ///
    /// <para>
    /// That order, and it matters. The row is the only record of where the artifact is, so dropping it
    /// first and then failing to reach the destination leaves bytes nobody can find, name or account
    /// for — and on a paid destination, bytes that are charged for indefinitely. A destination that
    /// refuses the delete therefore leaves the row alone and says so, which is a state somebody can
    /// retry.
    /// </para>
    ///
    /// <para>
    /// Deleting the last copy of something is what retention already does on a timer; this is the same
    /// operation asked for by hand, so the caller owns the confirmation.
    /// </para>
    /// </summary>
    Task DeleteAsync(Guid backupId, CancellationToken ct);

    /// <summary>
    /// Store an archive somebody uploaded as a backup of <paramref name="targetRef"/>, and return the
    /// new backup's id.
    ///
    /// <para>
    /// <b>The bytes are stored exactly as given.</b> Not re-encrypted and not repackaged: an artifact
    /// downloaded from Harbora is already in its stored form, so passing it through the encryption
    /// step again would wrap it twice and no restore would read it. The checksum is taken over what is
    /// actually written, which is what verification later recomputes.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing here judges whether the archive is a real Harbora artifact.</b> It cannot: the file
    /// may be encrypted, and inspecting it would mean decrypting it. The row is recorded as an import
    /// and left unverified, so the panel can offer the dry run that answers the question honestly
    /// rather than implying an answer by having accepted the upload.
    /// </para>
    /// </summary>
    Task<Guid> ImportAsync(
        Guid workspaceId,
        Domain.Common.BackupType type,
        string targetRef,
        Guid destinationId,
        string fileName,
        Stream content,
        CancellationToken ct);

    /// <summary>
    /// Write a small probe to a destination and delete it again. Null means it worked; anything else is
    /// the reason it did not, in a sentence.
    ///
    /// <para>
    /// A real round trip rather than a settings check, because every way a destination fails —
    /// credentials, a bucket that is not there, a directory nothing may write to, a host key that does
    /// not match — looks identical to a correct form until something is actually sent. Finding out at
    /// the first real backup means finding out when the backup was needed.
    /// </para>
    /// </summary>
    Task<string?> TestDestinationAsync(Guid destinationId, CancellationToken ct);
}

/// <summary>
/// Outcome of a dry-run verification. <paramref name="Checks"/> lists every individual check so the
/// UI can show what passed, not just a verdict.
/// </summary>
public record BackupVerification(
    bool IsRestorable,
    string? Reason,
    long SizeBytes,
    IReadOnlyList<BackupCheck> Checks)
{
    public static BackupVerification Failed(string reason, params BackupCheck[] checks) =>
        new(false, reason, 0, checks);
}

/// <summary>
/// One thing that was checked about a backup.
///
/// <paramref name="Skipped"/> exists because "not checked" and "checked and fine" must never look
/// the same on a screen — a Redis snapshot cannot be restored into a scratch database, and saying so
/// is honest, while showing a failed check would be alarming and showing a passed one would be a lie.
/// </summary>
public record BackupCheck(string Name, bool Passed, string? Detail = null, bool Skipped = false);

/// <summary>
/// What happened when a notification was handed to a channel.
///
/// The point of returning this is that "sent" used to mean "we called something and did not crash":
/// a webhook answering 404 counted as delivered, and the panel's Test button reported success
/// unconditionally. A channel nobody can tell is broken is worse than no channel.
/// </summary>
public sealed record NotificationResult(bool Delivered, string? Error = null)
{
    public static readonly NotificationResult Ok = new(true);
    public static NotificationResult Failed(string error) => new(false, error);
}

/// <summary>Fan-out for alerts across configured channels (email/Telegram/Discord/webhook).</summary>
public interface INotificationService
{
    /// <summary>
    /// Deliver a notification to every enabled alert in the workspace that opted into this event
    /// and whose minimum severity is satisfied. Best-effort — channel failures are logged, not thrown.
    ///
    /// <para>
    /// Answers how many rules it was handed to, and that number is the only way a caller can tell
    /// the difference between "delivered" and "there was nobody to deliver to". Nothing seeds an
    /// alert rule — the alerts page is the only thing in the product that creates one — so a
    /// workspace with none is the ordinary case, not an edge one, and against it this method
    /// iterates nothing, raises nothing and returns. A caller that discards the count records a
    /// notification sent to nobody; the failure of a channel that <i>exists</i> is a different fact
    /// and is written back onto the rule, where a broken channel is meant to be read.
    /// </para>
    ///
    /// <para>
    /// N4 (2026-08-16 notification-system spec, "in the reader's own language"): <paramref name="evt"/>
    /// carries what happened and what it happened to, not a pre-built sentence — a raise site's job
    /// stops at the facts, and this renders them per recipient, in that recipient's own
    /// <c>User.PreferredCulture</c>, via <c>INotificationTemplateCatalog</c>.
    /// </para>
    /// </summary>
    Task<int> NotifyAsync(Guid workspaceId, Domain.Notifications.NotificationEventData evt, Domain.Common.AlertSeverity severity, CancellationToken ct);

    /// <summary>
    /// Deliver through one specific rule, whatever its event opt-ins say.
    ///
    /// A per-application threshold belongs to the rule that defines it: broadcasting it to every
    /// channel in the workspace would tell people who never asked about that app, and matching it
    /// against the event flags would require a sixth flag nobody set. Used by the threshold
    /// evaluator, which already holds the row.
    /// </summary>
    Task<NotificationResult> NotifyRuleAsync(
        Guid alertId, Domain.Notifications.NotificationEventData evt, Domain.Common.AlertSeverity severity, CancellationToken ct);

    /// <summary>Send a one-off test message to a single alert (for the "test" button).</summary>
    /// <summary>
    /// Sends a test notification and reports what actually happened, so the panel can say "that URL
    /// returned 404" instead of "sent".
    /// </summary>
    Task<NotificationResult> SendTestAsync(Guid alertId, CancellationToken ct);

    /// <summary>
    /// One attempt at one queued <c>NotificationDelivery</c> row (N1, 2026-08-16 notification-system
    /// spec) — the job body <c>NotificationDeliveryJobHandler</c> runs. Updates the row regardless of
    /// outcome and rethrows on failure, so the job worker's own retry/backoff policy decides whether
    /// it runs again.
    /// </summary>
    Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct);

    /// <summary>
    /// Sends one Telegram message through the exact HTTP call, target-JSON shape
    /// (<c>{"botToken":"...","chatId":"..."}</c>, decrypted from <paramref name="encryptedTarget"/>)
    /// and response handling an <c>Alert</c>'s own Telegram channel already uses — reused, not
    /// forked, by <c>EventDispatcher</c> (P6, 2026-08-20 platform-options plan, "event subscriptions,
    /// not channels") so a Telegram <c>EventSubscription</c> goes out exactly the way a Telegram
    /// <c>Alert</c> already does. Throws <c>Harbora.Infrastructure.Notifications.NotificationChannelException</c>
    /// on a non-2xx response or on the shared delivery timeout, same as every other channel here.
    /// </summary>
    Task SendTelegramAsync(string encryptedTarget, string title, string body, CancellationToken ct);
}

/// <summary>
/// Publish seam for outbound event subscriptions (P6, 2026-08-20 platform-options plan). Deliberately
/// not <see cref="INotificationService"/>: that interface fans a raise site's fact out to <c>Alert</c>
/// rules, which are a person's own configured channels; this fans the same kind of fact out to
/// <c>EventSubscription</c> rows, which are a workspace's *other* workspaces or systems. The two are
/// raised from the same lifecycle seams (a deploy pipeline, a backup engine, the crash reconciler,
/// managed-service provisioning) but are two different audiences with two different vocabularies
/// (<see cref="Domain.Common.AlertEvent"/> has no "succeeded" member; <see cref="Domain.Notifications.EventKind"/>
/// does), so they stay two calls at each seam rather than one call trying to serve both.
///
/// <para>
/// <b>Enqueue only.</b> <see cref="PublishAsync"/> never makes an HTTP call itself — it matches
/// enabled, subscribed rows and writes durable <see cref="Domain.Notifications.EventDelivery"/> rows,
/// the actual send happening later on the job queue (<c>EventDispatcher.ExecuteQueuedDeliveryAsync</c>,
/// registered as an <c>IJobHandler</c>). The plan's own words: "the publish seams must not slow or
/// fail the acts they observe" — a deploy pipeline or backup engine that awaited a customer's webhook
/// endpoint directly would hang or fail on that endpoint's schedule, not its own. Also never throws:
/// every raise site treats this exactly as it already treats <see cref="INotificationService.NotifyAsync"/> —
/// best-effort, logged, not allowed to turn a successful deploy or backup into a failed one.
/// </para>
/// </summary>
public interface IEventPublisher
{
    /// <param name="workspaceId">Whose subscriptions to match — always the workspace the underlying
    /// fact belongs to, stamped by the caller exactly as every other raise site in this codebase
    /// stamps a WorkspaceId onto background work.</param>
    /// <param name="kind">Exactly one <see cref="Domain.Notifications.EventKind"/> bit — the event
    /// that happened, matched against each subscription's own mask.</param>
    /// <param name="resource">Named facts about what the event happened to (app name, deployment
    /// number, error text, …) — the same flat-bag-of-strings idiom
    /// <see cref="Domain.Notifications.NotificationEventData"/> already uses, carried verbatim into
    /// the delivered JSON payload's own fields.</param>
    Task PublishAsync(
        Guid workspaceId, Domain.Notifications.EventKind kind,
        IReadOnlyDictionary<string, string> resource, CancellationToken ct);

    /// <summary>
    /// One attempt at one queued <c>EventDelivery</c> row — the job body
    /// <c>EventDeliveryJobHandler</c> runs (the same thin <c>IJobHandler</c> adapter shape as
    /// <c>NotificationDeliveryJobHandler</c> over <see cref="INotificationService.ExecuteQueuedDeliveryAsync"/>).
    /// Updates the row regardless of outcome and rethrows on a retryable failure, so
    /// <c>Harbora.Infrastructure.Jobs.JobExecutionPolicy</c>'s retry/backoff decides whether it runs
    /// again.
    /// </summary>
    Task ExecuteQueuedDeliveryAsync(Guid deliveryId, CancellationToken ct);
}
