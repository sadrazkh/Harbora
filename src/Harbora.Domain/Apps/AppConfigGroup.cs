using Harbora.Domain.Common;

namespace Harbora.Domain.Apps;

/// <summary>
/// An app's attachment to a <see cref="ConfigGroup"/>. The join carries the two facts precedence and
/// honesty both depend on: the order this app attached the group in, and whether this app's running
/// container still reflects what the group currently says.
/// </summary>
public class AppConfigGroup : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    public Guid ConfigGroupId { get; set; }
    public ConfigGroup? ConfigGroup { get; set; }

    /// <summary>
    /// Attachment order for this app's groups — assigned as (current max for this app) + 1 when the
    /// group is attached, never reused. The app's own <see cref="EnvironmentVariable"/> rows always
    /// win regardless of this number; among groups, the higher order (attached later) wins on a
    /// shared key. Detaching and reattaching a group moves it to the back of this order, the same way
    /// a person re-adding something expects it to win over what has sat there longer.
    /// </summary>
    public int AttachOrder { get; set; }

    /// <summary>
    /// The "applies on next deploy" flag <c>FunctionDefinition.HasUnpublishedChanges</c> established
    /// — reused rather than reinvented. True whenever this app's running container might not reflect
    /// what the group currently says (just attached, or the group's entries changed since); cleared
    /// only when a deployment for this app succeeds and assembles the container's environment from
    /// the group's current rows. Editing a group therefore never restarts anything by itself — it
    /// only marks every attached app stale until its own next deploy picks the change up.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
