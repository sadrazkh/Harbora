using Harbora.Infrastructure.Learning;

namespace Harbora.Web.ViewModels;

/// <summary>The Learning Centre index: every chapter LearningLibrary found, in reading order.</summary>
public sealed record LearnIndexViewModel(IReadOnlyList<LearningChapter> Chapters);

/// <summary>
/// One rendered chapter, plus the full chapter list so the page can offer a way to the next one
/// without a second read of the directory.
/// </summary>
public sealed record LearnChapterViewModel(LearningChapter Chapter, string Html, IReadOnlyList<LearningChapter> Chapters);
