using Harbora.Domain.Common;
using Harbora.Domain.Functions;

namespace Harbora.Web.ViewModels;

/// <summary>One function app in the list.</summary>
public sealed record FunctionAppRow(
    Guid Id, string Name, string Slug, FunctionRuntime Runtime, AppStatus Status,
    int FunctionCount, bool HasUnpublishedChanges, bool EverPublished);

public sealed record FunctionAppListViewModel(IReadOnlyList<FunctionAppRow> Apps, string? RootDomain);

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
    public string? Code { get; set; }
    public bool IsEnabled { get; set; } = true;
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
public sealed record FunctionEditViewModel(
    Guid AppId, string AppName, FunctionRuntime Runtime, Guid? FunctionId,
    FunctionFormModel Form, IReadOnlyList<FunctionEventKind> Events,
    IReadOnlyList<FunctionRunRow>? Runs = null,
    bool IsPublished = false,
    bool HasUnpublishedChanges = false,
    IReadOnlyList<FunctionRevisionRow>? Revisions = null);
