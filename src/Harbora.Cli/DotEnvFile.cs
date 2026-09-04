using System.Text;

namespace Harbora.Cli;

/// <summary>
/// Reads and writes <c>.env.local</c> — the file <c>harbora env pull</c> fills with an app's
/// effective environment. A plain <c>KEY=VALUE</c> file on purpose: every tool a developer already
/// runs locally (npm, docker compose <c>--env-file</c>, dotenv, direnv) reads this format natively,
/// so nothing else has to change to pick it up.
/// </summary>
public static class DotEnvFile
{
    public const string FileName = ".env.local";

    /// <summary>
    /// Renders the effective environment as a <c>.env.local</c> body. Every secret entry carries a
    /// <c>SECRET</c> comment directly above it, naming where it came from — never folded into the
    /// value itself, so the file stays a plain <c>KEY=VALUE</c> list any dotenv-reading tool can still
    /// parse, and never silently indistinguishable from an ordinary value the way a bare env page
    /// would leave it (the reason this feature exists at all — see the CLI's <c>4.1</c> task brief).
    /// </summary>
    public static string Render(string slug, string server, IReadOnlyList<EffectiveEnvEntry> entries)
    {
        var sb = new StringBuilder();
        sb.Append("# Written by `harbora env pull` for app \"").Append(slug).Append("\" on ").Append(server).Append(".\n");
        sb.Append("# This file can contain real, decrypted secret values — do not commit it.\n");
        sb.Append("# `harbora doctor` warns if .gitignore does not exclude it.\n");
        sb.Append("#\n");
        sb.Append("# A `SECRET` comment above a line means the value came from something encrypted (an env\n");
        sb.Append("# var, a config group, a storage bucket, an email provider, or a database credential) —\n");
        sb.Append("# treat it exactly like a password.\n\n");

        foreach (var e in entries)
        {
            if (e.IsSecret)
                sb.Append("# SECRET (from ").Append(e.Source).Append(")\n");
            sb.Append(e.Key).Append('=').Append(Quote(e.Value)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Quotes a value only when it needs it — an unquoted value is what most dotenv-reading tools
    /// expect for the common case, and quoting every plain hostname or port number would make the
    /// file harder to read for no reason.
    /// </summary>
    private static string Quote(string value)
    {
        var needsQuoting = value.Length == 0 ||
            value.Any(c => c is ' ' or '#' or '"' or '\'' or '\n' or '\r' or '\\') ||
            value != value.Trim();
        if (!needsQuoting) return value;

        var escaped = value
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\r", "")
            .Replace("\n", "\\n");
        return "\"" + escaped + "\"";
    }

    /// <summary>
    /// Unquotes a value exactly the way <see cref="Quote"/> would have written it — the inverse used
    /// by <see cref="Parse"/>.
    /// </summary>
    private static string Unquote(string value)
    {
        if (value.Length < 2 || value[0] != '"' || value[^1] != '"') return value;
        var inner = value[1..^1];
        var sb = new StringBuilder(inner.Length);
        for (var i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '\\' && i + 1 < inner.Length)
            {
                var next = inner[++i];
                sb.Append(next switch { 'n' => '\n', '"' => '"', '\\' => '\\', _ => next });
            }
            else sb.Append(inner[i]);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses a <c>.env.local</c>-shaped file back into <c>KEY → value</c> pairs, for comparing an
    /// existing file against a freshly pulled one. Deliberately only as much of the dotenv format as
    /// <see cref="Render"/> ever writes — comments (including the <c>SECRET</c> markers) and blank
    /// lines are skipped, and a quoted value is unquoted so the comparison is by value, not by
    /// incidental formatting.
    /// </summary>
    public static Dictionary<string, string> Parse(string content)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[line[..eq]] = Unquote(line[(eq + 1)..]);
        }
        return result;
    }

    /// <summary>
    /// What changed between an existing file's content and a freshly rendered one — key names only,
    /// never a value, so a secret that would change is never itself printed to a terminal (and
    /// whatever records that terminal — scrollback, a CI log, a session recording) just to show that a
    /// pull would replace it.
    /// </summary>
    public static IReadOnlyList<string> Diff(string existingContent, string freshContent)
    {
        var before = Parse(existingContent);
        var after = Parse(freshContent);
        var lines = new List<string>();

        foreach (var key in after.Keys.Except(before.Keys).OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"+ {key}");
        foreach (var key in before.Keys.Except(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            lines.Add($"- {key}");
        foreach (var key in before.Keys.Intersect(after.Keys).OrderBy(k => k, StringComparer.Ordinal))
            if (!string.Equals(before[key], after[key], StringComparison.Ordinal))
                lines.Add($"~ {key}");

        return lines;
    }
}
