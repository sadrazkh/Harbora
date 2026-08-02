using Harbora.Infrastructure.Monitoring;

namespace Harbora.Web.ViewModels;

/// <summary>A bordered card with a header. The unit every screen in the design is built from.</summary>
public sealed record PanelModel(string Title, string? LinkText = null, string? LinkUrl = null);

/// <summary>One of the headline figures across the top of a page.</summary>
public sealed record StatCardModel(string Icon, string Label, MetricView Value, string? Delta = null);

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

/// <summary>A measurement and its label, routed through the honesty gate.</summary>
public sealed record MetricModel(MetricView View, string Label);

/// <summary>The title block every page opens with.</summary>
public sealed record PageHeaderModel(string Title, string? Description = null, string? Badge = null);

/// <summary>One row of the users table.</summary>
/// <param name="LastLoginAt">Null for an account that has never been used — not a date of zero.</param>
public sealed record UserRow(
    Guid Id,
    string Email,
    string DisplayName,
    Harbora.Domain.Common.SystemRole Role,
    bool IsActive,
    bool ScopedToProjects,
    DateTimeOffset? LastLoginAt);
