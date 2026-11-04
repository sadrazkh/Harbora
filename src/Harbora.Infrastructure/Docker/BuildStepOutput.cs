using System.Text.RegularExpressions;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// The last things the build step now running printed, leaving out the builder's own bookkeeping.
/// Starts again at each step, so what it holds when a build fails is what that step had to say.
/// </summary>
internal sealed class BuildStepOutput
{
    private const int MaxLines = 5;
    private const int MaxLineChars = 300;

    /// <summary>The classic builder wraps what a step writes to stderr in colour codes, and those are the lines that matter.</summary>
    private static readonly Regex Colour = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly Queue<string> _lines = new(MaxLines);

    /// <param name="message">One message from the daemon's stream, which can hold several lines.</param>
    public void Note(string message)
    {
        foreach (var raw in Colour.Replace(message, "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (DockerEngine.IsStepLine(line))
            {
                _lines.Clear();
                continue;
            }

            if (IsBuilderChatter(line)) continue;

            if (_lines.Count == MaxLines) _lines.Dequeue();
            _lines.Enqueue(line.Length <= MaxLineChars ? line : line[..MaxLineChars] + "…");
        }
    }

    /// <summary>Null when the step printed nothing of its own.</summary>
    public string? Said => _lines.Count == 0 ? null : string.Join(" | ", _lines);

    /// <summary>
    /// Lines that are nobody's reason. The classic builder's own around a step's output (<c>---&gt; Running
    /// in 1a2b</c>, and what it removes after); npm's echo of the command it was told to run, which only
    /// repeats the step; and its advice to upgrade, five lines long and printed last, which would otherwise
    /// be all that was left of a failed install.
    /// </summary>
    private static bool IsBuilderChatter(string line) =>
        line.StartsWith("--->", StringComparison.Ordinal) ||
        line.StartsWith("Removing intermediate container", StringComparison.Ordinal) ||
        line.StartsWith("> ", StringComparison.Ordinal) ||
        line.StartsWith("npm notice", StringComparison.Ordinal);
}
