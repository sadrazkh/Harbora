using Harbora.Domain.Common;

namespace Harbora.Domain.Functions;

/// <summary>
/// Which host image runs a function app's code. Persisted as an int on <c>App</c>; append only.
/// </summary>
public enum FunctionRuntime
{
    CSharp = 0,
    JavaScript = 1,
    Python = 2
}

/// <summary>
/// What makes a function run. Persisted; append only.
///
/// <para>
/// All three end up at the same door — the host only ever receives an HTTP invocation. What differs
/// is who knocks: a visitor, the scheduler, or something that happened inside the platform.
/// </para>
/// </summary>
public enum FunctionTrigger
{
    Http = 0,
    Cron = 1,
    Event = 2,

    /// <summary>
    /// F2 (2026-08-21 functions-and-services plan, "Queue-triggered functions"). A panel-side
    /// <c>BackgroundService</c> — not an amqp client baked into the generated host — consumes one
    /// attached RabbitMQ queue and calls the function through the same signed door every other
    /// trigger uses, so this still ends up at the same one HTTP invocation every other trigger does.
    /// See <see cref="FunctionDefinition.QueueServiceId"/> and <see cref="FunctionDefinition.QueueName"/>.
    /// </summary>
    Queue = 3
}

/// <summary>
/// One function inside a function app.
///
/// <para>
/// The code lives here rather than in a repository because that is the entire proposition: somebody
/// opens the panel, types, and presses publish. Publishing writes these rows into a generated build
/// context and hands it to the ordinary deployment pipeline, so a function app ships, rolls back and
/// streams logs exactly like every other app on the platform.
/// </para>
/// </summary>
public class FunctionDefinition : BaseEntity
{
    /// <summary>The function app this belongs to — an <c>App</c> with <c>SourceType = InlineCode</c>.</summary>
    public Guid AppId { get; set; }

