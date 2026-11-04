namespace Harbora.Shared.Inspection;

/// <summary>
/// Reads a source tree and says what it appears to need. Pure over the file system: no network, no
/// process, no database, so it runs identically on the panel and in the CLI and is tested with
/// nothing but a temporary folder.
/// </summary>
public static class SourceInspector
{
    /// <param name="root">An existing directory. A tree it cannot make sense of yields <see cref="SourceInspection.Nothing"/> and a note, never an exception.</param>
    public static SourceInspection Inspect(string root)
    {
        try
        {
            return string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)
                ? Unreadable()
                : Read(new SourceTree(root));
        }
        catch (Exception)
        {
            // Every reader below already turns a bad file into a note. This is the promise itself:
            // the source is a stranger's upload, and whatever it manages to provoke has to come back
            // as "could not read this" on a launch's page rather than as a failed job.
            return Unreadable();
        }
    }

    private static SourceInspection Unreadable() =>
        SourceInspection.Nothing with { Notes = [InspectionNotes.SourceUnreadable()] };

    private static SourceInspection Read(SourceTree tree)
    {
        var located = RootLocator.Locate(tree);
        var severalApps = located.Candidates.Count > 1;

        // With no root chosen everything is judged from the root as given, which is also what the
        // pipeline would build if nobody chose: it is what keeps Stack true in every result.
        var app = located.Directory is null ? tree : tree.Scope(located.Directory);

        var notes = new List<InspectionNote>();
        if (severalApps) notes.Add(InspectionNotes.SeveralAppsFound());

        // Looked for and never opened. There is no code path that reads this file: the example
        // names in ExampleEnvFiles are the only env files the inspector knows.
        if (tree.HasFile(".env") || app.HasFile(".env")) notes.Add(InspectionNotes.EnvFileIgnored());

        var stack = StackDetector.Detect(app);
        notes.AddRange(StackDetector.NotesFor(app, stack, severalApps));

        var example = ExampleEnvFiles.Read(app, out var unreadableExamples);
        var facts = ProjectFacts.Read(app, stack);

        // A compose file starts its own databases, so nothing is proposed beside it, and the names
        // in its example file are its own business rather than something an attachment could set.
        var ownServices = stack != DetectedStack.Compose;
        IReadOnlyList<DetectedService> services = ownServices ? ServiceDetector.Detect(app, facts, example, notes) : [];
        var (envVars, databaseUnclear) = EnvVarClassifier.Classify(example, services, ownServices);
        if (databaseUnclear) notes.Add(InspectionNotes.DatabaseUnclear());

        var (kind, startCommand) = KindDetector.Detect(app, stack, facts);

        foreach (var file in facts.UnreadableFiles.Concat(unreadableExamples))
            notes.Add(InspectionNotes.ManifestUnreadable(file));

        // A Node app that can start never needed the search to decide anything, only to word a note,
        // and an upload that includes node_modules should not be told it is too large for that.
        if (tree.WalkWasCutShort && !(stack == DetectedStack.Node && NodeStartRule.CanStart(app)))
            notes.Add(InspectionNotes.TreeTooLarge());

        return new SourceInspection(
            stack,
            kind,
            located.Directory,
            located.Candidates,
            PortDetector.Detect(app, stack, kind, example, facts),
            startCommand,
            ReleaseCommands.For(app, stack),
            services,
            envVars,
            notes.DistinctBy(n => (n.Code, n.Message)).ToList());
    }
}
