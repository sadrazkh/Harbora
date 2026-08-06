namespace Harbora.Infrastructure.Terminals;

/// <summary>Why a terminal was not opened, or <see cref="None"/> when it was.</summary>
public enum TerminalRefusal
{
    None,

    /// <summary>The operator has not turned the feature on. The route does not exist.</summary>
    FeatureOff,

    /// <summary>The caller may not manage this application.</summary>
    NotAllowed,

    /// <summary>The application runs on a node, and a node does not offer a terminal.</summary>
    NotLocal,

    /// <summary>There is no running container to attach to.</summary>
    NotRunning
}

/// <summary>
/// Whether somebody may open a shell inside a running container.
///
/// This is the widest door the panel has. A shell in an application's container is that
/// application's filesystem, its environment (which holds its database password), and its network
/// — every guard the rest of the platform maintains is downstream of this one decision. So the
/// decision lives here as a rule with no dependencies, and the order it asks its questions in is
/// part of the rule rather than an accident of how the controller was written.
///
/// The order matters twice over:
/// <list type="number">
///   <item>The feature flag comes first, so a platform that has not enabled terminals behaves as
///   though the page does not exist, rather than as though it exists and refuses.</item>
///   <item>Authorisation comes before state. "There is no container running" is information about
///   somebody's application; answering it before checking who is asking turns a 404 into a probe
///   for which applications exist and whether they are up.</item>
/// </list>
/// </summary>
public static class TerminalAccess
{
    /// <summary>
    /// A session with nobody typing is closed. Fifteen minutes because the risk is a shell left
    /// open on a laptop in a coffee shop, not a slow command — a command that is running counts as
    /// activity, because its output is traffic.
    /// </summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(15);

    /// <summary>
    /// And a ceiling regardless of activity. A shell that has been open for four hours is not
    /// somebody working; it is a tab nobody closed, and it outlives the reason it was opened.
    /// </summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(4);

    public static TerminalRefusal Decide(
        bool featureEnabled, bool mayManage, bool isLocalServer, bool hasRunningContainer)
    {
        if (!featureEnabled) return TerminalRefusal.FeatureOff;
        if (!mayManage) return TerminalRefusal.NotAllowed;
        if (!isLocalServer) return TerminalRefusal.NotLocal;
        if (!hasRunningContainer) return TerminalRefusal.NotRunning;
        return TerminalRefusal.None;
    }

    /// <summary>
    /// Whether a session that started at <paramref name="startedAt"/> and last saw traffic at
    /// <paramref name="lastActivity"/> should be closed now.
    /// </summary>
    public static bool ShouldClose(DateTimeOffset startedAt, DateTimeOffset lastActivity, DateTimeOffset now) =>
        now - lastActivity >= IdleTimeout || now - startedAt >= MaxDuration;

    /// <summary>
    /// What is run inside the container.
    ///
    /// A constant, and never anything the caller supplied: the point of the page is a shell, and a
    /// command that came in over the wire would make this an arbitrary-exec endpoint with a
    /// terminal drawn on it. bash when the image has it, sh when it does not — <c>exec</c> so the
    /// shell replaces this process and its exit ends the session.
    /// </summary>
    public static IReadOnlyList<string> Command { get; } =
        ["/bin/sh", "-c", "exec /bin/bash 2>/dev/null || exec /bin/sh"];

    /// <summary>
    /// A terminal size a container will accept. xterm reports what the browser window can show, and
    /// a window being resized while the page loads reports zero — which docker rejects, taking the
    /// session with it.
    /// </summary>
    public static (uint Columns, uint Rows) Size(int columns, int rows) =>
        ((uint)Math.Clamp(columns, 20, 500), (uint)Math.Clamp(rows, 5, 200));
}
