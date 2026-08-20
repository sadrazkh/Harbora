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
    Event = 2
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