    /// <summary>The workspace, copied from the app so a grant check never needs a join.</summary>
    public Guid WorkspaceId { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// URL-safe identifier, unique inside the app. It names the generated folder, the dispatch
    /// entry and the invocation path, so changing it is changing the function's address.
    /// </summary>
    public string Slug { get; set; } = string.Empty;

    public FunctionTrigger Trigger { get; set; }

    /// <summary>
    /// The path an HTTP function answers on, without a leading slash. Empty means the slug, which
    /// is what almost everybody wants and nobody should have to type.
    /// </summary>
    public string? Route { get; set; }

    /// <summary>Five-field cron expression for a <see cref="FunctionTrigger.Cron"/> function.</summary>
    public string? CronExpression { get; set; }

    /// <summary>A key from <see cref="FunctionEvents"/> for an <see cref="FunctionTrigger.Event"/> function.</summary>
    public string? EventKey { get; set; }

    /// <summary>
    /// The attached broker (a <c>ManagedService</c> of type RabbitMQ, in this function's own
    /// workspace — enforced at save time and re-checked by the consumer itself, the same
    /// belt-and-suspenders the tenant-filter trap this codebase keeps re-learning calls for) a
    /// <see cref="FunctionTrigger.Queue"/> function consumes from. Null for any other trigger.
    /// </summary>
    public Guid? QueueServiceId { get; set; }

    /// <summary>The queue name on <see cref="QueueServiceId"/> a <see cref="FunctionTrigger.Queue"/>
    /// function consumes. Declared (durable, non-exclusive) by the consumer itself if it does not
    /// already exist. Null for any other trigger.</summary>
    public string? QueueName { get; set; }

    /// <summary>
    /// Why the panel's queue consumer most recently could not stay connected to
    /// <see cref="QueueServiceId"/> — null while it is connected, or has never tried. Mirrors
    /// <c>EventSubscription.LastError</c>: the field <c>AttentionService</c> reads to feed a broker
    /// that has gone quiet into the dashboard's existing broken-channel path
    /// (<c>ChannelKind.QueueConsumer</c>), the same way a failing event subscription already does. A
    /// broker being down is not the same fact as a message failing once it is delivered — that is
    /// <see cref="FunctionQueueDeadLetter"/> — this is "nothing is being delivered at all".
    /// </summary>
    public string? QueueLastError { get; set; }

    /// <summary>When the consumer last tried to (re)connect — mirrors <c>EventSubscription.LastAttemptAt</c>.</summary>
    public DateTimeOffset? QueueLastAttemptAt { get; set; }

    /// <summary>The source, as typed.</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// Off means the code stays but nothing calls it — the switch somebody reaches for at 3am. A
    /// disabled function is still published into the image, so turning it back on needs no rebuild.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Computed by the scheduler so a due function is found without re-parsing every row.</summary>
    public DateTimeOffset? NextRunAt { get; set; }

    /// <summary>
    /// Set when the code changes and cleared when a deployment carrying it succeeds, so the page can
    /// say "edited, not published" — the state a function editor is in most of the time, and the one
    /// a person is most likely to mistake for "live".
    /// </summary>
    public bool HasUnpublishedChanges { get; set; }

    /// <summary>
    /// Off (the default) is exactly as closed as every function was before this flag existed: only
    /// the panel's own signed door (<see cref="FunctionInvocation"/> via <c>FunctionInvoker</c>) can
    /// reach it — cron ticks, platform events and a manual "Run now" all arrive that way regardless of
    /// this flag. On opens a second, unauthenticated route at the function's own URL, for an
    /// <see cref="FunctionTrigger.Http"/> function that needs to answer a visitor directly — a
    /// payment callback, an SMS delivery report, a webhook. Meaningless for <see cref="FunctionTrigger.Cron"/>
    /// or <see cref="FunctionTrigger.Event"/>, which never sit behind a visitor route to begin with.
    /// </summary>
    public bool IsPublic { get; set; }
}

/// <summary>
/// Who saw one <see cref="FunctionInvocation"/> happen.
///
/// <para>
/// Every call the platform makes itself — a schedule, a platform or custom event, a manual "Run
/// now" — is watched end to end: <c>FunctionInvoker</c> writes the row before it dials out and
/// completes it from the response it gets back. A public HTTP call (F1, 2026-08-21
/// functions-and-services plan) is the one shape that is not: a visitor reaches the generated host
/// directly, the panel is never on that path, and the only account of what happened is whatever the
/// host itself chooses to say afterwards. This is what keeps the second kind from silently looking
/// like the first — a row nobody watched must say so, not borrow the credibility of one somebody did.
/// </para>
/// </summary>
public enum FunctionInvocationOrigin
{
    /// <summary>The panel made this call and watched it happen — every row before this field existed
    /// was one of these, which is why it is the default.</summary>
    Panel = 0,

    /// <summary>
    /// The generated host answered a visitor's call to this function's public URL and reported back
    /// what it did, fire-and-forget, authenticated with the same secret it already holds
    /// (<see cref="Harbora.Infrastructure.Functions.FunctionProject.SecretEnvVar"/>). Best-effort: if
    /// the panel could not be reached at that moment, there is no row at all rather than a wrong one —
    /// the host never delays or fails a visitor's own response waiting to find out.
    /// </summary>
    PublicCall = 1
}

/// <summary>
/// One call of one function, however it was triggered.
///
/// <para>
/// Recorded for every invocation because a function that quietly stops firing is this feature's
/// worst failure mode: nothing errors, nothing alerts, and the first symptom is a report that never
/// arrived. A row per call makes "when did this last run, and what did it say" answerable.
/// </para>
/// </summary>
public class FunctionInvocation : BaseEntity
{
    public Guid FunctionId { get; set; }
    public Guid AppId { get; set; }
    public Guid WorkspaceId { get; set; }

    public FunctionTrigger Trigger { get; set; }

