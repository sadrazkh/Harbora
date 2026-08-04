using Harbora.Infrastructure.Templates;

namespace Harbora.Web.Infrastructure;

/// <summary>
/// Which template logos this build actually ships.
///
/// Read once at startup rather than stat-ed per tile. The catalogue page was asking the filesystem
/// whether a file existed for every card it drew, on every request — harmless at twenty templates
/// and the wrong shape for a question whose answer cannot change while the process is running.
/// </summary>
public sealed class TemplateLogoSet
{
    private readonly HashSet<string> _paths;

    public TemplateLogoSet(IWebHostEnvironment environment)
    {
        _paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var root = environment.WebRootPath;
        if (string.IsNullOrEmpty(root)) return;

        var folder = Path.Combine(root, "img", "apps");
        if (!Directory.Exists(folder)) return;

        foreach (var file in Directory.EnumerateFiles(folder, "*.svg"))
            _paths.Add(TemplateIcon.PathFor(Path.GetFileNameWithoutExtension(file)));
    }

    /// <summary>Passed straight to <see cref="TemplateIcon.For"/>, which owns the decision.</summary>
    public bool Has(string path) => _paths.Contains(path);
}
