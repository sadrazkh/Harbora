using System.Text.Json;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Reads Vite's build manifest so Razor can reference hashed, content-addressed island bundles.
/// This is how Vue is "embedded" — Vite compiles components into wwwroot/build and Razor pulls
/// the right hashed file in, exactly like linking a versioned jQuery bundle. No separate SPA server.
///
/// The manifest lives at wwwroot/build/manifest.json (configured in vite.config.ts). The legacy
/// .vite/manifest.json location is also probed for dev builds — but note dot-folders are excluded
/// from `dotnet publish`, which is exactly why the primary location is not hidden.
/// Loading is lazy + retried so the app can't get wedged if assets appear after startup.
/// </summary>
public sealed class ViteManifest
{
    private readonly string[] _candidatePaths;
    private readonly bool _devServer;
    private readonly string _devServerUrl;
    private Dictionary<string, ViteChunk>? _chunks;

    public ViteManifest(IWebHostEnvironment env, IConfiguration config)
    {
        _devServer = config.GetValue("Vite:UseDevServer", false);
        _devServerUrl = config["Vite:DevServerUrl"] ?? "http://localhost:5173";
        _candidatePaths =
        [
            Path.Combine(env.WebRootPath, "build", "manifest.json"),
            Path.Combine(env.WebRootPath, "build", ".vite", "manifest.json"),
        ];
        if (!_devServer) TryLoad();
    }

    public bool UseDevServer => _devServer;
    public string DevServerUrl => _devServerUrl;

    public (string? Js, IReadOnlyList<string> Css) Resolve(string entry)
    {
        if (_chunks is null && !_devServer) TryLoad(); // retry — assets may land after startup

        if (_chunks is null || !_chunks.TryGetValue(entry, out var chunk))
            return (null, Array.Empty<string>());
        var css = chunk.Css?.Select(c => "/build/" + c).ToList() ?? new List<string>();
        return ("/build/" + chunk.File, css);
    }

    private void TryLoad()
    {
        foreach (var path in _candidatePaths)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                _chunks = JsonSerializer.Deserialize<Dictionary<string, ViteChunk>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                return;
            }
            catch (Exception)
            {
                // Corrupt/partial manifest (e.g. mid-build) — try the next candidate or retry later.
            }
        }
    }

    public sealed class ViteChunk
    {
        public string File { get; set; } = string.Empty;
        public List<string>? Css { get; set; }
    }
}
