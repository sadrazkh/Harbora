using Harbora.Data;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Harbora.Infrastructure.Security;

/// <summary>One workspace that holds an account to single sign-on, named so a refusal can say it.</summary>
public sealed record SsoRequiringWorkspace(Guid WorkspaceId, string Name, string Slug);

/// <summary>
/// A member of a workspace whose account has no external identity connected to it yet — the people
/// the panel names before the setting is saved, not after.
/// </summary>
public sealed record SsoUnlinkedMember(Guid UserId, string Email, string DisplayName);

/// <summary>
/// Whether a workspace's "single sign-on only" setting refuses a particular password sign-in, and
/// who in a workspace it would refuse if it were turned on now.
///
/// <para><b>Why any workspace refuses, not only the one being entered.</b> A sign-in is not scoped
/// to a workspace: the password form asks for an address, and the cookie it mints carries whichever
/// membership came first. <c>POST /workspaces/switch</c> then moves that same cookie into any other
/// workspace the person belongs to without re-authenticating anybody. So a password session created
/// for workspace A is already a password session inside workspace B; refusing only at the moment
/// somebody switched would leave the policy true of a screen and false of the account. The rule this
/// class implements is therefore: if <i>any</i> workspace this account belongs to requires single
/// sign-on, the password form refuses, and it names that workspace.</para>
///
/// <para><b>Who is exempt, and why the exemption is not optional.</b> The workspace's own owner, and
/// the installation owner (<see cref="SystemRole.Owner"/>, the account first-run setup creates).
/// A provider that stops answering — a rotated secret, an expired certificate, an issuer that moved
/// house — would otherwise leave nobody at all able to reach the panel and turn the setting off, and
/// this codebase has no administrator-side "set somebody else's password" to undo that with. The
/// installation owner is exempt for the second half of the same reason: they are the only account
/// that can reach the platform settings page where providers are configured, so one customer
/// workspace must not be able to lock the installation out of its own repair tool.</para>
///
/// <para><b>Scope.</b> Sign-in happens with no workspace scope at all, so every query here uses
/// <c>IgnoreQueryFilters()</c> together with an explicit key. Without both, the read returns nothing
/// and the sign-in form quietly lets everybody through — the exact shape of failure this codebase
/// names as its defining defect.</para>
/// </summary>
public sealed class SingleSignOnRequirementService(HarboraDbContext db)
{
    /// <summary>
    /// The workspaces that refuse <paramref name="user"/> a password sign-in. Empty is the normal
    /// answer on every installation where nobody has turned the setting on.
    /// </summary>
    public async Task<IReadOnlyList<SsoRequiringWorkspace>> WorkspacesHoldingAsync(
        User user, CancellationToken ct)
    {
        // The installation owner is never held. See the class remarks: they are the repair path.
        if (user.Role == SystemRole.Owner) return [];

        return await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.UserId == user.Id
                        && m.Workspace!.RequiresSingleSignOn
                        && m.Workspace.OwnerUserId != user.Id
                        // An archived or deleted workspace is not a place anybody works. Holding an
                        // account to the policy of one would be a lockout with no screen to lift it
                        // from, because the workspace itself no longer appears in the panel.
                        && m.Workspace.ArchivedAt == null
                        && m.Workspace.DeletedAt == null)
            .OrderBy(m => m.Workspace!.Name)
            .Select(m => new SsoRequiringWorkspace(m.WorkspaceId, m.Workspace!.Name, m.Workspace.Slug))
            .ToListAsync(ct);
    }

    /// <summary>
    /// Members of <paramref name="workspaceId"/> whose accounts have no external identity connected,
    /// in the order the panel lists them.
    ///
    /// <para>
    /// The exempt people are left out on purpose: this list answers "who would be refused", and the
    /// owner and the installation owner would not be. Including them would make the panel warn about
    /// a consequence that cannot happen and teach an administrator to dismiss the warning.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<SsoUnlinkedMember>> UnlinkedMembersAsync(
        Guid workspaceId, CancellationToken ct)
    {
        var linkedUserIds = await db.ExternalLogins.IgnoreQueryFilters().AsNoTracking()
            .Select(l => l.UserId).Distinct().ToListAsync(ct);

        return await db.WorkspaceMembers.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId
                        && m.Workspace!.OwnerUserId != m.UserId
                        && m.User!.Role != SystemRole.Owner
                        && !linkedUserIds.Contains(m.UserId))
            .OrderBy(m => m.User!.Email)
            .Select(m => new SsoUnlinkedMember(m.UserId, m.User!.Email, m.User.DisplayName))
            .ToListAsync(ct);
    }
}
