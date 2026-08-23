namespace Harbora.Domain.Configuration;

/// <summary>
/// Format is detected from the file's own extension, with an explicit override — a config file is
/// not always named conventionally (<c>appsettings.json</c> is obvious; a Rails app's
/// <c>config/database</c> with no extension, or a <c>.conf</c> that is actually TOML, is not).
/// </summary>
public static class ConfigFileFormatDetector
{
    /// <summary>The format a rule actually uses: its explicit override when set, otherwise whatever
    /// the file's extension implies. Null when neither says anything usable.</summary>
    public static ConfigFileFormat? Resolve(string filePath, ConfigFileFormat? explicitOverride) =>
        explicitOverride ?? FromExtension(filePath);

    public static ConfigFileFormat? FromExtension(string filePath)
    {
        var ext = System.IO.Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "json" => ConfigFileFormat.Json,
            "yml" or "yaml" => ConfigFileFormat.Yaml,
            "env" => ConfigFileFormat.Env,
            "ini" or "conf" => ConfigFileFormat.Ini,
            "toml" => ConfigFileFormat.Toml,
            "config" or "xml" => ConfigFileFormat.Xml,
            _ => null
        };
    }
}
