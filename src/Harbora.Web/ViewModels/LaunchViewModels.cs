using Harbora.Domain.Common;
using Harbora.Infrastructure.Launch;
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
}

/// <summary>What <c>/launch</c> draws. The token is not here: what a person typed into it is never drawn back.</summary>
/// <param name="ReadingFailed">The last attempt could not be read, and <paramref name="Message"/> says why.</param>
/// <param name="AskedForDetails">Open the private-repository fold: there is something in it, or something missing from it.</param>
public sealed record LaunchQuestionModel(
    string? GitUrl = null,
    string? GitRef = null,
    string? Message = null,
    bool ReadingFailed = false,
    bool AskedForDetails = false);

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
/// <param name="Refusals">Why the last submit did not go through.</param>
/// <param name="SecretsToRetype">A secret was typed and refused along with everything else, and is not drawn back.</param>
public sealed record LaunchConfirmModel(
    LaunchProposal Proposal,
    LaunchConfirmForm Form,
    LaunchQuote Quote,
    string? RootDomain,
    string? Address,
    IReadOnlyList<SelectListItem> Environments,
    IReadOnlyList<Harbora.Application.Abstractions.ServiceRefusal> Refusals,
    bool SecretsToRetype)
{
    public bool IsWorker => Form.Kind == nameof(ServiceKind.Worker);

    public IEnumerable<LaunchEnvField> Asked => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.NeedsAnswer);
    public IEnumerable<LaunchEnvField> Generated => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.GeneratedSecret);
    public IEnumerable<LaunchEnvField> Examples => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.KeepExample);
    public IEnumerable<LaunchEnvField> Aliases => Proposal.EnvFields.Where(f => f.Disposition == EnvVarDisposition.AliasOfAttach);
}

/// <summary>The part of <c>/launch/{id}</c> that changes.</summary>
/// <param name="AppKind">What the launch's app turned out to be, for how its success is worded. Null until there is one.</param>
public sealed record LaunchProgressModel(LaunchView View, ServiceKind? AppKind);
