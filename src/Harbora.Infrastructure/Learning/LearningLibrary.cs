using System.Text.RegularExpressions;
using Harbora.Shared;
using Markdig;

namespace Harbora.Infrastructure.Learning;

/// <summary>
/// One tutorial chapter as found on disk.
/// </summary>
/// <param name="Slug">
/// The route segment for the chapter — the file name without its extension, so the numeric prefix
/// that orders the chapters is also what shows up in the address bar.
/// </param>
/// <param name="Number">The numeric prefix, parsed once so callers sorting the list do not re-parse it.</param>
/// <param name="Title">The chapter's own first-level heading, read fresh from the file rather than kept
/// anywhere else — the file is the one place a rename cannot drift away from.</param>
/// <param name="FileName">The file on disk, e.g. <c>03-applications.md</c>.</param>
public sealed record LearningChapter(string Slug, int Number, string Title, string FileName);

/// <summary>
/// Reads the tutorial chapters that live as markdown in <c>docs/tutorial</c>, renders one to HTML on
/// request, and decides which images alongside them may be served. No HTTP, no Razor — those belong
/// to the controller and views built on top of this.
///
/// <para>
/// Chapters are discovered by listing the directory and sorting on the numeric prefix in each file
/// name — never a list kept here, which would be the thing this class exists to protect against
/// going stale. <c>README.md</c> is the index for the docs site rather than a chapter, and is
/// excluded by the same rule that finds the rest: it carries no numeric prefix, so it never matches.
/// </para>
///
/// <para>
/// The image guard exists for a concrete reason: a raw screenshot taken straight off a running panel
/// carries webhook secrets, object-storage keys and account emails. <c>.gitignore</c> keeps a raw
/// capture out of the repository, but not out of a developer's working directory — and a render path
/// that served "everything under <c>img/</c>" would publish one from there with nothing in git ever
/// looking wrong. Only an annotated capture, the one whose secrets <c>annotations.json</c> has
/// painted out, may be served, and its name must resolve inside the image directory: no rooted path,
/// no climbing out with <c>..</c>.
/// </para>
/// </summary>
public sealed class LearningLibrary
{
    private static readonly Regex NumberedChapter = new(@"^(?<number>\d+)-.+\.md$", RegexOptions.Compiled);
    private static readonly Regex FirstHeading =
        new(@"^#[ \t]+(?<title>.+?)\s*$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// These chapter files are trusted content in the repository today, but the render path outlives
    /// that assumption — raw HTML embedded in a chapter is shown as text, never executed as markup.
    /// </summary>
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();

    private readonly string _chaptersRoot;
    private readonly string _imgRoot;

    public LearningLibrary(string chaptersRoot)
    {
        _chaptersRoot = chaptersRoot;
        _imgRoot = Path.Combine(chaptersRoot, "img");
    }

    /// <summary>
    /// Every chapter, numbered and titled, in reading order. Reads the directory on every call rather
    /// than caching it once: these are a handful of small files read from local disk, and the cost of
    /// re-reading them is far smaller than the cost of a stale list surviving an edit.
    /// </summary>
    public IReadOnlyList<LearningChapter> Chapters() =>
        Directory.EnumerateFiles(_chaptersRoot, "*.md")
            .Select(path => (Path: path, Match: NumberedChapter.Match(Path.GetFileName(path))))
            .Where(f => f.Match.Success)
            .Select(f => new LearningChapter(
                Slug: Path.GetFileNameWithoutExtension(f.Path),
                Number: int.Parse(f.Match.Groups["number"].Value),
                Title: ReadTitle(f.Path),
                FileName: Path.GetFileName(f.Path)))
            .OrderBy(c => c.Number)
            .ToList();

    /// <summary>
    /// The chapter's rendered HTML, or null when no chapter answers to <paramref name="slug"/> — a
    /// missing chapter is an honest null a controller can turn into a 404 page, not an exception it
    /// has to guard against.
    /// </summary>
    public async Task<string?> ReadAsync(string slug, CancellationToken ct = default)
    {
        var chapter = Chapters().FirstOrDefault(c => c.Slug == slug);
        if (chapter is null) return null;

        var markdown = await File.ReadAllTextAsync(Path.Combine(_chaptersRoot, chapter.FileName), ct);
        return Markdown.ToHtml(markdown, Pipeline);
    }

    /// <summary>
    /// Whether <paramref name="fileName"/> may be served from the image directory: it must end in
    /// <c>.annotated.png</c>, and it must resolve to a location inside that directory. The second
    /// check is the same shape <see cref="PathGuard"/> uses for restore output — a rooted name or one
    /// that climbs out with <c>..</c> is refused before anything is combined into a real path, because
    /// the failure mode is the same one: a name from outside deciding where a read lands.
    /// </summary>
    public bool MayServeImage(string fileName) => Guard(fileName).Allowed;

    /// <summary>
    /// The resolved path to <paramref name="fileName"/> inside the image directory, or null when
    /// <see cref="MayServeImage"/> would refuse it. The controller's only way to turn a requested file
    /// name into a path it may actually read — reusing the same check rather than reconstructing the
    /// path itself, which would be a second place the guard could be gotten wrong.
    /// </summary>
    public string? ResolveImagePath(string fileName)
    {
        var check = Guard(fileName);
        return check.Allowed ? check.ResolvedPath : null;
    }

    private PathCheck Guard(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return PathCheck.Fail(PathRejection.Empty);
        if (!fileName.EndsWith(".annotated.png", StringComparison.Ordinal))
            return PathCheck.Fail(PathRejection.InvalidCharacter);

        return PathGuard.ResolveWithin(_imgRoot, fileName);
    }

    private static string ReadTitle(string path)
    {
        var text = File.ReadAllText(path);
        var match = FirstHeading.Match(text);
        return match.Success ? match.Groups["title"].Value : Path.GetFileNameWithoutExtension(path);
    }
}
