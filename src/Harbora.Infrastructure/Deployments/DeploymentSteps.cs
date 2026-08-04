using Harbora.Domain.Common;

namespace Harbora.Infrastructure.Deployments;

/// <summary>What a step is doing right now.</summary>
public enum StepState
{
    /// <summary>Not started. Drawn plain.</summary>
    Pending,

    /// <summary>Happening now. This is the one that carries the animation.</summary>
    Active,

    /// <summary>Finished successfully.</summary>
    Done,

    /// <summary>The deployment failed at or before this step.</summary>
    Failed,

    /// <summary>Somebody stopped it.</summary>
    Cancelled
}

/// <summary>
/// The staged progress bar, as a rule.
///
/// It exists because the same mapping is needed twice: once by Razor to draw the bar on load, and
/// once by the browser to move it as the deployment runs. Two copies of "which step is Pushing"
/// drift the first time a status is added, and the drift shows as a bar that stops moving — the
/// exact symptom this replaces, where the bar was rendered once and nothing ever touched it again.
/// </summary>
public static class DeploymentSteps
{
    /// <summary>How many steps the bar draws. Labels live in the view; only the count is a rule.</summary>
    public const int Count = 5;

    /// <summary>
    /// Which step a status sits on, or null when the status is not a position on the bar — a
    /// failure is not "step 6", it is a stop wherever it happened.
    /// </summary>
    public static int? IndexOf(DeploymentStatus status) => status switch
    {
        DeploymentStatus.Queued => 0,
        DeploymentStatus.Building => 1,

        // Pushing is part of building as far as somebody watching is concerned: it is the same
        // wait, and a sixth box for it would move the bar backwards on the next status.
        DeploymentStatus.Pushing => 1,

        DeploymentStatus.Deploying => 2,
        DeploymentStatus.HealthChecking => 3,
        DeploymentStatus.Succeeded => 4,
        DeploymentStatus.RolledBack => 4,
        _ => null
    };

    /// <summary>Whether nothing more will happen — the point at which the bar stops animating.</summary>
    public static bool IsTerminal(DeploymentStatus status) =>
        status is DeploymentStatus.Succeeded or DeploymentStatus.Failed
               or DeploymentStatus.Cancelled or DeploymentStatus.RolledBack;

    /// <summary>How one step should be drawn for one status.</summary>
    public static StepState StateOf(int step, DeploymentStatus status)
    {
        if (status == DeploymentStatus.Succeeded) return StepState.Done;

        // A rollback ran to the end and came back. Marking every step done would claim the release
        // shipped; marking none would lose that it got as far as it did.
        if (status == DeploymentStatus.RolledBack)
            return step < Count - 1 ? StepState.Done : StepState.Failed;

        if (status is DeploymentStatus.Failed or DeploymentStatus.Cancelled)
        {
            // Failure stops the bar where it stood. Which step that was is not recorded on the row,
            // so the first is marked and the rest left plain rather than inventing a position.
            var stopped = status == DeploymentStatus.Failed ? StepState.Failed : StepState.Cancelled;
            return step == 0 ? stopped : StepState.Pending;
        }

        var active = IndexOf(status);
        if (active is null) return StepState.Pending;

        if (step < active) return StepState.Done;
        return step == active ? StepState.Active : StepState.Pending;
    }

    /// <summary>
    /// Every status and the step it sits on, for the browser to read.
    ///
    /// Serialised into the page so the script has no second copy of the mapping — it looks a status
    /// up here rather than knowing anything about Pushing or HealthChecking.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Map { get; } =
        Enum.GetValues<DeploymentStatus>()
            .Select(s => (Name: s.ToString(), Index: IndexOf(s)))
            .Where(s => s.Index is not null)
            .ToDictionary(s => s.Name, s => s.Index!.Value);

    /// <summary>The statuses after which nothing moves again.</summary>
    public static IReadOnlyList<string> TerminalNames { get; } =
        Enum.GetValues<DeploymentStatus>().Where(IsTerminal).Select(s => s.ToString()).ToList();
}
