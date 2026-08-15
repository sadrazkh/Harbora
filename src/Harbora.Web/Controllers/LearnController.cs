using Harbora.Infrastructure.Learning;
using Harbora.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Harbora.Web.Controllers;

/// <summary>
/// The Learning Centre: the nine tutorial chapters in <c>docs/tutorial</c>, rendered on request. Every
/// disk-reading and image-guard rule lives in <see cref="LearningLibrary"/> — this controller only
/// turns its answers into HTTP: a chapter that exists becomes a page, one that does not becomes a 404
/// that still offers the index, and an image <see cref="LearningLibrary.MayServeImage"/> refuses
/// becomes a 404 rather than a 403 that would confirm the file is there.
/// </summary>
[Authorize]
public sealed class LearnController(LearningLibrary library) : Controller
{
    [HttpGet("/learn")]
    public IActionResult Index() => View(new LearnIndexViewModel(library.Chapters()));

    /// <summary>
    /// Matched ahead of <see cref="Chapter"/> by ASP.NET Core's own route precedence — a literal
    /// segment ("img") outranks a parameter ("{slug}") regardless of registration order — so
    /// <c>/learn/img/x.png</c> never reaches the chapter action.
    /// </summary>
    [HttpGet("/learn/img/{file}")]
    public IActionResult Image(string file)
    {
        var path = library.ResolveImagePath(file);
        // 404, not 403: a refusal that says "forbidden" confirms a file by that name exists to be
        // forbidden. A raw capture living only in a developer's working directory must read exactly
        // like a name nobody ever wrote — the same shape NotFound() already gives a missing artefact
        // elsewhere in this controller layer (NodeArtifactController.DownloadChunk).
        return path is null ? NotFound() : PhysicalFile(path, "image/png");
    }

    [HttpGet("/learn/{slug}")]
    public async Task<IActionResult> Chapter(string slug, CancellationToken ct)
    {
        var chapters = library.Chapters();
        var chapter = chapters.FirstOrDefault(c => c.Slug == slug);
        var html = chapter is null ? null : await library.ReadAsync(slug, ct);

        if (chapter is null || html is null)
        {
            // Not an exception: ReadAsync already answers null for a chapter that does not exist
            // rather than throwing, and this is that null turned into a page instead of one letting a
            // NullReferenceException reach the generic error handler. Response.StatusCode is set
            // explicitly (rather than returning NotFound()) because this view has a body — the ASP.NET
            // Core status-code re-execute middleware only takes over an empty one, and the whole point
            // here is a page offering the index rather than the platform's generic "go to dashboard".
            Response.StatusCode = StatusCodes.Status404NotFound;
            return View("ChapterNotFound");
        }

        return View(new LearnChapterViewModel(chapter, html, chapters));
    }
}
