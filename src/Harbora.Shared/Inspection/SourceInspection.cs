namespace Harbora.Shared.Inspection;

/// <summary>What a source tree builds as. <see cref="Dockerfile"/> outranks a buildpack, as it does in the pipeline.</summary>
public enum DetectedStack
{
    Unknown = 0,
    Dockerfile = 1,
    Compose = 2,
    Node = 3,
    DotNet = 4,
    Go = 5,
    Php = 6,
    Python = 7,
    Static = 8
}

/// <summary>
/// A managed service a source tree appears to need. Its own enum rather than the domain's
/// <c>ManagedServiceType</c>: this project is referenced by the CLI, which has no domain, and the
/// inspector has to run on a developer's machine as well as on the panel.
/// </summary>
public enum DetectedServiceKind
{
    Postgres = 0,
    MySql = 1,
    MariaDb = 2,
    Redis = 3,
    MongoDb = 4,
    RabbitMq = 5,
    Nats = 6,
    Meilisearch = 7
}

public enum DetectedAppKind
{
    Web = 0,

    /// <summary>Long-running with no inbound traffic: a bot or a queue consumer. Gets no domain and no HTTP probe.</summary>
    Worker = 1
}

/// <summary>What one variable named by an example env file means, so the confirm screen asks only what it must.</summary>
public enum EnvVarDisposition
{
    /// <summary>A name an attachment already sets (<c>DATABASE_URL</c>, <c>PGHOST</c>, <c>REDIS_URL</c>, …). Nothing to do.</summary>
    ProvidedByAttach = 0,

    /// <summary>
    /// A different spelling of something an attachment provides (<c>MONGO_URI</c>, <c>DB_HOST</c>).
    /// Written as an alias on the attachment rather than copied, so a password rotation still reaches it.
    /// </summary>
    AliasOfAttach = 1,

    /// <summary>A secret only the app itself reads (<c>JWT_SECRET</c>, <c>SESSION_SECRET</c>). Harbora generates it.</summary>
    GeneratedSecret = 2,

    /// <summary>The example's own value is a usable default (<c>LOG_LEVEL=info</c>).</summary>
    KeepExample = 3,

    /// <summary>Only a person can answer this (<c>TELEGRAM_BOT_TOKEN</c>, <c>STRIPE_SECRET_KEY</c>). The one thing the screen asks.</summary>
    NeedsAnswer = 4
}

/// <summary>Which part of a connection an aliased variable holds. Resolved per engine to the name its attachment really sets.</summary>
public enum EnvAliasRole
{
    Url = 0,
    Host = 1,
    Port = 2,
    User = 3,
    Password = 4,
    Database = 5
}

/// <param name="File">The file it was read from, relative to the chosen root: <see cref="SourceInspection.RootDirectory"/> when there is one, otherwise the inspected root.</param>
/// <param name="Reason">What was found there — <c>"pg" in dependencies</c> — shown beside the proposal so a guess can be checked.</param>
public sealed record ServiceEvidence(string File, string Reason);

/// <param name="Certain">
/// False when the only evidence is weak (a name in an example file, say). The confirm screen leaves
/// such a service unticked: creating a database costs money, and a guess is not consent.
/// </param>
public sealed record DetectedService(DetectedServiceKind Kind, IReadOnlyList<ServiceEvidence> Evidence, bool Certain);

/// <param name="ExampleValue">The value in the example file. Never trusted as a secret, and never set for a <see cref="EnvVarDisposition.GeneratedSecret"/>.</param>
/// <param name="Service">For an attach name or alias: which service it belongs to.</param>
/// <param name="Role">For <see cref="EnvVarDisposition.AliasOfAttach"/>: which part of the connection it holds.</param>
public sealed record DetectedEnvVar(
    string Name,
    EnvVarDisposition Disposition,
    string? ExampleValue,
    DetectedServiceKind? Service,
    EnvAliasRole? Role,
    string SourceFile);

/// <summary>Something the person should know about the guess, in both languages. <paramref name="Code"/> is stable for tests.</summary>
public sealed record InspectionNote(string Code, string Message, string MessageFa);

/// <summary>
/// Everything that can be worked out from a source tree without building or running it.
///
/// <para>
/// A proposal, never a decision: every field reaches a confirm screen the person can change, because
/// a heuristic is wrong some of the time and a wrong guess that nobody saw is the failure this
/// platform keeps paying for.
/// </para>
/// </summary>
/// <param name="RootDirectory">The sub-path the app lives in, or null for the root. Set when the root holds nothing recognisable but one folder does — a monorepo, or a zip with a single top-level folder.</param>
/// <param name="RootCandidates">Every folder that looked like an app when more than one did. The screen offers these; the inspector does not choose.</param>
/// <param name="Port">Null for a <see cref="DetectedAppKind.Worker"/>. An <c>EXPOSE</c> outranks an example file, which outranks the stack's default. <see cref="DetectedStack.Static"/> is always 80, whatever an example file says, because its generated nginx never reads a port.</param>
/// <param name="StartCommand">Set only when the stack's default would be wrong. Null means the image's own.</param>
/// <param name="ReleaseCommand">A migration command this stack's tooling plainly uses, run once before traffic moves.</param>
public sealed record SourceInspection(
    DetectedStack Stack,
    DetectedAppKind Kind,
    string? RootDirectory,
    IReadOnlyList<string> RootCandidates,
    int? Port,
    string? StartCommand,
    string? ReleaseCommand,
    IReadOnlyList<DetectedService> Services,
    IReadOnlyList<DetectedEnvVar> EnvVars,
    IReadOnlyList<InspectionNote> Notes)
{
    public static SourceInspection Nothing { get; } =
        new(DetectedStack.Unknown, DetectedAppKind.Web, null, [], null, null, null, [], [], []);
}
