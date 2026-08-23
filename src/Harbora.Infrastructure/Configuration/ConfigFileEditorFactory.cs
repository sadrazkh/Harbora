using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;

namespace Harbora.Infrastructure.Configuration;

/// <summary>One editor per <see cref="ConfigFileFormat"/> — the fixed set C2 (2026-08-22
/// config-delivery plan) ships in v1.</summary>
public sealed class ConfigFileEditorFactory
{
    private readonly IReadOnlyDictionary<ConfigFileFormat, IConfigFileEditor> _editors =
        new Dictionary<ConfigFileFormat, IConfigFileEditor>
        {
            [ConfigFileFormat.Json] = new JsonConfigFileEditor(),
            [ConfigFileFormat.Yaml] = new YamlConfigFileEditor(),
            [ConfigFileFormat.Env] = new EnvConfigFileEditor(),
            [ConfigFileFormat.Ini] = new IniConfigFileEditor(),
            [ConfigFileFormat.Toml] = new TomlConfigFileEditor(),
            [ConfigFileFormat.Xml] = new XmlConfigFileEditor()
        };

    public IConfigFileEditor For(ConfigFileFormat format) => _editors[format];
}
