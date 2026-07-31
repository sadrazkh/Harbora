using Harbora.Domain.Common;

namespace Harbora.Domain.Identity;

public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>PBKDF2 hash in "iterations.salt.hash" (base64) format. Never logged, never returned by API.</summary>
    public string PasswordHash { get; set; } = string.Empty;

    public SystemRole Role { get; set; } = SystemRole.Member;
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// When true, this person's role only reaches the projects they have been granted. False — the
    /// default, and what everyone was before project grants existed — means it reaches everything in
    /// the workspace, so turning this on is a deliberate act and turning it off restores the old
    /// behaviour exactly.
    /// </summary>
    public bool ScopedToProjects { get; set; }

    /// <summary>Preferred UI culture: "fa" or "en". Drives RTL/LTR + localization.</summary>
    public string PreferredCulture { get; set; } = "fa";

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<ApiToken> Tokens { get; set; } = new List<ApiToken>();
    public ICollection<WorkspaceMember> Memberships { get; set; } = new List<WorkspaceMember>();
}
