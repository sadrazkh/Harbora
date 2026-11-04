namespace Harbora.Shared.Inspection;

internal enum Ecosystem
{
    Node,
    Python,
    DotNet,
    Go,
    Php
}

/// <param name="Name">As the project spells it, for the reason shown to the person.</param>
/// <param name="Key">Lower-cased, and in Python's case normalised, for matching.</param>
/// <param name="File">The file it was read from, relative to the judged root.</param>
/// <param name="Section">Where in that file: <c>dependencies</c>, <c>require-dev</c>, <c>requirements.txt</c>.</param>
/// <param name="Runtime">
/// False for what the running app may not need: a <c>devDependencies</c> entry, an optional or peer one.
/// Such a thing is still shown, but it is not enough to tick a database that costs money.
/// </param>
internal sealed record Dependency(Ecosystem Ecosystem, string Name, string Key, string File, string Section, bool Runtime)
{
    public string Reason => $"\"{Name}\" in {Section}";
}

/// <summary>What the project files at the judged root say. Nothing further down is read, which is what makes a decoy under <c>node_modules</c> a non-event.</summary>
internal sealed class ProjectFacts
{
    public List<Dependency> Dependencies { get; } = [];

    /// <summary>Files that are there but said nothing, each worth a note.</summary>
    public List<string> UnreadableFiles { get; } = [];

    /// <summary>The SDK of the project the pipeline would publish.</summary>
    public string? DotNetSdk { get; set; }

    /// <summary>A <c>package.json</c> script or entry point that names a worker, or a worker file at the root.</summary>
    public bool NodeHasWorkerScript { get; set; }

    public bool Has(Ecosystem ecosystem, IReadOnlySet<string> keys) =>
        Dependencies.Any(d => d.Ecosystem == ecosystem && keys.Contains(d.Key));

    public bool HasRuntime(Ecosystem ecosystem, IReadOnlySet<string> keys) =>
        Dependencies.Any(d => d.Ecosystem == ecosystem && d.Runtime && keys.Contains(d.Key));

    /// <summary>
    /// All of a language's files are read whatever the stack turned out to be: a Laravel app builds as
    /// Node because of its <c>package.json</c>, and it needs its database all the same. Only the
    /// recursive search for .NET projects is held back to where they can matter. A compose stack reads
    /// nothing: it brings its own services and runs its own commands.
    /// </summary>
    public static ProjectFacts Read(SourceTree app, DetectedStack stack)
    {
        var facts = new ProjectFacts();
        if (stack == DetectedStack.Compose) return facts;

        NodeManifest.Read(app, facts);
        PythonManifests.Read(app, facts);
        GoManifest.Read(app, facts);
        ComposerManifest.Read(app, facts);
        if (stack is DetectedStack.DotNet or DetectedStack.Dockerfile) DotNetManifest.Read(app, facts);

        return facts;
    }
}
