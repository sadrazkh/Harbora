namespace Harbora.Infrastructure.Networking;

/// <summary>Why an edit was refused, or what it will cost. Null reason means "go ahead".</summary>
public sealed record WiringVerdict(bool Allowed, string? Reason, IReadOnlyList<string> Warnings)
{
    public static WiringVerdict Ok(params string[] warnings) => new(true, null, warnings);
    public static WiringVerdict No(string reason) => new(false, reason, []);
}

/// <summary>
/// The rules for changing wiring from the diagram.
///
/// Both edits look harmless on a canvas and are not. Attaching a database writes a hostname into a
/// service's configuration, and a hostname only resolves inside one network — so attaching across
/// environments produces a service that starts, looks healthy, and cannot reach its database.
/// Moving a service between environments moves it to a different network entirely, which silently
/// severs every connection it had.
///
/// Neither is blocked because it is exotic; they are blocked, or warned about, because the failure
/// arrives later and looks like something else.
/// </summary>
public static class NetworkWiring
{
    /// <summary>
    /// Whether a database may be attached to a service.
    ///
    /// The environment is the network boundary, so it is also the wiring boundary. Refused rather
    /// than warned: there is no configuration in which this works. Both ids are required now (P2,
    /// 2026-08-17 app-environment-management design) — a service or database with no environment is
    /// no longer a state that can exist, so the check that used to name that case has nothing left to
    /// catch.
    /// </summary>
    public static WiringVerdict CanAttach(Guid serviceEnvironmentId, Guid databaseEnvironmentId)
    {
        if (serviceEnvironmentId != databaseEnvironmentId)
            return WiringVerdict.No(
                "They are in different environments, which are different private networks. " +
                "The service would be given a hostname it cannot resolve.");

        return WiringVerdict.Ok();
    }

    /// <summary>
    /// What moving a service to another environment will break.
    ///
    /// Allowed, because it is a legitimate thing to want, but never silent: every database the
    /// service is wired to stays behind on the old network, and the service's own internal name
    /// stops resolving for whatever used to call it.
    /// </summary>
    /// <param name="attachedNames">Databases this service currently holds a connection to.</param>
    /// <param name="dependentNames">Services that reach this one by its internal name.</param>
    public static WiringVerdict CanMove(
        Guid currentEnvironmentId,
        Guid targetEnvironmentId,
        IReadOnlyList<string> attachedNames,
        IReadOnlyList<string> dependentNames)
    {
        if (currentEnvironmentId == targetEnvironmentId)
            return WiringVerdict.No("It is already in that environment.");

        var warnings = new List<string>();

        if (attachedNames.Count > 0)
            warnings.Add(
                $"It will lose its connection to {Join(attachedNames)}, which stay in the old environment. " +
                "Attach an equivalent there, or the service will start and fail to reach its data.");

        if (dependentNames.Count > 0)
            warnings.Add(
                $"{Join(dependentNames)} reach this service by its internal name and will stop being able to.");

        // Said even when nothing else is: the move is a configuration change, and configuration
        // changes do not reach a running container until it is rebuilt.
        warnings.Add("The service must be redeployed before the move takes effect.");

        return WiringVerdict.Ok([.. warnings]);
    }

    /// <summary>
    /// Names in a sentence, capped. A warning listing forty services is one nobody finishes reading,
    /// and the count is the part that decides whether to go ahead.
    /// </summary>
    private static string Join(IReadOnlyList<string> names)
    {
        const int shown = 3;
        if (names.Count <= shown) return string.Join(", ", names);

        return $"{string.Join(", ", names.Take(shown))} and {names.Count - shown} more";
    }
}
