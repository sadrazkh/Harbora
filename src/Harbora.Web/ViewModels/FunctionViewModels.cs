using Harbora.Domain.Common;
using Harbora.Domain.Functions;

namespace Harbora.Web.ViewModels;

/// <summary>One function app in the list.</summary>
public sealed record FunctionAppRow(
    Guid Id, string Name, string Slug, FunctionRuntime Runtime, AppStatus Status,
    int FunctionCount, bool HasUnpublishedChanges, bool EverPublished);

/// <summary>
/// One <c>custom.*</c> key a workspace's own apps have raised (F3, 2026-08-21 functions-and-services
/// plan). <paramref name="SubscriberCount"/> is what turns "seen" into actionable: zero says plainly
/// that the key arrived and nothing is listening yet, rather than leaving the page silent about it.
/// </summary>
public sealed record FunctionCustomEventKeyRow(
    string Key, int TimesSeen, DateTimeOffset LastSeenAt, int SubscriberCount);

public sealed record FunctionAppListViewModel(
    IReadOnlyList<FunctionAppRow> Apps, string? RootDomain,
    IReadOnlyList<FunctionCustomEventKeyRow>? CustomEvents = null);

/// <summary>What the create form posts.</summary>
public sealed class FunctionAppFormModel
{
    public string Name { get; set; } = string.Empty;
    public FunctionRuntime Runtime { get; set; } = FunctionRuntime.CSharp;
    public string? InstanceSizeKey { get; set; }
}

public sealed record FunctionSizeOption(string Key, string Name, long MemoryBytes, double CpuCores);

public sealed record FunctionAppFormViewModel(
    FunctionAppFormModel Form, IReadOnlyList<FunctionSizeOption> Sizes);

/// <summary>One function on its app's page.</summary>
/// <param name="IsPublic">
/// Whether this <see cref="FunctionTrigger.Http"/> function answers a visitor directly — see
/// <c>FunctionDefinition.IsPublic</c>. Meaningless (always false) for any other trigger, which never
/// sits behind the visitor route this flag gates. Owner visibility follow-up (2026-08-21
/// functions-and-services plan): F1 shipped this toggle but the function list never showed its state,
/// which is most of why the owner could not find it.
/// </param>
/// <param name="FunctionUrl">
/// The exact address a visitor would use to reach this function, copy-ready — the same computation
/// <c>FunctionEditViewModel.FunctionUrl</c> already makes, so the list and the editor can never
/// disagree. Null unless <paramref name="IsPublic"/> and the app has a host.
/// </param>
public sealed record FunctionRow(
    Guid Id, string Name, string Slug, FunctionTrigger Trigger, string Route,
    string? CronExpression, string? EventKey, bool IsEnabled, bool HasUnpublishedChanges,
    DateTimeOffset? NextRunAt,
    /// <summary>F2: the queue/broker this function consumes, formatted "queue @ broker" — null for
    /// any other trigger. Already resolved to a name here so the list page never has to join.</summary>
    string? QueueSummary = null,
    /// <summary>F2: why the consumer most recently could not stay connected — null while connected
    /// or never tried. Mirrors <c>FunctionDefinition.QueueLastError</c>.</summary>
    string? QueueLastError = null,
    /// <summary>F2: how many dead letters are waiting on this function's own page.</summary>
    int DeadLetterCount = 0,
    bool IsPublic = false,
    string? FunctionUrl = null);

/// <summary>One RabbitMQ service a Queue-triggered function may consume from — this workspace's own,
/// already filtered, so the editor's dropdown can never even offer another workspace's.</summary>
public sealed record QueueBrokerOption(Guid Id, string Name);

/// <summary>
/// One message F2's consumer could not get this function to accept twice in a row, as the function's
/// own page shows it.
/// </summary>
public sealed record FunctionDeadLetterRow(Guid Id, DateTimeOffset CreatedAt, string Body, string Reason);

public sealed record FunctionAppDetailsViewModel(
    Guid Id, string Name, string Slug, FunctionRuntime Runtime, AppStatus Status,
    bool EverPublished, string? Host, IReadOnlyList<FunctionRow> Functions);

/// <summary>What the editor posts.</summary>
public sealed class FunctionFormModel
{
    public string? Name { get; set; }
    public FunctionTrigger Trigger { get; set; }
    public string? Route { get; set; }
    public string? CronExpression { get; set; }
    public string? EventKey { get; set; }

