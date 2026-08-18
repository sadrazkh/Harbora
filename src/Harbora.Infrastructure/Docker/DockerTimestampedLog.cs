using System.Globalization;
using Harbora.Application.Abstractions;

namespace Harbora.Infrastructure.Docker;

/// <summary>
/// Parses the per-line stamp Docker attaches to every line of a container's log stream when the
/// snapshot is requested with <c>Timestamps: true</c> — an RFC3339Nano stamp, a space, then whatever
/// the container wrote. Nothing before the time-window search asked Docker for one: every other
/// reader of a container's output (<see cref="Harbora.Infrastructure.Deployments.LogFilter"/>
/// included) works on a stream that carries no time of its own.
///
/// A line that does not start with a stamp Docker could have written is treated as a continuation of
/// the line before it rather than dropped or thrown on: losing the ability to time-bound a line is a
/// smaller failure than losing the line.
/// </summary>
public static class DockerTimestampedLog
{
    public static IReadOnlyList<TimedLogLine> Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return [];

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var result = new List<TimedLogLine>();

        foreach (var line in lines)
        {
            if (line.Length == 0) continue;

            var space = line.IndexOf(' ');
            var stamp = space > 0 ? line[..space] : line;

            if (space > 0 && DateTimeOffset.TryParse(
                    stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when))
            {
                result.Add(new TimedLogLine(when, line[(space + 1)..]));
            }
            else if (result.Count > 0)
            {
                // No stamp of its own: stays attached to the line before it rather than vanishing.
                var previous = result[^1];
                result[^1] = previous with { Text = previous.Text + "\n" + line };
            }
            // A first line with no parseable stamp has nothing to attach to and is dropped: it cannot
            // be placed in the window it was asked to respect.
        }

        return result;
    }
}
