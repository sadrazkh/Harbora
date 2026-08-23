namespace Harbora.Domain.Configuration;

/// <summary>
/// The five config-file idioms C2 (2026-08-22 config-delivery plan) ships in v1, per the owner's
/// correction: <i>"Every framework or style of programming can have its own... all of them have to
/// be supported, because everyone uses them — not just me."</i>
///
/// <para>
/// Each carries its own key-path syntax rather than one forced onto all five — see
/// <c>Harbora.Infrastructure.Configuration.IConfigFileEditor</c>'s own doc for what a path looks
/// like in each one.
/// </para>
/// </summary>
public enum ConfigFileFormat
{
    /// <summary>appsettings.json and friends. Key path: colon-separated (<c>ConnectionStrings:Default</c>),
    /// the exact idiom ASP.NET Core's own configuration binder uses.</summary>
    Json,

    /// <summary>Rails <c>config/database.yml</c>, Spring <c>application.yml</c>. Key path:
    /// dot-separated through nested mappings (<c>production.adapter</c>).</summary>
    Yaml,

    /// <summary>Laravel/.env, Django, Node <c>.env</c> files. Key path: the bare variable name
    /// (<c>DATABASE_URL</c>) — there is no nesting to speak of.</summary>
    Env,

    /// <summary>Classic <c>.ini</c>/<c>.conf</c> files (Python, PHP). Key path: <c>section.key</c>,
    /// or a bare key for one outside any section.</summary>
    Ini,

    /// <summary>TOML (Python's <c>pyproject.toml</c>-adjacent config, Rust, others). Key path:
    /// dot-separated through tables, the same shape as INI's <c>section.key</c>.</summary>
    Toml,

    /// <summary><c>web.config</c>/<c>app.config</c> — classic .NET, still widespread. Key path: an
    /// XPath-ish route to an element or attribute
    /// (<c>connectionStrings/add[@name='Default']/@connectionString</c>).</summary>
    Xml
}
