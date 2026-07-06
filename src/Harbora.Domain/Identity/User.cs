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

    /// <summary>Preferred UI culture: "fa" or "en". Drives RTL/LTR + localization.</summary>
    public string PreferredCulture { get; set; } = "fa";

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<ApiToken> Tokens { get; set; } = new List<ApiToken>();
    public ICollection<WorkspaceMember> Memberships { get; set; } = new List<WorkspaceMember>();
}
