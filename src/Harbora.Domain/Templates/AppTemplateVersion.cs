using Harbora.Domain.Common;

namespace Harbora.Domain.Templates;

/// <summary>
/// Where a version sits in its life. Exactly one version of a template may be
/// <see cref="Recommended"/> — that is the one offered by default.
/// </summary>
public enum VersionLifecycle
{
    /// <summary>The default choice. One per template.</summary>
    Recommended = 0,

    /// <summary>Supported and safe to pick.</summary>
    Stable = 1,

    /// <summary>The release before the current stable; kept for people mid-upgrade.</summary>
    PreviousStable = 2,

    /// <summary>Old but still runs. Offered with a warning, not hidden.</summary>
    Legacy = 3,

    /// <summary>Going away. Existing deployments keep working; new ones are discouraged.</summary>
    Deprecated = 4,

    /// <summary>Must not be deployed again — a known-bad or end-of-life image.</summary>
    Unsupported = 5
}

/// <summary>
/// Whether a version is visible to tenants. New versions arrive as <see cref="Draft"/> and an
/// administrator publishes them: a registry gaining a tag is not the same as an operator deciding
/// their customers should run it.
/// </summary>
public enum VersionPublication
{
    Draft = 0,
    Published = 1
}

/// <summary>
/// One deployable version of a ready-made app.
///
/// The image is pinned by digest, not by tag. A tag is a moving pointer — deploying "postgres:16"
/// twice a month apart can produce two different databases, and the second one is the one that
/// breaks at 3am with no record of what changed. The tag is kept for display; the digest is what
/// gets deployed.
/// </summary>
public class AppTemplateVersion : BaseEntity
{
    public Guid AppTemplateId { get; set; }
    public AppTemplate? AppTemplate { get; set; }

    /// <summary>What a person calls this version — "16.4", "2.1", "latest-lts".</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>Repository without the tag, e.g. <c>postgres</c> or <c>ghcr.io/n8n-io/n8n</c>.</summary>
    public string ImageRepository { get; set; } = string.Empty;

    /// <summary>The tag as published, kept so the UI can show something recognisable.</summary>
    public string ImageTag { get; set; } = string.Empty;

    /// <summary>
    /// <c>sha256:…</c> — the immutable identity of the image. Null while a version is a draft that
    /// nobody has resolved yet; a published version without one cannot be deployed.
    /// </summary>
    public string? ImageDigest { get; set; }

    public VersionLifecycle Lifecycle { get; set; } = VersionLifecycle.Stable;
    public VersionPublication Publication { get; set; } = VersionPublication.Draft;

    /// <summary>Comma-separated, e.g. <c>amd64,arm64</c>. A node of another architecture cannot run it.</summary>
    public string SupportedArchitectures { get; set; } = "amd64";

    /// <summary>Minimum Harbora node version, or null when any node will do.</summary>
    public string? MinimumNodeVersion { get; set; }

    /// <summary>
    /// Environment, secrets, volumes, ports and health check for this version specifically —
    /// they change between versions, which is why the manifest lives here rather than on the
    /// template.
    /// </summary>
    public string ManifestJson { get; set; } = "{}";

    /// <summary>Shown before an upgrade. Empty means "nothing special about this one".</summary>
    public string? UpgradeNotes { get; set; }
    public string? UpgradeNotesFa { get; set; }

    /// <summary>Shown in red before an upgrade — data migrations, breaking changes.</summary>
    public string? MigrationWarnings { get; set; }
    public string? MigrationWarningsFa { get; set; }

    /// <summary>Whether a service on a later version may move back to this one.</summary>
    public bool AllowsDowngrade { get; set; }

    public DateTimeOffset? ReleasedAt { get; set; }

    /// <summary>Set when the registry check first saw this tag, so "new" can be shown to an admin.</summary>
    public DateTimeOffset? DiscoveredAt { get; set; }
}