    /// <summary>Who saw this call happen — see <see cref="FunctionInvocationOrigin"/>. Defaults to
    /// <see cref="FunctionInvocationOrigin.Panel"/>, which is exactly right for every row written
    /// before this field existed: they were all panel-made.</summary>
    public FunctionInvocationOrigin Origin { get; set; }

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public int DurationMs { get; set; }

    /// <summary>
    /// What to send, for an invocation the panel makes itself. Written before the job is queued, so
    /// the queue keeps carrying only a kind and an id and a scheduled call still fires after a
    /// restart that landed between the tick and the request.
    ///
    /// <para>Null for an HTTP call, which the platform observes rather than makes.</para>
    /// </summary>
    public string? EnvelopeJson { get; set; }

    /// <summary>
    /// Null while the call is still queued or in flight. A row that has been pending far longer than
    /// any function may run is a call that was lost, and it is distinguishable from a failure.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Null when the host could not be reached at all, which is a different failure from a 500.</summary>
    public int? StatusCode { get; set; }

    public bool Succeeded { get; set; }

    /// <summary>Short reason for a failure, already redacted. Never the whole response body.</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One past save of a function's code. Immutable, in the same shape <c>Deployment</c> already uses
/// for its own history — a restore is a new save that happens to carry an old body, never a rewrite
/// of a row that already exists — so the table only ever grows by insert, and reading "what was this
/// five saves ago" never has to distrust a row somebody might have touched since.
///
/// <para>
/// <see cref="BaseEntity.CreatedAt"/> is when this version was written; there is no
/// <c>UpdatedAt</c> use here, because a revision is never updated after the fact.
/// </para>
/// </summary>
public class FunctionCodeRevision : BaseEntity
{
    public Guid FunctionId { get; set; }

    /// <summary>Copied from the function, the same way every other per-function child table does it.</summary>
    public Guid WorkspaceId { get; set; }

    /// <summary>The function's code exactly as it stood the moment this revision was written.</summary>
    public string Code { get; set; } = string.Empty;
}

/// <summary>
/// One queue message F2's consumer could not get a function to accept twice in a row — delivered,
/// the function failed; redelivered once (the broker's own <c>Redelivered</c> flag, not a counter
/// this class keeps), it failed again. Parked here rather than dropped and acked away, because a
/// queue consumer that drops a failed message and moves on is the defect class this codebase has
/// spent weeks removing: the customer believes their message was processed, and it never was.
///
/// <para>
/// Surfaced on the function's own page (F2's acceptance criterion), not folded into
/// <see cref="FunctionInvocation"/>: an invocation row is one HTTP call the platform made and can
/// point at a specific attempt; a dead letter is the broker message itself, which by definition
/// caused two of those attempts (or, for the first attempt, none — see
/// <see cref="Harbora.Infrastructure.Functions.QueueFunctionConsumerHost"/>'s handling of a function
/// that stopped being callable between the two attempts) and needs to hold the payload a person
/// would want back to replay it by hand.
/// </para>
/// </summary>
public class FunctionQueueDeadLetter : BaseEntity
{
    public Guid FunctionId { get; set; }
    public Guid AppId { get; set; }
    public Guid WorkspaceId { get; set; }

    /// <summary>The queue this arrived on, copied at park time — <see cref="FunctionDefinition.QueueName"/>
    /// may since have changed.</summary>
    public string QueueName { get; set; } = string.Empty;

    /// <summary>The message body exactly as the broker delivered it, so a person can inspect it or
    /// replay it by hand once the handler is fixed.</summary>
    public string Body { get; set; } = string.Empty;

    /// <summary>Why the second attempt also failed — the invocation's own <c>Error</c>, copied so this
    /// row explains itself without a join back to a row the invocation-retention sweeper may have
    /// already pruned.</summary>
    public string Reason { get; set; } = string.Empty;
}
