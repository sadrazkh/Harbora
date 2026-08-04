using Harbora.Domain.Common;
using Harbora.Domain.Identity;

namespace Harbora.Infrastructure.Navigation;

/// <summary>
/// Which mode a person actually gets.
///
/// The rule that matters is about people who already use Harbora: they were never asked, and moving
/// them to a reduced interface would look like features were removed. So an existing account keeps
/// Advanced, and Simple is the default only for accounts created after it existed.
///
/// A stored choice always wins over every default. Anything else means a person turns Advanced on,
/// comes back tomorrow, and finds their setting quietly overruled by a platform default they cannot
/// see.
/// </summary>
public static class PanelModeResolver
{
    /// <summary>
    /// The effective mode.
    /// </summary>
    /// <param name="userPreference">What this person chose, or null if they never chose.</param>
    /// <param name="role">Operators get the full panel unless they say otherwise.</param>
    /// <param name="platformDefault">What an administrator set for new accounts, or null.</param>
    public static PanelMode Resolve(PanelMode? userPreference, SystemRole role, PanelMode? platformDefault)
    {
        // An explicit choice is never overridden — not by the platform default, not by role.
        if (userPreference is { } chosen) return chosen;

        // Owners and admins operate the platform; the specialist controls are their everyday tools,
        // and starting them in a reduced interface hides the things they signed in to use.
        if (role is SystemRole.Owner or SystemRole.Admin) return PanelMode.Advanced;

        return platformDefault ?? PanelMode.Simple;
    }

    /// <summary>
    /// The mode to write onto an account that predates this feature.
    ///
    /// Always Advanced. These people have been using the full panel; switching them to Simple on an
    /// upgrade would read as "our features disappeared", and the ones who need Advanced are exactly
    /// the ones least likely to go looking for a toggle to get it back.
    /// </summary>
    public static PanelMode ForExistingAccount() => PanelMode.Advanced;
}
