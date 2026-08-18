using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Deployments;

/// <summary>
/// Finding the line that matters in a wall of container output.
///
/// The logs screen showed a tail and nothing else, which is fine until something goes wrong — and
/// then the one line that explains it is somewhere in four hundred, and the browser's own find only
/// searches what happens to be on screen. Filtering happens here, on the whole tail, before it is
/// sent.
///
/// Deliberately plain text matching rather than a regular expression: a mistyped pattern silently
/// matching nothing is worse than no filter at all, and nobody debugging an outage wants to be
/// debugging their regex too.
/// </summary>
public static class LogFilter
{
    /// <summary>Words that mark a line worth looking at first, whatever the application calls itself.</summary>
    private static readonly string[] ProblemWords =
        ["error", "fail", "fatal", "exception", "panic", "critical", "warn"];

    /// <summary>
    /// Keeps the lines that match, in order.
    ///
    /// <paramref name="onlyProblems"/> is the one-click version of what people type by hand, and it
    /// keeps the surrounding blank-free structure: a stack trace's indented continuation lines stay
    /// with the line that introduced them, because a message without its trace explains nothing.
    /// </summary>
    public static IReadOnlyList<string> Apply(string? text, string? search, bool onlyProblems)
    {
        if (string.IsNullOrEmpty(text)) return [];

        var lines = text.Replace("\r\n", "\n").Split('\n');
        return KeptIndexes(lines.Length, i => lines[i], search, onlyProblems).Select(i => lines[i]).ToList();
    }

    /// <summary>
    /// The same rule <see cref="Apply"/> states, kept for lines that already carry their own moment —
    /// a time-window search's lines, timestamped by Docker itself, rather than a plain tail with none.
    /// A continuation line joins the group of the line that introduced it, so a stack trace's frames
    /// carry the timestamp of the error they belong to, not one of their own.
    /// </summary>
    public static IReadOnlyList<TimedLogLine> ApplyTimed(
        IReadOnlyList<TimedLogLine> lines, string? search, bool onlyProblems)
    {
        if (lines.Count == 0) return [];

        return KeptIndexes(lines.Count, i => lines[i].Text, search, onlyProblems)
            .Select(i => lines[i]).ToList();
    }

    /// <summary>
    /// The grouping rule both overloads share, worked out once over anything indexable: a blank line
    /// ends a group, a line matching the filter starts one and keeps its continuation lines (those
    /// that begin with whitespace), and nothing widens what the filter alone would have kept.
    /// </summary>
    private static List<int> KeptIndexes(int count, Func<int, string> textAt, string? search, bool onlyProblems)
    {
        var wanted = new List<int>();
        var keepingContinuation = false;

        for (var i = 0; i < count; i++)
        {
            var line = textAt(i);
            if (line.Length == 0) { keepingContinuation = false; continue; }

            var isContinuation = char.IsWhiteSpace(line[0]);
            if (isContinuation && keepingContinuation)
            {
                wanted.Add(i);
                continue;
            }

            var matches = Matches(line, search, onlyProblems);
            keepingContinuation = matches && !isContinuation;
            if (matches) wanted.Add(i);
        }

        return wanted;
    }

    /// <summary>
    /// Whether a line is one of the interesting ones.
    ///
    /// Matched at the start of a word, not anywhere in it: "terror" contains "error", and a filter
    /// that fires on ordinary prose is one people stop trusting after the first time. Endings are
    /// deliberately still matched, so "failed", "errors" and "warning" all count.
    /// </summary>
    public static bool IsProblem(string line)
    {
        foreach (var word in ProblemWords)
        {
            var at = 0;
            while ((at = line.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                if (at == 0 || !char.IsLetter(line[at - 1])) return true;
                at++;
            }
        }

        return false;
    }

    private static bool Matches(string line, string? search, bool onlyProblems)
    {
        if (onlyProblems && !IsProblem(line)) return false;

        return string.IsNullOrWhiteSpace(search)
            || line.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A filename for a download. Stamped, because the second thing anyone does with a log file is
    /// send it to someone, and "logs.txt" three times in a folder helps nobody.
    ///
    /// InvariantCulture because this goes into a FILENAME: the panel's default culture is Persian,
    /// so the ambient calendar writes a Jalali year — "14050509" for what everything else calls
    /// 2026-07-31. Observed on the live server; the same trap the backup artifact names already
    /// carry a note about. Unit tests do not catch it, because the test runner's culture is
    /// invariant already.
    /// </summary>
    public static string FileName(string slug, DateTimeOffset when) =>
        $"{slug}-logs-{when.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}.txt";
}
