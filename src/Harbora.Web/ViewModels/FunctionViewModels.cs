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
public sealed record FunctionRow(
    Guid Id, string Name, string Slug, FunctionTrigger Trigger, string Route,
    string? CronExpression, string? EventKey, bool IsEnabled, bool HasUnpublishedChanges,
    DateTimeOffset? NextRunAt);

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
}

/// <summary>One past call, as the history table shows it.</summary>
public sealed record FunctionRunRow(
    DateTimeOffset StartedAt, FunctionTrigger Trigger, int? StatusCode, bool Succeeded,
    int DurationMs, string? Error, bool StillRunning);

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
public sealed record FunctionEditViewModel(
    Guid AppId, string AppName, FunctionRuntime Runtime, Guid? FunctionId,
    FunctionFormModel Form, IReadOnlyList<FunctionEventKind> Events,
    IReadOnlyList<FunctionRunRow>? Runs = null,
    bool IsPublished = false,
    bool HasUnpublishedChanges = false,
    IReadOnlyList<FunctionRevisionRow>? Revisions = null,
    string? FunctionUrl = null,
    IReadOnlyList<string>? CustomEventKeys = null);
