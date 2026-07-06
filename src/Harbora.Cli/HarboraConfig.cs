using System.Text.Json;

namespace Harbora.Cli;

/// <summary>
/// CLI credential + project config. Global auth lives in ~/.harbora/config.json; per-project
/// defaults (the app slug) live in ./harbora.yml so `harbora deploy` needs no arguments in CI.
/// </summary>
public sealed class HarboraConfig
{
    public string? Server { get; set; }
    public string? Token { get; set; }

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbora");
    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static HarboraConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new HarboraConfig();
        return JsonSerializer.Deserialize<HarboraConfig>(File.ReadAllText(ConfigPath)) ?? new HarboraConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Reads the app slug from ./harbora.yml (a single `app: my-slug` line is enough).</summary>
    public static string? ReadProjectSlug()
    {
        foreach (var file in new[] { "harbora.yml", "harbora.yaml", "deployx.yml" })
        {
            if (!File.Exists(file)) continue;
            foreach (var line in File.ReadAllLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("app:", StringComparison.OrdinalIgnoreCase))
                    return trimmed[4..].Trim().Trim('"', '\'');
            }
        }
        return null;
    }
}
