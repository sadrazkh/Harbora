using System.Text;

namespace Harbora.Shared.Inspection;

/// <summary>
/// The only thing in the inspector that touches the disk, so the rules of the road live in one place:
/// nothing over 1 MiB is read, a link is never followed out of the tree, a binary file reads as
/// nothing, and no failure escapes. Paths in and out are relative to this tree and use <c>/</c>.
/// </summary>
internal sealed class SourceTree
{
    private const long MaxFileBytes = 1024 * 1024;
    private const int MaxListedEntries = 5_000;

    // Enough for any repository whose checked-in tree is still source. Past it the inspector says
    // so with a note rather than taking as long as the tree does.
    private const int MaxWalkEntries = 20_000;
    private const int MaxWalkDepth = 16;

    private static readonly HashSet<string> NeverAnApp = new(StringComparer.Ordinal)
    {
        "node_modules", "vendor", "bin", "obj", "dist", "build", "venv", "__pycache__", "target", "__MACOSX"
    };

    private static readonly HashSet<string> SystemLitter = new(StringComparer.OrdinalIgnoreCase)
    {
        ".DS_Store", "Thumbs.db", "desktop.ini"
    };

    private static readonly EnumerationOptions OneLevel = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false
    };

    private readonly string _path;
    private readonly string _prefix;
    private readonly CsprojWalk _walk;

    public SourceTree(string root)
    {
        _path = root;
        _prefix = "";
        _walk = new CsprojWalk(root);
    }

    private SourceTree(string path, string prefix, CsprojWalk walk)
    {
        _path = path;
        _prefix = prefix;
        _walk = walk;
    }

    /// <summary>The same tree seen from one of its folders. The search and its budget are shared.</summary>
    public SourceTree Scope(string relative) =>
        new(Full(relative), _prefix + relative.Trim('/') + "/", _walk);

    /// <summary>True when the search for project files stopped at a limit rather than at the end of the tree.</summary>
    public bool WalkWasCutShort => _walk.CutShort;

    public bool HasFile(string relative) => Try(() => File.Exists(Full(relative)));

    public bool HasDirectory(string relative) => Try(() => Directory.Exists(Full(relative)));

    /// <summary>Null for anything missing, unreadable, over the limit, binary, or reached through a link.</summary>
    public string? ReadText(string relative)
    {
        try
        {
            if (LeadsThroughLink(relative)) return null;

            var info = new FileInfo(Full(relative));
            if (!info.Exists || info.Length > MaxFileBytes) return null;

            // Not opened at all: opening a named pipe, which reports a length of 0, waits for ever.
            if (info.Length == 0) return "";

            // Byte-order marks are honoured because PowerShell writes UTF-16 by default, and a NUL
            // only counts after decoding so that such a file is not mistaken for a binary one.
            using var reader = new StreamReader(info.FullName, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = reader.ReadToEnd();
            return text.Contains('\0') ? null : text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The names of the files directly in this folder, links included.</summary>
    public IReadOnlyList<string> FileNames() =>
        List().Where(i => i is FileInfo).Select(i => i.Name).ToList();

    /// <summary>The folders directly in this one that could be an app: no links, nothing hidden, nothing on the skip list.</summary>
    public IReadOnlyList<string> AppFolders() =>
        List().Where(i => i is DirectoryInfo && !IsLink(i) && !i.Name.StartsWith('.') && !NeverAnApp.Contains(i.Name))
            .Select(i => i.Name).ToList();

    /// <summary>The one folder this folder is only a wrapper around, or null when it holds anything else.</summary>
    public string? SoleChildFolder()
    {
        if (FileNames().Any(n => !SystemLitter.Contains(n))) return null;
        return AppFolders() is [var only] ? only : null;
    }

    /// <summary>
    /// Every project file under this folder, relative to it, in the order the pipeline's own
    /// <c>Directory.EnumerateFiles</c> finds them. See <see cref="CsprojWalk"/> for why this one search
    /// ignores the skip list.
    /// </summary>
    public IReadOnlyList<string> Csprojs() =>
        _walk.Found.Where(p => p.StartsWith(_prefix, StringComparison.Ordinal))
            .Select(p => p[_prefix.Length..]).ToList();

    /// <summary>
    /// Resolves a path one file names, by its text alone: <paramref name="target"/> is relative to the
    /// folder of <paramref name="fromFile"/>, and null comes back when it climbs out of this tree.
    /// </summary>
    public static string? Resolve(string fromFile, string target)
    {
        var segments = fromFile.Split('/').SkipLast(1).ToList();
        foreach (var segment in target.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment != "..") { segments.Add(segment); continue; }
            if (segments.Count == 0) return null;
            segments.RemoveAt(segments.Count - 1);
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private string Full(string relative) =>
        relative.Length == 0 ? _path : Path.Combine(_path, relative.Replace('/', Path.DirectorySeparatorChar));

    private List<FileSystemInfo> List() => Entries(_path, MaxListedEntries);

    private static List<FileSystemInfo> Entries(string path, int limit)
    {
        try
        {
            return new DirectoryInfo(path).EnumerateFileSystemInfos("*", OneLevel).Take(limit).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private bool LeadsThroughLink(string relative)
    {
        var current = _path;
        foreach (var segment in relative.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && IsLink(info)) return true;
        }
        return false;
    }

    // The attribute alone would also catch a OneDrive placeholder, which is an ordinary file, and on
    // the CLI that would make a whole project read as empty. LinkTarget only answers for a real link.
    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return (info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Something that cannot even say what it is does not get followed.
            return true;
        }
    }

    private static bool Try(Func<bool> check)
    {
        try { return check(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// The one recursive search here, and the one that deliberately does not use the skip list: the
    /// pipeline's <c>FindCsproj</c> leaves out only <c>bin</c> and <c>obj</c>, so a project under
    /// <c>vendor/</c> really is what it will build, and saying otherwise would be the inspector
    /// disagreeing with the deploy it is proposing. <c>.git</c> is the one addition: nothing checked
    /// out lives there, and it could spend the whole budget on its own.
    ///
    /// <para>
    /// Breadth-first with one listing per folder, because that is the order
    /// <c>Directory.EnumerateFiles(AllDirectories)</c> yields, and "the first one" is the pipeline's
    /// answer when no project looks like a web one. Lazy, because a tree with a <c>package.json</c>
    /// at its root never asks. Run once from the inspected root and filtered for any folder inside
    /// it, which gives the list and the order a walk from that folder would.
    /// </para>
    /// </summary>
    private sealed class CsprojWalk(string root)
    {
        private List<string>? _found;

        public bool CutShort { get; private set; }

        public List<string> Found => _found ??= Run();

        private List<string> Run()
        {
            var found = new List<string>();
            var left = MaxWalkEntries;
            var pending = new Queue<(string Path, string Relative, int Depth)>();
            pending.Enqueue((root, "", 0));

            var matching = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            while (left > 0 && pending.TryDequeue(out var folder))
            {
                foreach (var entry in Entries(folder.Path, left))
                {
                    left--;
                    if (entry is FileInfo)
                    {
                        if (entry.Name.EndsWith(".csproj", matching)) found.Add(folder.Relative + entry.Name);
                        continue;
                    }

                    if (entry.Name is "bin" or "obj" or ".git" || IsLink(entry)) continue;
                    if (folder.Depth >= MaxWalkDepth) { CutShort = true; continue; }
                    pending.Enqueue((entry.FullName, folder.Relative + entry.Name + "/", folder.Depth + 1));
                }
            }

            if (left <= 0) CutShort = true;
            return found;
        }
    }
}
