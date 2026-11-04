using System.Xml;
using System.Xml.Linq;

namespace Harbora.Shared.Inspection;

/// <summary>Reads the project the pipeline would publish, and the projects it references.</summary>
internal static class DotNetManifest
{
    private const int MaxProjects = 64;

    private static readonly XmlReaderSettings Plain = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null
    };

    /// <summary><c>Buildpacks.FindCsproj</c>'s choice, over the list <see cref="SourceTree.Csprojs"/> finds in its order.</summary>
    public static string? Pick(IReadOnlyList<string> projects)
    {
        if (projects.Count == 0) return null;
        return projects.FirstOrDefault(p =>
                   p.Contains("web", StringComparison.OrdinalIgnoreCase) ||
                   p.Contains("api", StringComparison.OrdinalIgnoreCase) ||
                   p.Contains("server", StringComparison.OrdinalIgnoreCase))
               ?? projects[0];
    }

    /// <summary>
    /// Follows project references rather than reading every project in the tree. The database package
    /// usually sits in a <c>Data</c> project the web one references, so the web project alone would
    /// miss it; while a test project's <c>Microsoft.EntityFrameworkCore.Sqlite</c> is no business of a
    /// running app's, so every project in the tree would invent a problem it does not have.
    /// </summary>
    public static void Read(SourceTree app, ProjectFacts facts)
    {
        if (Pick(app.Csprojs()) is not { } chosen) return;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { chosen };
        var pending = new Queue<string>();
        pending.Enqueue(chosen);

        while (pending.TryDequeue(out var file))
        {
            if (Parse(app, file) is not { } project)
            {
                // A reference to something that is not there is the build's problem to report, not a
                // file that failed to say something.
                if (file == chosen || app.HasFile(file)) facts.UnreadableFiles.Add(file);
                continue;
            }

            if (file == chosen) facts.DotNetSdk = SdkOf(project);

            foreach (var element in project.Descendants())
            {
                if (element.Attribute("Include")?.Value.Trim() is not { Length: > 0 } include) continue;

                if (element.Name.LocalName == "PackageReference")
                {
                    facts.Dependencies.Add(new Dependency(
                        Ecosystem.DotNet, include, include.ToLowerInvariant(), file, "PackageReference", true));
                }
                else if (element.Name.LocalName == "ProjectReference" &&
                         SourceTree.Resolve(file, include) is { } target &&
                         seen.Count < MaxProjects && seen.Add(target))
                {
                    pending.Enqueue(target);
                }
            }
        }
    }

    private static XDocument? Parse(SourceTree app, string file)
    {
        if (app.ReadText(file) is not { } text) return null;

        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Plain);
            return XDocument.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static string? SdkOf(XDocument project)
    {
        var root = project.Root;
        return root?.Attribute("Sdk")?.Value
               ?? root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Sdk")?.Attribute("Name")?.Value
               ?? root?.Elements().FirstOrDefault(e => e.Name.LocalName == "Import")?.Attribute("Sdk")?.Value;
    }
}
