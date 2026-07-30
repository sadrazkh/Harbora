using System.Text.Json;
using System.Text.Json.Serialization;

namespace Harbora.Cli;

/// <summary>One logged-in account: a server, and a token for it.</summary>
public sealed class Profile
{
    /// <summary>Shown when choosing between accounts — the email, or the server if it is unknown.</summary>
    public string Name { get; set; } = "";
    public string Server { get; set; } = "";
    public string Token { get; set; } = "";
}

/// <summary>
/// CLI credentials in <c>~/.harbora/config.json</c>.
///
/// More than one account can be signed in at once, because people do have a work panel and their own,
/// and <c>harbora login</c> used to overwrite whichever one was already there. The old single
/// <c>Server</c>/<c>Token</c> pair is still read, so an existing install keeps working and is migrated
/// the next time the file is written.
/// </summary>
public sealed class HarboraConfig
{
    /// <summary>Legacy single-account fields. Still honoured on load; not written again.</summary>
    public string? Server { get; set; }
    public string? Token { get; set; }

    public List<Profile> Profiles { get; set; } = [];

    /// <summary>Which profile commands use unless told otherwise.</summary>
    public string? Current { get; set; }

    [JsonIgnore]
    public bool HasAny => Profiles.Count > 0;

    /// <summary>True when a choice genuinely has to be made rather than assumed.</summary>
    [JsonIgnore]
    public bool NeedsAccountChoice => Profiles.Count > 1;

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".harbora");

    private static string ConfigPath => Path.Combine(ConfigDir, "config.json");

    public static HarboraConfig Load()
    {
        if (!File.Exists(ConfigPath)) return new HarboraConfig();

        HarboraConfig cfg;
        try
        {
            cfg = JsonSerializer.Deserialize<HarboraConfig>(File.ReadAllText(ConfigPath)) ?? new HarboraConfig();
        }
        catch (JsonException)
        {
            // A corrupt config should send someone to `harbora login`, not print a parser error.
            return new HarboraConfig();
        }

        return Migrate(cfg);
    }

    /// <summary>
    /// Folds a pre-profiles config into the profile list, so the very next command already behaves
    /// as though the account had been added properly.
    /// </summary>
    public static HarboraConfig Migrate(HarboraConfig cfg)
    {
        if (cfg.Profiles.Count == 0 && !string.IsNullOrWhiteSpace(cfg.Server) && !string.IsNullOrWhiteSpace(cfg.Token))
        {
            cfg.Profiles.Add(new Profile { Name = cfg.Server!, Server = cfg.Server!.TrimEnd('/'), Token = cfg.Token! });
            cfg.Current = Key(cfg.Profiles[0]);
        }
        return cfg;
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        // The legacy pair is dropped on write: two copies of a live token means revoking one leaves
        // the other behind.
        Server = null;
        Token = null;
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        RestrictPermissions();
    }

    /// <summary>Adds or refreshes the profile for an account, and makes it current.</summary>
    public Profile Upsert(string name, string server, string token)
    {
        server = server.TrimEnd('/');

        var existing = Profiles.FirstOrDefault(
            p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase)
                 && string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        // A migrated config has no idea who it belongs to, so the profile is named after the server.
        // Signing in then learns the real name — and without this it would file that as a *second*
        // account on the same server, and every command would start asking which of the two to use.
        existing ??= Profiles.FirstOrDefault(
            p => string.Equals(p.Server, server, StringComparison.OrdinalIgnoreCase) && IsPlaceholderName(p));

        if (existing is null)
        {
            existing = new Profile { Name = name, Server = server, Token = token };
            Profiles.Add(existing);
        }
        else
        {
            existing.Name = name;
            existing.Server = server;
            existing.Token = token;
        }

        Current = Key(existing);
        return existing;
    }

    /// <summary>A profile named after its own server was created by migration, not by a login.</summary>
    private static bool IsPlaceholderName(Profile p) =>
        string.Equals(p.Name.TrimEnd('/'), p.Server.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The profile to use: the one named, the current one, or the only one there is.</summary>
    public Profile? Resolve(string? name = null)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return Profiles.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                   ?? Profiles.FirstOrDefault(p => Key(p).Contains(name!, StringComparison.OrdinalIgnoreCase));

        if (Profiles.Count == 1) return Profiles[0];
        return Profiles.FirstOrDefault(p => Key(p) == Current) ?? Profiles.FirstOrDefault();
    }

    public void Remove(Profile profile)
    {
        Profiles.Remove(profile);
        if (Current == Key(profile)) Current = Profiles.Count > 0 ? Key(Profiles[0]) : null;
    }

    public static string Key(Profile p) => $"{p.Name}@{p.Server}";

    /// <summary>Reads the app slug from ./harbora.yml (a single `app: my-slug` line is enough).</summary>
    public static string? ReadProjectSlug()
    {
        foreach (var file in ProjectConfig.FileNames)
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

    /// <summary>
    /// The file holds live tokens, so on Unix it is owner-only. Windows inherits the user profile's
    /// ACL, which is already restricted to that account.
    /// </summary>
    private static void RestrictPermissions()
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(ConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
        catch { /* a config that saved but could not be chmod'ed still beats no config */ }
    }
}
