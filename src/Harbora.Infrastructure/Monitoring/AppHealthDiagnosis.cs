using Harbora.Application.Abstractions;
using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Monitoring;

/// <summary>What the container runtime says about an app right now.</summary>
public enum ObservedAppState
{
    /// <summary>No container carries this app's label — it has never run, or a deploy is mid-flight.</summary>
    Missing = 0,
    Running = 1,
    /// <summary>A container is being restarted after dying: a crash loop.</summary>
    CrashLooping = 2,
    /// <summary>Stopped and not coming back on its own.</summary>
    Exited = 3
}

/// <summary>
/// Reconciles the status shown in the panel with what the containers are actually doing.
///
/// Two lies were possible before, in opposite directions. Crash detection watched only for "exited",
/// but app containers run under <c>unless-stopped</c>, so a container that dies on startup is revived
/// within moments and reports "restarting" — a crash-looping app kept its green Running badge
/// indefinitely (confirmed on the live server). And nothing ever cleared <see cref="AppStatus.Crashed"/>,
/// so an app that recovered on its own stayed marked as crashed until someone deployed again.
/// </summary>
public static class AppHealthDiagnosis
{
    /// <summary>
    /// Collapses an app's containers into one state.
    ///
    /// Crash-looping outranks running deliberately: a restarting container flaps through "running",
    /// and during a cutover the old container is healthy while the new one may already be failing.
    /// Treating any restart as a crash means the panel errs toward telling the user something is
    /// wrong, which is the side to err on.
    /// </summary>
    public static ObservedAppState Observe(IEnumerable<ContainerInfo> appContainers)
    {
        var seen = ObservedAppState.Missing;
        foreach (var c in appContainers)
        {
            if (Is(c, "restarting")) return ObservedAppState.CrashLooping;
            if (Is(c, "running")) seen = ObservedAppState.Running;
            else if (seen != ObservedAppState.Running && Is(c, "exited")) seen = ObservedAppState.Exited;
        }
        return seen;
    }

    /// <summary>
    /// The status the app should now carry, or <c>null</c> to leave it alone.
    ///
    /// Leaving it alone is the common answer, and the important one: a deploy in flight owns the
    /// app's status, and a user who stopped an app has said what they want. Neither is the monitor's
    /// to overrule.
    /// </summary>
    public static AppStatus? NextStatus(AppStatus current, ObservedAppState observed)
    {
        // Deliberate states. "Stopped" means someone asked for it; "Deploying" belongs to the
        // pipeline, whose containers are legitimately half-up while it works.
        if (current is AppStatus.Stopped or AppStatus.Deploying) return null;

        return observed switch
        {
            ObservedAppState.CrashLooping or ObservedAppState.Exited
                => current == AppStatus.Crashed ? null : AppStatus.Crashed,

            // Recovery: Docker restarted it successfully, or the operator fixed whatever it was.
            ObservedAppState.Running
                => current == AppStatus.Crashed ? AppStatus.Running : null,

            // No containers at all says nothing about health — an app can be between deployments.
            _ => null
        };
    }

    private static bool Is(ContainerInfo c, string state) =>
        c.State.Equals(state, StringComparison.OrdinalIgnoreCase);
}
