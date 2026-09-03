using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Harbora.Application.Abstractions;
using Harbora.Data;
using Harbora.Domain.Billing;
using Harbora.Domain.Common;
using Harbora.Domain.Identity;
using Harbora.Infrastructure.Billing;
using Harbora.Infrastructure.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Harbora.Infrastructure.Security;

public sealed record IssuedWorkspaceInvitation(WorkspaceInvitation Invitation, string Token);

/// <summary>Creates personal/team workspaces and owns the single-use invitation lifecycle.</summary>
public sealed partial class WorkspaceAccountService(
    HarboraDbContext db,
    ProjectService projects,
    ISystemClock clock,
    IOptions<BillingOptions> billing,
    IQuotaService? quota = null,
    IFunctionEventBus? functionEvents = null,
    SignupTrialCreditService? signupCredit = null)
{
    /// <summary>
    /// Tells subscribing functions, when there is a bus to tell. Optional for the same reason the
    /// quota service is: this type is constructed directly by tests and by first-run setup, and
    /// neither should have to build an event bus to create a workspace.
    /// </summary>
    private Task PublishAsync(string key, Guid workspaceId, string? subject,
        (string Key, string? Value)[] data, CancellationToken ct) =>
        functionEvents?.PublishAsync(
            Domain.Functions.FunctionEvent.Create(key, workspaceId, subject, data), ct)
        ?? Task.CompletedTask;

    public async Task<Workspace> EnsurePersonalWorkspaceAsync(User user, CancellationToken ct)
    {
        var existing = await db.Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.OwnerUserId == user.Id && w.IsPersonal, ct);
        if (existing is not null) return existing;

        // The setup owner's original provider workspace is already their home. Claim it instead of
        // surprising an upgraded single-user installation with a second, empty workspace.
        if (user.Role == SystemRole.Owner)
        {
            var provider = await db.WorkspaceMembers.IgnoreQueryFilters()
                .Where(m => m.UserId == user.Id && m.Workspace!.IsDefault)
                .Select(m => m.Workspace!)
                .FirstOrDefaultAsync(ct);
            if (provider is not null)
            {
                provider.OwnerUserId = user.Id;
                provider.IsPersonal = true;
                await db.SaveChangesAsync(ct);
                return provider;
            }
        }

        var label = string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email.Split('@')[0] : user.DisplayName.Trim();
        return await CreateAsync(user.Id, $"{label}'s workspace", label, personal: true, ct);
    }

    public Task<Workspace> CreateTeamWorkspaceAsync(Guid ownerUserId, string name, CancellationToken ct) =>
        CreateAsync(ownerUserId, name, name, personal: false, ct);

    private async Task<Workspace> CreateAsync(
        Guid ownerUserId, string name, string slugSource, bool personal, CancellationToken ct)
    {
        var owner = await db.Users.IgnoreQueryFilters().FirstOrDefaultAsync(u => u.Id == ownerUserId, ct)
            ?? throw new InvalidOperationException("The workspace owner account no longer exists.");

        var slug = await UniqueSlugAsync(slugSource, ct);
        // Public accounts must not silently inherit the provider's unlimited plan. Prefer the
        // least expensive enabled customer plan; null keeps installations with custom/no plans
        // working and follows the same fallback as the existing tenant console.
        var planId = await db.Plans.IgnoreQueryFilters()
            .Where(p => p.IsEnabled && !p.IsDefault)
            .OrderBy(p => p.MonthlyPrice).ThenBy(p => p.Name)
            .Select(p => (Guid?)p.Id).FirstOrDefaultAsync(ct);
        var workspace = new Workspace
        {
            Name = string.IsNullOrWhiteSpace(name) ? slug : name.Trim(),
            Slug = slug,
            OwnerUserId = owner.Id,
            IsPersonal = personal,
            IsDefault = false,
            PlanId = planId,
            CreatedAt = clock.UtcNow
        };
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember
        {
            Workspace = workspace,
            User = owner,
            Role = WorkspaceRole.Admin,
            CreatedAt = clock.UtcNow
        });
        db.Wallets.Add(new Wallet
        {
            WorkspaceId = workspace.Id,
            Currency = billing.Value.CurrencyOrDefault,
            CreatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync(ct);

        // After the wallet and the owner's own membership are committed — RedeemAsync requires both:
        // a wallet to read the account's currency off, and a WorkspaceMember row to prove the person
        // it is crediting on behalf of actually belongs here. Optional the same way quota and the
        // event bus are: this type is also built directly by tests and by first-run setup, and
        // neither should have to wire a voucher service just to create a workspace. See
        // SignupTrialCreditService's own class comment for why "the owner" is the identity that
        // makes a second attempt for the same person collect nothing, whichever workspace they are
        // creating and however many times they retry.
        if (signupCredit is not null)
            await signupCredit.GrantAsync(workspace.Id, owner.Id, ct);

        await projects.EnsureDefaultEnvironmentAsync(workspace.Id, ct);
        await PublishAsync(Domain.Functions.FunctionEvents.WorkspaceCreated, workspace.Id, workspace.Name,
            [("workspace", workspace.Slug), ("personal", personal ? "true" : "false")], ct);
        return workspace;
    }

    public async Task<IssuedWorkspaceInvitation> InviteAsync(
        Guid workspaceId, Guid invitedBy, string email, WorkspaceRole role, CancellationToken ct)
    {
        email = NormalizeEmail(email);
        if (role is not (WorkspaceRole.Admin or WorkspaceRole.Member or WorkspaceRole.Viewer or WorkspaceRole.Operator))
            throw new ArgumentException("Choose a valid workspace role.", nameof(role));

        await using var reservation = quota is null
            ? NoopQuotaReservation.Instance
            : await quota.AcquireCreationLockAsync(workspaceId, ct);

        if (await db.WorkspaceMembers.IgnoreQueryFilters()
            .AnyAsync(m => m.WorkspaceId == workspaceId && m.User!.Email == email, ct))
            throw new InvalidOperationException("That account is already a member of this workspace.");

        var now = clock.UtcNow;
        var replacesActiveReservation = await db.WorkspaceInvitations.IgnoreQueryFilters()
            .AnyAsync(i => i.WorkspaceId == workspaceId && i.Email == email
                && i.AcceptedAt == null && !i.IsRevoked && i.ExpiresAt > now, ct);
        if (quota is not null)
        {
            var check = await quota.CanAddGovernedResourcesAsync(workspaceId,
                new GovernanceQuotaDelta(Members: replacesActiveReservation ? 0 : 1), ct);
            if (!check.Allowed) throw new InvalidOperationException(check.Reason);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var old = await db.WorkspaceInvitations.IgnoreQueryFilters()
            .Where(i => i.WorkspaceId == workspaceId && i.Email == email && i.AcceptedAt == null && !i.IsRevoked)
            .ToListAsync(ct);
        foreach (var invitation in old) invitation.IsRevoked = true;

        var row = new WorkspaceInvitation
        {
            WorkspaceId = workspaceId,
            Email = email,
            Role = role,
            TokenHash = Hash(token),
            TokenHint = token[..6],
            CreatedByUserId = invitedBy,
            CreatedAt = now,
            ExpiresAt = now.AddDays(7)
        };
        db.WorkspaceInvitations.Add(row);
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
        // The address is what an onboarding function needs; the token deliberately never leaves here.
        await PublishAsync(Domain.Functions.FunctionEvents.MemberInvited, workspaceId, email,
            [("email", email), ("role", role.ToString())], ct);
        return new(row, token);
    }

    public async Task<WorkspaceInvitation?> FindInvitationAsync(string? token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var hash = Hash(token.Trim());
        return await db.WorkspaceInvitations.IgnoreQueryFilters().Include(i => i.Workspace)
            .FirstOrDefaultAsync(i => i.TokenHash == hash, ct);
    }

    public async Task<Workspace> AcceptInvitationAsync(string token, User user, CancellationToken ct)
    {
        var invitation = await FindInvitationAsync(token, ct)
            ?? throw new InvalidOperationException("This invitation is not valid.");

        await using var reservation = quota is null
            ? NoopQuotaReservation.Instance
            : await quota.AcquireCreationLockAsync(invitation.WorkspaceId, ct);

        // The token had to be read once to discover its workspace and therefore its lock key. Read
        // it again after taking that lock so two acceptance requests cannot both act on the stale
        // pre-lock state.
        await db.Entry(invitation).ReloadAsync(ct);
        if (invitation.IsRevoked) throw new InvalidOperationException("This invitation was revoked.");
        if (invitation.AcceptedAt is not null) throw new InvalidOperationException("This invitation was already used.");
        if (invitation.ExpiresAt <= clock.UtcNow) throw new InvalidOperationException("This invitation has expired.");
        if (!string.Equals(invitation.Email, NormalizeEmail(user.Email), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This invitation belongs to a different email address.");

        if (quota is not null)
        {
            // The pending invitation already reserves this seat. A zero delta still refuses if an
            // administrator lowered the plan beneath its current use after the link was issued.
            var check = await quota.CanAddGovernedResourcesAsync(invitation.WorkspaceId,
                new GovernanceQuotaDelta(), ct);
            if (!check.Allowed) throw new InvalidOperationException(check.Reason);
        }

        if (!await db.WorkspaceMembers.IgnoreQueryFilters()
                .AnyAsync(m => m.WorkspaceId == invitation.WorkspaceId && m.UserId == user.Id, ct))
            db.WorkspaceMembers.Add(new WorkspaceMember
            {
                WorkspaceId = invitation.WorkspaceId,
                UserId = user.Id,
                Role = invitation.Role,
                CreatedAt = clock.UtcNow
            });
        invitation.AcceptedAt = clock.UtcNow;
        invitation.AcceptedByUserId = user.Id;
        await db.SaveChangesAsync(ct);
        await reservation.CommitAsync(ct);
        await PublishAsync(Domain.Functions.FunctionEvents.MemberJoined, invitation.WorkspaceId, user.Email,
            [("email", user.Email), ("role", invitation.Role.ToString())], ct);
        return invitation.Workspace!;
    }

    public static string NormalizeEmail(string? email)
    {
        var value = (email ?? "").Trim().ToLowerInvariant();
        if (value.Length is 0 or > 256 ||
            !MailAddress.TryCreate(value, out var parsed) ||
            !string.Equals(parsed.Address, value, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Enter a valid email address.", nameof(email));
        return value;
    }

    private async Task<string> UniqueSlugAsync(string source, CancellationToken ct)
    {
        var stem = NonSlug().Replace((source ?? "").Trim().ToLowerInvariant(), "-").Trim('-');
        if (stem.Length == 0) stem = "workspace";
        if (stem.Length > 45) stem = stem[..45].Trim('-');
        var candidate = stem;
        for (var n = 2; await db.Workspaces.IgnoreQueryFilters().AnyAsync(w => w.Slug == candidate, ct); n++)
            candidate = $"{stem}-{n}";
        return candidate;
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlug();
}
