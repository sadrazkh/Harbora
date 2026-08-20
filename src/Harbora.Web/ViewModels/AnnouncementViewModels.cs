namespace Harbora.Web.ViewModels;

/// <summary>One row of the platform announcements admin list.</summary>
public sealed record AnnouncementRow(
    Guid Id,
    string Title,
    string Body,
    string TitleFa,
    string BodyFa,
    Harbora.Domain.Common.AlertSeverity Severity,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    string CreatedByEmail,
    DateTimeOffset CreatedAt,
    bool IsActiveNow);
