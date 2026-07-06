using System.Formats.Tar;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Builds a Docker build-context tarball. Shared by the local engine and the remote engine
/// (which uploads the tar to an agent), so context packing is identical everywhere.
/// </summary>
public static class DockerTar
{
    public static Stream Create(string sourceDir)
    {
        var ms = new MemoryStream();
        using (var writer = new TarWriter(ms, TarEntryFormat.Pax, leaveOpen: true))
        {
            var root = Path.GetFullPath(sourceDir);
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                // Skip heavy/irrelevant dirs to keep the context lean.
                if (rel.StartsWith(".git/") || rel.Contains("/node_modules/") || rel.StartsWith("node_modules/"))
                    continue;
                writer.WriteEntry(file, rel);
            }
        }
        ms.Position = 0;
        return ms;
    }
}
