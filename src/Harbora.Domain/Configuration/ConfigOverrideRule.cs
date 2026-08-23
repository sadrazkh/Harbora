using Harbora.Domain.Apps;
using Harbora.Domain.Common;

namespace Harbora.Domain.Configuration;

/// <summary>
/// One rule: "inside this app's own config file, at this key path, put this value" (C2, 2026-08-22
/// config-delivery plan). This is the mechanism a .NET developer actually wants — <c>appsettings.json</c>
/// stays in Git with a placeholder, and Harbora replaces the real value at deploy time, so a password
/// never has to be committed and nobody has to rewrite their code to read environment variables.
///
/// <para>
/// Applied inside the built image's container, never baked into the image itself — see
/// <c>Harbora.Infrastructure.Deployments.DeploymentPipeline</c>'s own call site. Every deployment
/// (including a rollback or a plain redeploy) re-resolves every rule against the app's current
/// panel state and re-applies it to the freshly created container, the same "never carried from a
/// previous run" guarantee <see cref="AppConfigGroup.HasUnpublishedChanges"/> already gives config
/// groups and buckets.
/// </para>
/// </summary>
public class ConfigOverrideRule : BaseEntity
{
    public Guid AppId { get; set; }
    public App? App { get; set; }

    /// <summary>The file's path inside the container — <c>appsettings.Production.json</c>,
    /// <c>config/database.yml</c>, an absolute path, whatever the app actually reads. Resolved
    /// relative to the container's working directory when not absolute.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Set only when the file is not named conventionally for its real format (a Rails
    /// <c>config/database</c> with no extension, a TOML file saved as <c>.conf</c>). Null means
    /// "detect from <see cref="FilePath"/>'s extension" — see <see cref="ConfigFileFormatDetector"/>.
    /// </summary>
    public ConfigFileFormat? FormatOverride { get; set; }

    /// <summary>
    /// The key path, in whatever idiom <see cref="FormatOverride"/> (or the detected format) actually
    /// uses — <c>ConnectionStrings:Default</c> for JSON, <c>production.adapter</c> for YAML, a bare
    /// <c>DATABASE_URL</c> for <c>.env</c>, <c>section.key</c> for INI/TOML, an XPath-ish route for
    /// XML. Deliberately one syntax per format rather than one syntax forced onto all five.
    /// </summary>
    public string KeyPath { get; set; } = string.Empty;

    public ConfigOverrideValueKind ValueKind { get; set; }

    /// <summary>Plaintext, used only when <see cref="ValueKind"/> is <see cref="ConfigOverrideValueKind.Literal"/>.</summary>
    public string? LiteralValue { get; set; }

    /// <summary>Ciphertext via <c>ISecretProtector</c>, used only when <see cref="ValueKind"/> is
    /// <see cref="ConfigOverrideValueKind.Secret"/>. Never decrypted back to the panel — masked in
    /// the UI like every other secret.</summary>
    public string? EncryptedSecretValue { get; set; }

    /// <summary>
    /// The attached service this rule points at, used only when <see cref="ValueKind"/> is
    /// <see cref="ConfigOverrideValueKind.AttachedServiceConnectionString"/>. Deliberately an opaque
    /// id rather than a typed foreign key to C1's attachment table: C2 does not depend on C1's
    /// schema landing first — <c>IAttachedServiceConnectionStringResolver</c> is handed this id and
    /// decides for itself whether it still means anything.
    /// </summary>
    public Guid? AttachedServiceReferenceId { get; set; }

    /// <summary>
    /// Deterministic application order when several rules target the same file — lower first. Two
    /// rules for the same file and key path is almost certainly a mistake, but ordering still makes
    /// the outcome predictable rather than a race, matching how <see cref="AppConfigGroup.AttachOrder"/>
    /// resolves the same kind of ambiguity for config groups.
    /// </summary>
    public int Order { get; set; }

    /// <summary>
    /// The <see cref="AppConfigGroup.HasUnpublishedChanges"/> idiom, reused rather than reinvented.
    /// True whenever this app's running container might not carry this rule's current value (just
    /// created or edited, or its literal/secret changed) — cleared only when a deployment for this
    /// app succeeds and re-applies every rule fresh.
    /// </summary>
    public bool HasUnpublishedChanges { get; set; } = true;
}
