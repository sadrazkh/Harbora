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

    /// <summary>
    /// Simple or Advanced, or null when this person has never chosen and should follow the
    /// platform default. Stored on the account rather than in the browser so the choice travels
    /// with the person instead of with the device.
    /// </summary>
    public PanelMode? PanelMode { get; set; }

    /// <summary>
    /// Whether the ready-made apps shelf beside the application list is open, or null when this
    /// person has never said. On the account for the same reason the panel mode is: a browser flag
    /// would make it a property of the laptop, and the same person would meet the panel again on
    /// their phone.
    /// </summary>
    public bool? ShowQuickStart { get; set; }

    /// <summary>Whether the counts panel beside the application list is open. Null is "never said".</summary>
    public bool? ShowOverview { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public ICollection<ApiToken> Tokens { get; set; } = new List<ApiToken>();
    public ICollection<WorkspaceMember> Memberships { get; set; } = new List<WorkspaceMember>();
}
