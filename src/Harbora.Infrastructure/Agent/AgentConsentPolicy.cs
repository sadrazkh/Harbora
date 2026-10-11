namespace Harbora.Infrastructure.Agent;

/// <summary>
/// The whole confirmation policy, as pure rules — the owner's consent rule from the 2026-10-11 design:
/// reads run; a safe write asks the first time and may be remembered; a dangerous write always asks; a
/// delete always asks and wants the resource's name typed.
///
/// <para>
/// Decided by the tool's <see cref="AgentToolKind"/>, which its author chose — never by the model and
/// never by what a request claims. In particular <see cref="IsRememberable"/> is consulted when an
/// approval is saved, so a request asking to remember a dangerous tool simply has that part ignored.
/// </para>
/// </summary>
public static class AgentConsentPolicy
{
    /// <summary>Whether a call of this kind runs as soon as the model asks, with no plan at all.</summary>
    public static bool RunsImmediately(AgentToolKind kind) => kind == AgentToolKind.Read;

    /// <summary>Whether "don't ask again" may be offered — and recorded — for this kind.</summary>
    public static bool IsRememberable(AgentToolKind kind) => kind == AgentToolKind.SafeWrite;

    /// <summary>
    /// Whether a proposed write step skips the card. Only a <see cref="AgentToolKind.SafeWrite"/> the
    /// person has remembered; everything else is shown, every time.
    /// </summary>
    public static bool IsAutoApproved(AgentToolKind kind, bool remembered) =>
        kind == AgentToolKind.SafeWrite && remembered;

    /// <summary>Whether the step can only run once the person has typed its confirmation phrase.</summary>
    public static bool NeedsTypedConfirmation(AgentToolKind kind) => kind == AgentToolKind.Destructive;

    /// <summary>
    /// Whether what the person typed is the phrase: trimmed, then compared exactly — case-sensitive,
    /// ordinal. "Shop-DB" is not "shop-db"; typing the name is the point, and a forgiving comparison
    /// would turn it back into a click. An empty or missing expected phrase never matches: a
    /// destructive step without one is a tool bug, and it fails closed.
    /// </summary>
    public static bool ConfirmationMatches(string? expected, string? typed) =>
        !string.IsNullOrWhiteSpace(expected)
        && typed is not null
        && string.Equals(expected.Trim(), typed.Trim(), StringComparison.Ordinal);

    /// <summary>The kind as the plan card's badge names it: "safe", "dangerous" or "destructive"
    /// (and "read" for completeness — a read is never in a plan).</summary>
    public static string KindLabel(AgentToolKind kind) => kind switch
    {
        AgentToolKind.Read => "read",
        AgentToolKind.SafeWrite => "safe",
        AgentToolKind.DangerousWrite => "dangerous",
        AgentToolKind.Destructive => "destructive",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };
}