    /// <summary>
    /// A raw key typed for a <c>custom.*</c> subscription, not yet namespaced. Takes precedence over
    /// <see cref="EventKey"/> when non-empty — typing a new key is a more specific act than whatever
    /// the select happened to have selected. See <see cref="Harbora.Domain.Functions.FunctionEvents.NormaliseCustomKey"/>
    /// for what the server does with it.
    /// </summary>
    public string? CustomEventKey { get; set; }

    public string? Code { get; set; }
    public bool IsEnabled { get; set; } = true;

    /// <summary>Only meaningful for an HTTP trigger — see <see cref="FunctionDefinition.IsPublic"/>.</summary>
    public bool IsPublic { get; set; }

    /// <summary>Only meaningful for a Queue trigger — see <see cref="FunctionDefinition.QueueServiceId"/>.</summary>
    public Guid? QueueServiceId { get; set; }

    /// <summary>Only meaningful for a Queue trigger — see <see cref="FunctionDefinition.QueueName"/>.</summary>
    public string? QueueName { get; set; }
}

/// <summary>One past call, as the history table shows it.</summary>
/// <param name="Origin">
/// Who saw this call happen (<see cref="FunctionInvocationOrigin"/>) — <c>Panel</c> for everything the
/// panel made itself and watched (schedules, events, Run now), <c>PublicCall</c> for a visitor who
/// reached this function's own public URL directly, which the panel only knows about because the host
/// reported it afterwards (F1 reversal, 2026-08-21 functions-and-services plan follow-up). Carried
/// through so the row can say which kind it is, rather than let a host-reported row read as though the
/// panel had watched it happen the same way it watches everything else.
/// </param>
public sealed record FunctionRunRow(
    DateTimeOffset StartedAt, FunctionTrigger Trigger, int? StatusCode, bool Succeeded,
    int DurationMs, string? Error, bool StillRunning, FunctionInvocationOrigin Origin = FunctionInvocationOrigin.Panel);

/// <summary>One kept revision of a function's code, as the editor's history panel shows it.</summary>
public sealed record FunctionRevisionRow(Guid Id, DateTimeOffset CreatedAt, bool IsCurrent);

/// <param name="IsPublished">
/// Whether this app has ever been published — <c>App.ActiveDeploymentId is not null</c>, which is the
/// same condition <c>FunctionInvoker.QueueAsync</c> checks before it will run anything. The editor
/// disables Run now when this is false rather than letting the press fail: an unpublished app cannot
/// run, and saying so on the button is clearer than saying it after.
/// </param>
/// <param name="HasUnpublishedChanges">
/// Whether the saved row differs from what is deployed. Run now invokes the <em>published</em> code,
/// so when this is true the editor says so beside the button — running is instant and honest, and the
/// alternative (rebuilding on every press) would make Run now a second name for Publish.
/// </param>
/// <param name="FunctionUrl">
/// The exact address a visitor would use to reach this function once it is <c>Public</c> and
/// published — <c>https://{app's own host}/{FunctionProject.RouteFor(fn)}</c>, the very route the
/// generated host answers on, so the page and the container cannot disagree. Null when the app has
/// no host yet (never assigned, or the platform has no root domain configured) or the function is not
/// an existing HTTP trigger — there is nothing honest to show yet.
/// </param>
/// <param name="CustomEventKeys">
/// Every <c>custom.*</c> key this workspace's own apps have raised, for the Event picker's own
/// "Custom" group (F3, 2026-08-21 functions-and-services plan). Already namespaced.
/// </param>
/// <param name="AvailableBrokers">
/// The RabbitMQ services this workspace has — everything the Queue trigger's dropdown may offer. An
/// empty list is itself informative: the editor says so, rather than rendering an empty select
/// nobody can act on.
/// </param>
/// <param name="QueueLastError">
/// F2: why the consumer most recently could not stay connected to this function's attached broker —
/// null while connected or never tried. Mirrors <c>FunctionDefinition.QueueLastError</c>.
/// </param>
/// <param name="DeadLetters">F2: messages the consumer could not get this function to accept twice in a row.</param>
public sealed record FunctionEditViewModel(
    Guid AppId, string AppName, FunctionRuntime Runtime, Guid? FunctionId,
    FunctionFormModel Form, IReadOnlyList<FunctionEventKind> Events,
    IReadOnlyList<FunctionRunRow>? Runs = null,
    bool IsPublished = false,
    bool HasUnpublishedChanges = false,
    IReadOnlyList<FunctionRevisionRow>? Revisions = null,
    string? FunctionUrl = null,
    IReadOnlyList<string>? CustomEventKeys = null,
    IReadOnlyList<QueueBrokerOption>? AvailableBrokers = null,
    string? QueueLastError = null,
    IReadOnlyList<FunctionDeadLetterRow>? DeadLetters = null);
