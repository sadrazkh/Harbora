namespace Harbora.Web.Infrastructure;

/// <summary>
/// A source archive a browser handed over: where it goes, and how it is written there.
///
/// <para>
/// The CLI's route (<c>ApiV1Controller.DeployArchive</c>) reads a raw request body. A browser sends a
/// form, so the two share a directory and a ceiling but not the copy. Written down here because two
/// actions take a browser's archive — starting a launch and sending an uploaded app a new version —
/// and both have to leave nothing half-written behind.
/// </para>
/// </summary>
public static class UploadedArchive
{
    /// <summary>
    /// The same ceiling <c>ApiV1Controller</c> uses for a pushed source tree.
    ///
    /// <para>
    /// An action taking one needs this twice. <c>[RequestSizeLimit]</c> is the server's limit on the
    /// body; <c>[RequestFormLimits]</c> is the form reader's limit on a multipart one, which is 128 MB
    /// by default and knows nothing about the other. With only the first, anything between the two
    /// ends on an error page that says nothing about size: a 500, on the in-process host.
    /// </para>
    /// </summary>
    public const long MaxBytes = 512L * 1024 * 1024;

    /// <summary>What the file input offers. The real test is <see cref="LooksLikeArchive"/>.</summary>
    public const string Accept = ".zip,.tar.gz,.tgz";

    private static string Directory(string workDir) => Path.Combine(workDir, "_uploads");

    /// <summary>
    /// Where a launch's archive goes: the person's own file name, in a folder of its own.
    ///
    /// <para>
    /// The folder is what keeps the name usable. <c>LaunchStartRequest</c> carries only this path, and
    /// the path is all the launch has to name the app and label the source with, so a random token in
    /// the file name would come back as the suggested name of the app.
    /// </para>
    /// </summary>
    public static string PathForLaunch(string workDir, string? clientFileName) =>
        Path.Combine(Directory(workDir), $"launch-{Guid.CreateVersion7():N}", SafeFileName(clientFileName));

    /// <summary>Where a new version of an app goes, named the way the CLI's route names one.</summary>
    public static string PathForApp(string workDir, string appSlug, string? clientFileName) =>
        Path.Combine(Directory(workDir), $"{appSlug}-{Guid.CreateVersion7():N}{Extension(clientFileName)}");

    /// <summary>Copies it to disk. Never leaves a half-written archive for the pipeline to trip over.</summary>
    public static async Task SaveAsync(IFormFile file, string path, CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            await using var target = File.Create(path);
            await file.CopyToAsync(target, ct);
        }
        catch (Exception)
        {
            Delete(path);
            throw;
        }
    }

    /// <summary>
    /// The first two bytes, the same way the extractor tells the two formats apart. Refusing here says
    /// so on the form; letting a Word document through says so later, as a failed build.
    /// </summary>
    public static bool LooksLikeArchive(IFormFile file)
    {
        using var stream = file.OpenReadStream();
        Span<byte> head = stackalloc byte[2];
        return stream.ReadAtLeast(head, 2, throwOnEndOfStream: false) == 2
            && head[0] switch
            {
                0x1F => head[1] == 0x8B,
                0x50 => head[1] == 0x4B,
                _ => false
            };
    }

    /// <summary>Removes an archive nothing is going to read, and the folder a launch's sat in.</summary>
    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);

            var folder = Path.GetDirectoryName(path);
            if (folder is not null && Path.GetFileName(folder).StartsWith("launch-", StringComparison.Ordinal)
                && System.IO.Directory.Exists(folder) && !System.IO.Directory.EnumerateFileSystemEntries(folder).Any())
                System.IO.Directory.Delete(folder);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
    }

    /// <summary>
    /// A file name as a browser reports it is whatever the sender says, separators and all, so only
    /// letters, digits and a few marks survive. Letters of any script, because a Persian project name
    /// is as good a name as any.
    /// </summary>
    private static string SafeFileName(string? clientFileName)
    {
        var extension = Extension(clientFileName);
        var name = clientFileName ?? "";
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) name = name[..^extension.Length];

        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray())
            .Trim('-', '.', '_');
        if (safe.Length > 80) safe = safe[..80].Trim('-', '.', '_');

        return (safe.Length == 0 ? "source" : safe) + extension;
    }

    private static string Extension(string? clientFileName)
    {
        var name = clientFileName ?? "";
        if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) return ".tar.gz";
        if (name.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)) return ".tgz";
        return ".zip";
    }
}
