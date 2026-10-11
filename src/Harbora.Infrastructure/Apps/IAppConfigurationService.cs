using System.Text;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Apps;

/// <summary>Who is changing an app: the workspace the change happens in, the person, and — for the audit
/// entry — the address the request came from, when there is a request.</summary>
public sealed record AppActor(Guid WorkspaceId, Guid UserId, string? ClientIp = null);

/// <summary>
/// One of an app's own items — an environment variable, a domain, a volume — named either by its row id
/// (what a form posts) or by its natural name: the variable's key, the domain's host, the volume's mount
/// path (what the agent, which addresses things by name, has). <see cref="Id"/> wins when both are set.
/// </summary>
public sealed record AppItemRef(Guid? Id, string? Name)
{
    public static AppItemRef ById(Guid id) => new(id, null);
    public static AppItemRef ByName(string name) => new(null, name);
}

/// <summary>A variable to set. An existing variable with the same key is overwritten, flags included.</summary>
public sealed record AppEnvVarInput(string Key, string? Value, bool IsSecret = false, bool AvailableAtBuild = false)
{
    /// <summary>Never prints <see cref="Value"/>: a logged input must not be how a secret reaches a log.</summary>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append($"Key = {Key}, IsSecret = {IsSecret}, AvailableAtBuild = {AvailableAtBuild}");
        return true;
    }
}

/// <summary>What setting one variable did — the key and flags, never the value.</summary>
public sealed record AppEnvVarChange(string Key, bool Created, bool IsSecret, bool AvailableAtBuild);

/// <param name="IsPrimary">True when this became the app's primary address (its first domain).</param>
public sealed record AppDomainAdded(Guid DomainId, string Host, bool SslEnabled, bool IsPrimary);

/// <param name="NewPrimaryHost">The surviving domain that became primary because the removed one was,
/// or null when nothing changed hands.</param>
public sealed record AppDomainRemoved(string Host, string? NewPrimaryHost);

public sealed record AppVolumeAdded(Guid VolumeId, string MountPath, string VolumeName);

/// <param name="DataDeletionRequested">Whether the caller asked for the data to be deleted too.</param>
/// <param name="DataDeleted">Whether it actually was — decided by the Docker call, not assumed from the
/// request. Requested and not deleted means the data is still on the server in
/// <paramref name="VolumeName"/>.</param>
public sealed record AppVolumeRemoved(string MountPath, string VolumeName, bool DataDeletionRequested, bool DataDeleted);

/// <summary>
/// Changing an app's environment variables, domains and volumes — the one path the app's own pages and,
/// next, the AI agent's tools both go through. Each mutation has a validate method beside it that answers
/// "would this be refused, and why" and changes nothing.
///
/// <para>
/// Every method checks, itself, that the caller may touch this app with <c>Capabilities.AppsEnv</c>
/// (<c>ProjectAccessService.CanTouchAppAsync</c> — the capability the app pages' own policy uses for all
/// six), so a caller that is not a controller cannot skip it. Reads go through the request's workspace
/// filter; nothing here reads another tenant's rows except the host-uniqueness check, which has to.
/// </para>
/// </summary>
public interface IAppConfigurationService
{
    Task<IReadOnlyList<ServiceRefusal>> ValidateSetEnvAsync(
        AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct);

    /// <summary>Creates or overwrites each variable; a secret's value is stored encrypted. Audited as one
    /// <c>app.env_set</c> per key, naming the key and never the value.</summary>
    Task<ServiceOutcome<IReadOnlyList<AppEnvVarChange>>> SetEnvAsync(
        AppActor actor, Guid appId, IReadOnlyList<AppEnvVarInput> variables, CancellationToken ct);

    Task<IReadOnlyList<ServiceRefusal>> ValidateDeleteEnvAsync(
        AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct);

    /// <summary>Deletes the variable and returns its key. Audited as <c>app.env_deleted</c>.</summary>
    Task<ServiceOutcome<string>> DeleteEnvAsync(
        AppActor actor, Guid appId, AppItemRef variable, CancellationToken ct);

    Task<IReadOnlyList<ServiceRefusal>> ValidateAddDomainAsync(
        AppActor actor, Guid appId, string? host, CancellationToken ct);

    /// <summary>Attaches a custom domain (lower-cased, trimmed). Refuses a host in use anywhere on the
    /// platform, one of the platform's own hosts, and one past the plan's domain ceiling. Audited as
    /// <c>app.domain_added</c>.</summary>
    Task<ServiceOutcome<AppDomainAdded>> AddDomainAsync(
        AppActor actor, Guid appId, string? host, bool ssl, CancellationToken ct);

    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveDomainAsync(
        AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct);

    /// <summary>Detaches the domain and its routes, and promotes a survivor to primary when the primary
    /// went. Audited as <c>app.domain_removed</c>.</summary>
    Task<ServiceOutcome<AppDomainRemoved>> RemoveDomainAsync(
        AppActor actor, Guid appId, AppItemRef domain, CancellationToken ct);

    Task<IReadOnlyList<ServiceRefusal>> ValidateAddVolumeAsync(
        AppActor actor, Guid appId, string? mountPath, CancellationToken ct);

    /// <summary>Adds a volume at the normalised mount path. It is a row until the next deployment mounts
    /// it. Audited as <c>app.volume_added</c>.</summary>
    Task<ServiceOutcome<AppVolumeAdded>> AddVolumeAsync(
        AppActor actor, Guid appId, string? mountPath, CancellationToken ct);

    /// <summary>Also refuses <paramref name="deleteData"/> on a Protected volume (HARBORA-0033).</summary>
    Task<IReadOnlyList<ServiceRefusal>> ValidateRemoveVolumeAsync(
        AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct);

    /// <summary>Stops mounting the volume, and deletes its data only when asked and not Protected.
    /// Audited as <c>app.volume_deleted</c> when the data really went, <c>app.volume_detached</c>
    /// otherwise.</summary>
    Task<ServiceOutcome<AppVolumeRemoved>> RemoveVolumeAsync(
        AppActor actor, Guid appId, AppItemRef volume, bool deleteData, CancellationToken ct);
}
