using Harbora.Domain.Common;

namespace Harbora.Domain.Templates;

/// <summary>How a logo was obtained, so the licence question can be answered later.</summary>
public enum AssetLicense
{
    /// <summary>Nobody recorded where it came from. Treated as unusable until someone does.</summary>
    Unknown = 0,

    /// <summary>The project's own mark, used to identify the project. Trademark, not copyright.</summary>
    ProjectTrademark = 1,

    /// <summary>Published under a permissive licence that allows redistribution.</summary>
    Permissive = 2,

    /// <summary>Drawn for Harbora — no third-party rights involved.</summary>
    OriginalWork = 3
}

/// <summary>
/// A template's logo, stored in this repository rather than hotlinked.
///
/// Hotlinking a logo means every panel page load tells a third party who is looking at it, and the
/// image disappears the day they move the file. The licence and source are recorded because "where
/// did this logo come from" is a question that arrives long after the person who added it has gone.
/// </summary>
public class AppTemplateAsset : BaseEntity
{
    public Guid AppTemplateId { get; set; }
    public AppTemplate? AppTemplate { get; set; }

    /// <summary>Web path under wwwroot, e.g. <c>/img/apps/postgres.svg</c>.</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Usually <c>svg</c>: it stays sharp at every size and is a few hundred bytes.</summary>
    public string Format { get; set; } = "svg";

    /// <summary>Where it came from — a URL or a short note. Never a hotlink target.</summary>
    public string? SourceUrl { get; set; }

    public AssetLicense License { get; set; } = AssetLicense.Unknown;

    /// <summary>Anything a future reader needs, e.g. "official mark, used to identify the project".</summary>
    public string? LicenseNote { get; set; }

    /// <summary>
    /// True when the mark is legible on both themes on its own. A logo that is pure white needs a
    /// tinted plate behind it in light mode, and the card has to know that before it draws it.
    /// </summary>
    public bool WorksOnBothThemes { get; set; } = true;
}
