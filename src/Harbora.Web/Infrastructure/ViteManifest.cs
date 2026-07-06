using System.Text.Json;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Reads Vite's build manifest so Razor can reference hashed, content-addressed island bundles.
/// This is how Vue is "embedded" — Vite compiles components into wwwroot/build and Razor pulls
/// the right hashed file in, exactly like linking a versioned jQuery bundle. No separate SPA server.
/// </summary>
public sealed class ViteManifest
{
    private readonly Dictionary<string, ViteChunk>? _chunks;
    private readonly bool _devServer;
    private readonly string _devServerUrl;

    public ViteManifest(IWebHostEnvironment env, IConfiguration config)
    {
        _devServer = config.GetValue("Vite:UseDevServer", false);
        _devServerUrl = config["Vite:DevServerUrl"] ?? "http://localhost:5173";

        var manifestPath = Path.Combine(env.WebRootPath, "build", ".vite", "manifest.json");
        if (!_devServer && File.Exists(manifestPath))
        {
            var json = File.ReadAllText(manifestPath);
            _chunks = JsonSerializer.Deserialize<Dictionary<string, ViteChunk>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }

    public bool UseDevServer => _devServer;
    public string DevServerUrl => _devServerUrl;

    public (string? Js, IReadOnlyList<string> Css) Resolve(string entry)
    {
        if (_chunks is null || !_chunks.TryGetValue(entry, out var chunk))
            return (null, Array.Empty<string>());
        var css = chunk.Css?.Select(c => "/build/" + c).ToList() ?? new List<string>();
        return ("/build/" + chunk.File, css);
    }

    public sealed class ViteChunk
    {
        public string File { get; set; } = string.Empty;
        public List<string>? Css { get; set; }
    }
}
