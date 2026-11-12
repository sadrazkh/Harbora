using Harbora.Shared;
using Spectre.Console;

namespace Harbora.Cli;

/// <summary>
/// What the CLI says about the env files it keeps on this machine, after a push and in
/// <c>harbora doctor</c>. Each one is named, because a build that used to read it now builds without
/// it: Vite, Next.js and Create React App read <c>.env.production</c> at build time. The sentence says
/// where the values go instead, and how to send a file that has to go.
/// </summary>
public static class EnvFileNotice
{
    /// <summary>A monorepo can have an env file in every package. Past this many, a count.</summary>
    private const int Named = 5;

    public static string Kept(IReadOnlyList<string> paths) =>
        $"Kept on this machine: {List(paths)} — env files hold real values. Set what the app needs as its " +
        "variables in the panel; a public name such as VITE_ or NEXT_PUBLIC_ reaches a generated build by itself.";

    /// <summary>How to send one of these anyway, or null when each is <c>.env</c> or <c>.env.local</c>, which never go.</summary>
    public static string? HowToSend(IReadOnlyList<string> paths) =>
        paths.FirstOrDefault(CanBeSent) is { } path
            ? $"To upload {path} anyway, add \"!{path}\" to .dockerignore, or under ignore: in harbora.yml when " +
              "there is no .dockerignore. .env and .env.local never go."
            : null;

    /// <summary>
    /// The end of <c>harbora doctor</c>'s sentence about a Dockerfile that copies an env file the upload
    /// keeps back: that build fails on the missing file, so what to do instead.
    /// </summary>
    public static string ForCopy(string path) =>
        CanBeSent(path)
            ? "an env file holds real values, so it stays on this machine. Set its values as the app's variables " +
              $"and drop the COPY, or send the file by adding \"!{path}\" to .dockerignore."
            : "it never leaves this machine, whatever any ignore file says. Set its values as the app's variables " +
              "and drop the COPY.";

    /// <summary>After a push: nothing when no env file was kept back.</summary>
    public static void Print(IReadOnlyList<SourcePacker.ExcludedEntry> excluded)
    {
        var kept = excluded.Where(e => e.EnvFile).Select(e => e.Path).Order(StringComparer.Ordinal).ToList();
        if (kept.Count == 0) return;

        AnsiConsole.MarkupLine($"[yellow]![/] {Markup.Escape(Kept(kept))}");
        if (HowToSend(kept) is { } how)
            AnsiConsole.MarkupLine($"  [grey]{Markup.Escape(how)}[/]");
    }

    public static bool CanBeSent(string path) =>
        !EnvFiles.NeverUploaded.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    public static string List(IReadOnlyList<string> paths) =>
        paths.Count <= Named
            ? string.Join(", ", paths)
            : $"{string.Join(", ", paths.Take(Named))} and {paths.Count - Named} more";
}
