using Harbora.Domain.Common;

namespace Harbora.Domain.Authorization;

/// <summary>Everything a decision about one user needs to know.</summary>
/// <param name="ActiveOwnerCount">
/// How many active Owners exist right now, including the target. The platform becomes
/// unadministrable at zero, so this is what the last-owner rules are measured against.
/// </param>
public sealed record UserAdminContext(
    Guid ActorId,
    SystemRole ActorRole,
    Guid TargetId,
    SystemRole TargetRole,
    bool TargetIsActive,
    int ActiveOwnerCount)
{
    public bool IsSelf => ActorId == TargetId;
}

/// <summary>
/// Who may do what to whom.
///
/// Every rule here prevents a state that cannot be undone from inside the panel. The codebase
/// already carries a break-glass <c>harbora make-owner</c> command, documented as being "for when
/// the only owner was deleted or demoted" — that command is the scar left by not having this class.
///
/// Refusals are returned as sentences rather than booleans because each one has to be shown to
/// somebody who is about to be told no, and "forbidden" does not tell them which rule they hit or
/// what to do instead.
/// </summary>
public static class UserAdministration
{
    /// <summary>Roles permitted to administer other users at all.</summary>
    private static bool MayAdminister(SystemRole role) =>
        role is SystemRole.Owner or SystemRole.Admin;

    /// <summary>Null when the role change is allowed; otherwise why not.</summary>
    public static string? RefuseRoleChange(UserAdminContext context, SystemRole newRole)
    {
        if (Gate(context) is { } refusal) return refusal;

        // Both directions of self-service are dangerous: downwards an owner locks themselves out,
        // upwards anybody who can reach this form appoints themselves.
        if (context.IsSelf)
            return "You cannot change your own role. Ask another owner to do it.";

        if (newRole == SystemRole.Owner && context.ActorRole != SystemRole.Owner)
            return "Only an owner can make somebody else an owner.";

        // Demoting the last owner leaves nobody who can undo it.
        if (context.TargetRole == SystemRole.Owner && newRole != SystemRole.Owner && LastOwner(context))
            return "This is the last owner. Make somebody else an owner first.";

        return null;
    }

    /// <summary>Null when the user may be suspended; otherwise why not.</summary>
    public static string? RefuseDeactivation(UserAdminContext context)
    {
        if (Gate(context) is { } refusal) return refusal;

        if (context.IsSelf)
            return "You cannot suspend your own account.";

        if (context.TargetRole == SystemRole.Owner && LastOwner(context))
            return "This is the last owner. Make somebody else an owner first.";

        return null;
    }

    /// <summary>
    /// Null when the user may be restored. Deliberately not symmetric with deactivation: bringing
    /// an account back cannot lock anybody out, so the owner-count rule must not stand in its way —
    /// least of all when the count is zero and restoring an owner is the only way out.
    /// </summary>
    public static string? RefuseReactivation(UserAdminContext context) => Gate(context);

    /// <summary>
    /// Null when this actor may replace the target's password.
    ///
    /// Setting somebody's password is taking over their account, so it needs the same standing as
    /// changing their role — an admin must not be able to do it to an owner. Doing it to yourself
    /// is fine and is not treated as a special case: <see cref="Gate"/> already lets an owner act
    /// on an owner, which is what self is.
    /// </summary>
    public static string? RefusePasswordReset(UserAdminContext context) => Gate(context);

    /// <summary>Null when this actor may create a user with that role; otherwise why not.</summary>
    public static string? RefuseCreation(SystemRole actorRole, SystemRole newRole)
    {
        if (!MayAdminister(actorRole))
            return "You do not have permission to manage users.";

        if (newRole == SystemRole.Owner && actorRole != SystemRole.Owner)
            return "Only an owner can create another owner.";

        return null;
    }

    /// <summary>The checks every operation shares.</summary>
    private static string? Gate(UserAdminContext context)
    {
        if (!MayAdminister(context.ActorRole))
            return "You do not have permission to manage users.";

        // An admin who can demote or suspend an owner outranks one, whatever the matrix says.
        if (context.TargetRole == SystemRole.Owner && context.ActorRole != SystemRole.Owner)
            return "Only an owner can manage another owner.";

        return null;
    }

    /// <summary>
    /// Whether removing this owner would leave none. Counted from active owners including the
    /// target, so one active owner means this one.
    /// </summary>
    private static bool LastOwner(UserAdminContext context) =>
        context.TargetIsActive && context.ActiveOwnerCount <= 1;
}
