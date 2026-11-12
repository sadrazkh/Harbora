using Harbora.Domain.Common;
using Harbora.Infrastructure.Launch;
using Harbora.Infrastructure.Services;
using Harbora.Shared.Inspection;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace Harbora.Web.ViewModels;

/// <summary>The question's two forms. Exactly one of the URL and the file arrives: each has its own form.</summary>
public sealed class LaunchStartForm
{
    public string? GitUrl { get; set; }
    public string? GitRef { get; set; }
    public string? GitToken { get; set; }
    public IFormFile? File { get; set; }

    /// <summary>
    /// The picker's button: <c>{account id}|{clone url}</c>. One form with a button per repository, so a
    /// long list carries one antiforgery token rather than one per row, and the button that was pressed
    /// is the only one whose value is posted.
    /// </summary>
    public string? Repository { get; set; }

    public static string PickedValue(Guid accountId, string cloneUrl) => $"{accountId:D}|{cloneUrl}";

    public static (Guid AccountId, string CloneUrl)? ReadPicked(string? value)
    {
        var bar = value?.IndexOf('|') ?? -1;
        return bar > 0 && Guid.TryParse(value![..bar], out var id) && value[(bar + 1)..].Trim() is { Length: > 0 } url
            ? (id, url)
            : null;
    }
}

/// <summary>What <c>/launch</c> draws. The token is not here: what a person typed into it is never drawn back.</summary>
/// <param name="ReadingFailed">The last attempt could not be read, and <paramref name="Message"/> says why.</param>
/// <param name="AskedForDetails">Open the private-repository fold: there is something in it, or something missing from it.</param>
/// <param name="Accounts">Each connected Git account's repositories, or why they could not be listed. Empty with none connected, and then the page is the paste field it always was.</param>
/// <param name="Filter">What the list was narrowed to, when somebody searched.</param>
public sealed record LaunchQuestionModel(
    string? GitUrl = null,
    string? GitRef = null,
    string? Message = null,
    bool ReadingFailed = false,
    bool AskedForDetails = false,
    IReadOnlyList<Harbora.Infrastructure.Git.AccountRepositories>? Accounts = null,
    string? Filter = null);

/// <summary>
/// The confirm screen as posted. Port and kind are strings because a binder that cannot read one
/// leaves the field null, and null here would mean quietly going back to the guess the person had just
/// changed. <see cref="Harbora.Web.Controllers.LaunchController"/> reads them itself, and says so when it cannot.
/// </summary>
public sealed class LaunchConfirmForm
{
    public string? Name { get; set; }
    public string? Slug { get; set; }
    public string? Kind { get; set; }
    public string? ContainerPort { get; set; }
    public string? Command { get; set; }
    public string? ReleaseCommand { get; set; }
    public string? RootDirectory { get; set; }
    public Guid? EnvironmentId { get; set; }
    public Guid? ServerId { get; set; }
    public string? InstanceSizeKey { get; set; }

    /// <summary>The ticked <c>ManagedServiceType</c>s, by name.</summary>
    public List<string> Services { get; set; } = [];

    /// <summary>The answers and the example values, by variable name.</summary>
    public Dictionary<string, string?> Values { get; set; } = [];

    /// <summary>Set by the button a browser with no script gets: draw the screen again with a fresh figure, confirm nothing.</summary>
    public string? Recheck { get; set; }
}

/// <summary>Everything the confirm screen draws.</summary>
/// <param name="Form">What is in the fields: the proposal's guesses, or what was just posted.</param>
/// <param name="RootDomain">What an address ends in, or null when this panel has none to give.</param>
/// <param name="Address">The address this app would get as things stand, or null when it would get none.</param>
/// <param name="AddressIsReserved">It would get none because the platform keeps that name for itself.</param>
/// <param name="Refusals">Why the last submit did not go through.</param>
/// <param name="SecretsToRetype">A secret was typed and refused along with everything else, and is not drawn back.</param>
/// <param name="ConnectionsToRetype">The same for a connection, which lives under Details and so has to say it there.</param>
public sealed record LaunchConfirmModel(
    LaunchProposal Proposal,
    LaunchConfirmForm Form,
    LaunchQuote Quote,
    string? RootDomain,
    string? Address,
    bool AddressIsReserved,
    IReadOnlyList<SelectListItem> Environments,
    IReadOnlyList<Harbora.Application.Abstractions.ServiceRefusal> Refusals,
    bool SecretsToRetype,
    bool ConnectionsToRetype = false)
{
    public bool IsWorker => Form.Kind == nameof(ServiceKind.Worker);

    public IEnumerable<LaunchEnvField> Asked => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.NeedsAnswer);
    public IEnumerable<LaunchEnvField> Generated => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.GeneratedSecret);
    public IEnumerable<LaunchEnvField> Examples => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.KeepExample);
    public IEnumerable<LaunchEnvField> Connections => Proposal.EnvFields.Where(LaunchConnections.IsConnection);
}

/// <summary>
/// What the screen and the form both have to know about a name a service's attachment answers:
/// <c>DATABASE_URL</c>, or a different spelling of it such as <c>DB_HOST</c>. Whether it needs asking
/// depends on what is ticked at that moment, so it cannot be settled once when the proposal is made.
/// </summary>
public static class LaunchConnections
{
    public static bool IsConnection(LaunchEnvField field) =>
        field.Disposition is EnvVarDisposition.ProvidedByAttach or EnvVarDisposition.AliasOfAttach;

    /// <summary>
    /// The services that would set this name if ticked. More than the one the field names:
    /// <c>DATABASE_URL</c> is set by Postgres, MySQL and MariaDB alike, so ticking MySQL instead still
    /// answers it, whereas an alias is only ever written for its own engine. None at all means nothing
    /// on this screen is known to set it, and it can only be asked.
    /// </summary>
    public static IReadOnlySet<ManagedServiceType> AnsweredBy(LaunchEnvField field)
    {
        var services = new HashSet<ManagedServiceType>();
        if (field.Service is { } named) services.Add(named);
        if (field.Disposition == EnvVarDisposition.ProvidedByAttach)
            foreach (var (kind, names) in AttachEnvNames.All)
                if (names.Contains(field.Key)) services.Add(AttachEnvRoles.TypeFor(kind));
        return services;
    }

    /// <summary>
    /// Whether what is typed for this stays off the screen. Somebody's own <c>DATABASE_URL</c> has their
    /// password in it, whatever the proposal says about a name it expected an attachment to answer. So
    /// a connection is masked unless its name is plainly a part with nothing secret in it: masking
    /// a host costs a glance, and the reverse leaves a password on a page.
    /// </summary>
    public static bool IsMasked(LaunchEnvField field) => field.IsSecret
        || (IsConnection(field) && !PlainParts.Any(part => field.Key.EndsWith(part, StringComparison.OrdinalIgnoreCase)));

    private static readonly string[] PlainParts = ["HOST", "PORT", "USER", "USERNAME", "NAME", "DATABASE", "DB"];
}

/// <summary>The one question a several-app repository gets before anything else can be said about it.</summary>
/// <param name="Message">Why the last answer was refused.</param>
public sealed record LaunchRootModel(Guid LaunchId, IReadOnlyList<string> Candidates, string? Message = null);

/// <summary>The part of <c>/launch/{id}</c> that changes.</summary>
/// <param name="AppKind">What the launch's app turned out to be, for how its success is worded. Null until there is one.</param>
public sealed record LaunchProgressModel(LaunchView View, ServiceKind? AppKind);
