using Harbora.Infrastructure.Monitoring;

namespace Harbora.Web.ViewModels;

/// <summary>A bordered card with a header. The unit every screen in the design is built from.</summary>
public sealed record PanelModel(string Title, string? LinkText = null, string? LinkUrl = null);

/// <summary>One of the headline figures across the top of a page.</summary>
public sealed record StatCardModel(string Icon, string Label, MetricView Value, string? Delta = null);

/// <summary>
/// A template's mark, wherever it is drawn.
/// </summary>
/// <param name="Key">The template key — also the logo file's name by convention.</param>
/// <param name="Name">Used for the alt text, and for the initials when no logo ships.</param>
/// <param name="SizeClass">Extra classes for slots that are not the default size.</param>
/// <param name="Extra">Raw attributes a caller needs on the element, such as a script hook.</param>
public sealed record TemplateLogoModel(
    string? Key,
    string? Name,
    string? SizeClass = null,
    Microsoft.AspNetCore.Html.IHtmlContent? Extra = null);

/// <summary>
/// A specialist block that folds in Simple mode and is open in Advanced.
///
/// <paramref name="Open"/> is a decision rather than a preference:
/// <c>PanelSections.StartsOpen</c> makes it, so every page answers the same way and a rejected form
/// opens the block holding whatever it complained about.
/// </summary>
public sealed record AdvancedSectionModel(string Title, bool Open, string Icon = "sliders-horizontal");

/// <summary>
/// The semantic tones a pill may carry. Constants rather than loose strings because a mistyped
/// "sucess" silently falls through to the neutral tone, which reads as "nothing is wrong".
/// </summary>
public static class Tone
{
    public const string Ok = "ok";
    public const string Warn = "warn";
    public const string Error = "error";
    public const string Info = "info";
    public const string Idle = "idle";
}

/// <summary>Tone is semantic, not a colour: the palette decides what "warn" looks like.</summary>
public sealed record StatusPillModel(string Text, string Tone);

/// <summary>Shown instead of an empty table, with the one action that fills it.</summary>
public sealed record EmptyStateModel(string Icon, string Message, string? ActionText = null, string? ActionUrl = null);

/// <summary>
/// The usage tab's window picker — 1 hour / 24 hours / 7 days, per
/// <see cref="Harbora.Infrastructure.Monitoring.UsageRangeWindow"/>. One partial shared by the app
/// and database Usage tabs rather than two copies, so the two cannot drift into offering different
/// windows or drawing the selected one differently.
/// </summary>
public sealed record UsageRangeControlModel(int SelectedMinutes);

/// <summary>A measurement and its label, routed through the honesty gate.</summary>
public sealed record MetricModel(MetricView View, string Label);

/// <summary>The title block every page opens with.</summary>
public sealed record PageHeaderModel(string Title, string? Description = null, string? Badge = null);

/// <summary>
/// One sidebar row — a pinned essential or a member of a collapsible group, drawn the same way
/// either side of that line so a group that folds and one that never does still look like one
/// family of controls. <c>Entry</c> already carries the locked/feature-gated state
/// <c>NavigationMap.Draw</c> computed; this model only adds the resolved label and, for a pinned
/// row, the one live count the redesign's stat strip also shows.
/// </summary>
public sealed record SidebarItemModel(
    Harbora.Infrastructure.Navigation.NavEntry Entry,
    string Label,
    bool Active,
    string? Count = null,
    bool CountDanger = false);

/// <summary>One row of the users table.</summary>
/// <param name="LastLoginAt">Null for an account that has never been used — not a date of zero.</param>
public sealed record UserRow(
    Guid Id,
    string Email,
    string DisplayName,
    Harbora.Domain.Common.SystemRole Role,
    bool IsActive,
    bool ScopedToProjects,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? EmailVerifiedAt = null,
    Guid? PersonalWorkspaceId = null,
    int WorkspaceCount = 0);
